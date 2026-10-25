using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Providers;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Endpoints;

public static class ChatEndpoints
{
    public static void Map(RouteGroupBuilder v1)
    {
        v1.MapPost("/chat/completions", Completions);
    }

    private static async Task<IResult> Completions(
        HttpContext http, SharpClawDbContext db, TimeProvider time,
        IChatProvider provider, IThreadEventBus bus, GatewayMetrics metrics,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Chat");
        var agent = AuthContext.Agent(http);

        // Required headers.
        if (!TryHeaderGuid(http, "X-Correlation-Id", out var correlationId))
            return ApiResults.ValidationFailed("X-Correlation-Id header is required and must be a GUID.");
        if (!TryHeaderGuid(http, "X-Thread-Id", out var threadId))
            return ApiResults.ValidationFailed("X-Thread-Id header is required and must be a GUID.");

        var ct = http.RequestAborted;

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["thread_id"] = threadId.ToString(),
            ["correlation_id"] = correlationId.ToString(),
        });

        // Ownership: thread and claimed message must belong to this agent.
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null || thread.AgentId != agent.Id)
            return ApiResults.NotFound("Thread not found.");
        var userMsg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == correlationId, ct);
        if (userMsg is null || userMsg.AgentId != agent.Id || userMsg.ThreadId != threadId ||
            userMsg.Role != Roles.User || userMsg.Status != MessageStatus.Claimed)
            return ApiResults.NotFound("Correlation message not found.");

        // Parse and validate body.
        JsonDocument bodyDoc;
        try
        {
            bodyDoc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return ApiResults.ValidationFailed("Request body must be valid JSON.");
        }
        using (bodyDoc)
        {
            var parseError = ParseRequest(bodyDoc.RootElement, out var parsed);
            if (parseError is not null)
                return parseError;

            var now = time.GetUtcNow().UtcDateTime;

            // Budget check.
            var spent = await AgentQueries.TodaySpendAsync(db, time, agent.Id, ct);
            if (spent >= agent.DailyBudgetUsd)
            {
                await RecordRejected(db, agent, threadId, correlationId, now, ct);
                logger.LogWarning("llm call rejected: budget_exceeded spent_usd={SpentUsd} budget_usd={BudgetUsd}",
                    spent, agent.DailyBudgetUsd);
                metrics.RecordBudgetExceeded(agent.Name);
                metrics.RecordCall(agent.Name, agent.Model, LlmCallStatus.Rejected);
                var reset = NextUtcMidnight(now);
                var retryAfter = (int)Math.Ceiling((reset - now).TotalSeconds);
                return ApiResults.Error(429, ErrorCodes.BudgetExceeded,
                    $"Daily budget exhausted. Resets at {reset:yyyy-MM-ddTHH:mm:ssZ}.",
                    headers => headers["Retry-After"] = retryAfter.ToString());
            }

            var providerRequest = new ProviderChatRequest(
                agent.Model, parsed!.Messages, parsed.Temperature, parsed.MaxTokens, parsed.TopP, parsed.Stop);

            return await StreamAsync(
                http, db, time, provider, bus, metrics, logger,
                agent, threadId, correlationId, providerRequest, now, ct);
        }
    }

    private static async Task<IResult> StreamAsync(
        HttpContext http, SharpClawDbContext db, TimeProvider time, IChatProvider provider,
        IThreadEventBus bus, GatewayMetrics metrics, ILogger logger,
        AuthenticatedAgent agent, Guid threadId, Guid correlationId,
        ProviderChatRequest request, DateTime startedAt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var writer = new ChatChunkWriter(agent.Model);

        var enumerator = provider.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        bool hasEvent;
        try
        {
            hasEvent = await enumerator.MoveNextAsync();
        }
        catch (ProviderException pex)
        {
            await enumerator.DisposeAsync();
            await FinalizeCall(db, metrics, logger, agent, threadId, correlationId, startedAt,
                LlmCallStatus.Error, 0, 0, 0, usageKnown: false, (int)sw.ElapsedMilliseconds, null,
                ErrorCodeFor(pex), pex.Message, ct);
            return pex.Kind == ProviderErrorKind.Timeout
                ? ApiResults.Error(504, ErrorCodes.ProviderTimeout, "Upstream timed out.")
                : ApiResults.Error(502, ErrorCodes.ProviderError, "Upstream failure.");
        }
        catch (OperationCanceledException)
        {
            await enumerator.DisposeAsync();
            await FinalizeCall(db, metrics, logger, agent, threadId, correlationId, startedAt,
                LlmCallStatus.Cancelled, 0, 0, 0, usageKnown: false, (int)sw.ElapsedMilliseconds, null,
                null, null, CancellationToken.None);
            return Results.Empty;
        }

        // Begin the SSE response.
        var response = http.Response;
        response.StatusCode = 200;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        int promptTokens = 0, completionTokens = 0;
        double costUsd = 0;
        bool usageKnown = false;
        int? ttftMs = null;
        var finishReason = "stop";

        await WriteAsync(response, writer.RoleChunk(), ct);

        try
        {
            while (hasEvent)
            {
                var ev = enumerator.Current;
                switch (ev)
                {
                    case ChatStreamEvent.Delta d:
                        ttftMs ??= (int)sw.ElapsedMilliseconds;
                        await WriteAsync(response, writer.ContentChunk(d.Text), ct);
                        bus.Publish(threadId, new ThreadSseEvent(
                            SseEventNames.Delta,
                            JsonSerializer.Serialize(new DeltaEvent(correlationId, d.Text), SharpClawJson.Options)));
                        break;
                    case ChatStreamEvent.Usage u:
                        promptTokens = u.PromptTokens;
                        completionTokens = u.CompletionTokens;
                        costUsd = u.CostUsd ?? 0;
                        usageKnown = true;
                        break;
                    case ChatStreamEvent.Done done:
                        finishReason = done.FinishReason;
                        break;
                }

                hasEvent = await enumerator.MoveNextAsync();
            }
        }
        catch (ProviderException pex)
        {
            await WriteAsync(response, ChatChunkWriter.ErrorChunk(ErrorCodes.ProviderError, "Upstream failure."), ct);
            await WriteAsync(response, ChatChunkWriter.DoneLine, ct);
            await enumerator.DisposeAsync();
            await FinalizeCall(db, metrics, logger, agent, threadId, correlationId, startedAt,
                LlmCallStatus.Error, promptTokens, completionTokens, usageKnown ? costUsd : 0, usageKnown,
                (int)sw.ElapsedMilliseconds, ttftMs, ErrorCodeFor(pex), pex.Message, ct);
            return Results.Empty;
        }
        catch (OperationCanceledException)
        {
            await enumerator.DisposeAsync();
            await FinalizeCall(db, metrics, logger, agent, threadId, correlationId, startedAt,
                LlmCallStatus.Cancelled, 0, 0, 0, usageKnown: false, (int)sw.ElapsedMilliseconds, ttftMs,
                null, null, CancellationToken.None);
            return Results.Empty;
        }

        await enumerator.DisposeAsync();

        await WriteAsync(response, writer.FinishChunk(finishReason), ct);
        if (usageKnown)
            await WriteAsync(response, writer.UsageChunk(promptTokens, completionTokens), ct);
        else
            logger.LogWarning("Provider reported no usage for correlation {CorrelationId}", correlationId);
        await WriteAsync(response, ChatChunkWriter.DoneLine, ct);

        await FinalizeCall(db, metrics, logger, agent, threadId, correlationId, startedAt,
            LlmCallStatus.Ok, promptTokens, completionTokens, usageKnown ? costUsd : 0, usageKnown,
            (int)sw.ElapsedMilliseconds, ttftMs, null, null, ct);

        return Results.Empty;
    }

    private static async Task RecordRejected(
        SharpClawDbContext db, AuthenticatedAgent agent, Guid threadId, Guid correlationId,
        DateTime now, CancellationToken ct)
    {
        db.LlmCalls.Add(new LlmCall
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            ThreadId = threadId,
            CorrelationId = correlationId,
            Model = agent.Model,
            Status = LlmCallStatus.Rejected,
            UsageKnown = false,
            ErrorCode = ErrorCodes.BudgetExceeded,
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    private static async Task FinalizeCall(
        SharpClawDbContext db, GatewayMetrics metrics, ILogger logger, AuthenticatedAgent agent,
        Guid threadId, Guid correlationId, DateTime startedAt, string status,
        int promptTokens, int completionTokens, double costUsd, bool usageKnown,
        int latencyMs, int? ttftMs, string? errorCode, string? errorDetail, CancellationToken ct)
    {
        db.LlmCalls.Add(new LlmCall
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            ThreadId = threadId,
            CorrelationId = correlationId,
            Model = agent.Model,
            Status = status,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            CostUsd = costUsd,
            UsageKnown = usageKnown,
            LatencyMs = latencyMs,
            TtftMs = ttftMs,
            ErrorCode = errorCode,
            ErrorDetail = Truncate(errorDetail, 200),
            CreatedAt = startedAt,
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "llm call finished: status={Status} model={Model} prompt_tokens={PromptTokens} completion_tokens={CompletionTokens} cost_usd={CostUsd} usage_known={UsageKnown} latency_ms={LatencyMs} ttft_ms={TtftMs} error_code={ErrorCode}",
            status, agent.Model, promptTokens, completionTokens, costUsd, usageKnown, latencyMs, ttftMs, errorCode);

        metrics.RecordCall(agent.Name, agent.Model, status);
        metrics.RecordTokens(agent.Name, agent.Model, promptTokens, completionTokens);
        metrics.RecordCost(agent.Name, agent.Model, costUsd);
        metrics.RecordDuration(agent.Name, agent.Model, latencyMs / 1000.0);
        if (ttftMs is { } t)
            metrics.RecordTtft(agent.Name, agent.Model, t / 1000.0);
    }

    private static IResult? ParseRequest(JsonElement root, out ParsedChatRequest? parsed)
    {
        parsed = null;

        if (root.ValueKind != JsonValueKind.Object)
            return ApiResults.ValidationFailed("Body must be a JSON object.");

        if (root.TryGetProperty("tools", out _) || root.TryGetProperty("tool_choice", out _))
            return ApiResults.Unsupported("tools are not supported in this phase.");

        if (!root.TryGetProperty("stream", out var stream) ||
            stream.ValueKind != JsonValueKind.True)
            return ApiResults.Unsupported("Only stream:true is supported.");

        if (!root.TryGetProperty("messages", out var messagesEl) ||
            messagesEl.ValueKind != JsonValueKind.Array)
            return ApiResults.ValidationFailed("messages is required.");

        var messages = new List<ProviderChatMessage>();
        foreach (var m in messagesEl.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object ||
                !m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                return ApiResults.ValidationFailed("each message needs a role.");
            var role = roleEl.GetString()!;
            if (role is not (Roles.System or Roles.User or Roles.Assistant))
                return ApiResults.ValidationFailed($"unsupported role '{role}'.");

            if (!m.TryGetProperty("content", out var contentEl))
                return ApiResults.ValidationFailed("each message needs content.");
            if (contentEl.ValueKind != JsonValueKind.String)
                return ApiResults.Unsupported("only string message content is supported.");

            messages.Add(new ProviderChatMessage(role, contentEl.GetString() ?? ""));
        }
        if (messages.Count == 0)
            return ApiResults.ValidationFailed("messages must be non-empty.");

        double? temperature = TryNumber(root, "temperature");
        int? maxTokens = TryInt(root, "max_tokens");
        double? topP = TryNumber(root, "top_p");

        List<string>? stop = null;
        if (root.TryGetProperty("stop", out var stopEl))
        {
            if (stopEl.ValueKind == JsonValueKind.String)
                stop = new List<string> { stopEl.GetString()! };
            else if (stopEl.ValueKind == JsonValueKind.Array)
                stop = stopEl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToList();
        }

        parsed = new ParsedChatRequest(messages, temperature, maxTokens, topP, stop);
        return null;
    }

    private sealed record ParsedChatRequest(
        List<ProviderChatMessage> Messages, double? Temperature, int? MaxTokens, double? TopP, List<string>? Stop);

    private static double? TryNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

    private static int? TryInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static bool TryHeaderGuid(HttpContext http, string name, out Guid value)
    {
        value = Guid.Empty;
        var raw = http.Request.Headers[name].ToString();
        return !string.IsNullOrEmpty(raw) && Guid.TryParse(raw, out value);
    }

    private static string ErrorCodeFor(ProviderException pex) => pex.Kind switch
    {
        ProviderErrorKind.Timeout => ErrorCodes.ProviderTimeout,
        _ => ErrorCodes.ProviderError,
    };

    private static DateTime NextUtcMidnight(DateTime now) =>
        new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.Body.WriteAsync(bytes, ct);
        await response.Body.FlushAsync(ct);
    }
}
