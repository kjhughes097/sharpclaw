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

/// <summary>
/// <c>POST /v1/chat/completions</c>: validate, check the budget, stream from the provider as
/// OpenAI-style SSE chunks built by the gateway, tee deltas to the thread, and record the call.
/// </summary>
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
        var ct = http.RequestAborted;

        if (!TryHeaderGuid(http, "X-Correlation-Id", out var correlationId))
            return ApiResults.ValidationFailed("X-Correlation-Id header is required and must be a GUID.");
        if (!TryHeaderGuid(http, "X-Thread-Id", out var threadId))
            return ApiResults.ValidationFailed("X-Thread-Id header is required and must be a GUID.");

        var purposeHeader = http.Request.Headers["X-SharpClaw-Purpose"].ToString();
        var purpose = string.IsNullOrEmpty(purposeHeader) ? LlmCallPurpose.Chat : purposeHeader;
        if (purpose is not (LlmCallPurpose.Chat or LlmCallPurpose.RiskAssessment))
            return ApiResults.ValidationFailed("X-SharpClaw-Purpose must be 'chat' or 'risk_assessment'.");

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["thread_id"] = threadId.ToString(),
            ["correlation_id"] = correlationId.ToString(),
        });

        var ownershipError = await CheckOwnershipAsync(db, agent, threadId, correlationId, ct);
        if (ownershipError is not null)
            return ownershipError;

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
            var (parsed, parseError) = ChatRequestParser.Parse(bodyDoc.RootElement);
            if (parseError is not null)
                return parseError;

            var now = time.GetUtcNow().UtcDateTime;
            // Claim heartbeat: a multi-round tool turn must not be requeued as if the agent had died.
            await InboxRepository.TouchClaimAsync(db, agent.Id, correlationId, now, ct);
            var recorder = new LlmCallRecorder(db, metrics, logger, agent, threadId, correlationId, now, purpose);

            var spent = await AgentQueries.TodaySpendAsync(db, time, agent.Id, ct);
            if (spent >= agent.DailyBudgetUsd)
            {
                await recorder.RecordAsync(LlmCallOutcome.Rejected(ErrorCodes.BudgetExceeded), ct);
                logger.LogWarning("llm call rejected: budget_exceeded spent_usd={SpentUsd} budget_usd={BudgetUsd}",
                    spent, agent.DailyBudgetUsd);
                metrics.RecordBudgetExceeded(agent.Name);
                return BudgetExceeded(now);
            }

            // Risk-assessment calls are internal to the agent: never show their output to the user.
            var teeToThread = purpose == LlmCallPurpose.Chat;
            var stream = new ChatStream(http.Response, bus, recorder, logger, agent.Model, threadId, correlationId, teeToThread);
            return await stream.RunAsync(provider, parsed!.ToProviderRequest(agent.Model), ct);
        }
    }

    /// <summary>The thread and the claimed user message must both belong to this agent (else 404).</summary>
    private static async Task<IResult?> CheckOwnershipAsync(
        SharpClawDbContext db, AuthenticatedAgent agent, Guid threadId, Guid correlationId, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == threadId, ct);
        if (thread is null || thread.AgentId != agent.Id)
            return ApiResults.NotFound("Thread not found.");

        var userMsg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == correlationId, ct);
        if (userMsg is null || userMsg.AgentId != agent.Id || userMsg.ThreadId != threadId ||
            userMsg.Role != Roles.User || userMsg.Status != MessageStatus.Claimed)
            return ApiResults.NotFound("Correlation message not found.");

        return null;
    }

    private static IResult BudgetExceeded(DateTime now)
    {
        var reset = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
        var retryAfter = (int)Math.Ceiling((reset - now).TotalSeconds);
        return ApiResults.Error(429, ErrorCodes.BudgetExceeded,
            $"Daily budget exhausted. Resets at {reset:yyyy-MM-ddTHH:mm:ssZ}.",
            headers => headers["Retry-After"] = retryAfter.ToString());
    }

    private static bool TryHeaderGuid(HttpContext http, string name, out Guid value)
    {
        value = Guid.Empty;
        var raw = http.Request.Headers[name].ToString();
        return !string.IsNullOrEmpty(raw) && Guid.TryParse(raw, out value);
    }

    /// <summary>
    /// One streamed completion. Holds the per-call state (tokens, timings) so the streaming steps
    /// don't need to pass it around, and decides how each failure mode is recorded.
    /// </summary>
    private sealed class ChatStream
    {
        private readonly HttpResponse _response;
        private readonly IThreadEventBus _bus;
        private readonly LlmCallRecorder _recorder;
        private readonly ILogger _logger;
        private readonly Guid _threadId;
        private readonly Guid _correlationId;
        private readonly ChatChunkWriter _writer;
        private readonly bool _teeToThread;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly List<ChatStreamEvent.ToolCall> _toolCalls = new();

        private int _promptTokens;
        private int _completionTokens;
        private double _costUsd;
        private bool _usageKnown;
        private int? _ttftMs;
        private string _finishReason = "stop";

        public ChatStream(HttpResponse response, IThreadEventBus bus, LlmCallRecorder recorder, ILogger logger,
            string model, Guid threadId, Guid correlationId, bool teeToThread)
        {
            _teeToThread = teeToThread;
            _response = response;
            _bus = bus;
            _recorder = recorder;
            _logger = logger;
            _threadId = threadId;
            _correlationId = correlationId;
            _writer = new ChatChunkWriter(model);
        }

        private int ElapsedMs => (int)_sw.ElapsedMilliseconds;

        public async Task<IResult> RunAsync(IChatProvider provider, ProviderChatRequest request, CancellationToken ct)
        {
            await using var events = provider.StreamAsync(request, ct).GetAsyncEnumerator(ct);

            // Before the first event we can still answer with a proper HTTP error status.
            bool hasEvent;
            try
            {
                hasEvent = await events.MoveNextAsync();
            }
            catch (ProviderException pex)
            {
                await _recorder.RecordAsync(Failed(pex), ct);
                return pex.Kind == ProviderErrorKind.Timeout
                    ? ApiResults.Error(504, ErrorCodes.ProviderTimeout, "Upstream timed out.")
                    : ApiResults.Error(502, ErrorCodes.ProviderError, "Upstream failure.");
            }
            catch (OperationCanceledException)
            {
                await _recorder.RecordAsync(LlmCallOutcome.Cancelled(ElapsedMs), CancellationToken.None);
                return Results.Empty;
            }

            _response.StatusCode = 200;
            _response.Headers.ContentType = "text/event-stream";
            _response.Headers.CacheControl = "no-cache";
            await WriteAsync(_writer.RoleChunk(), ct);

            // After streaming starts the status code is fixed; failures become an error chunk.
            try
            {
                while (hasEvent)
                {
                    await HandleAsync(events.Current, ct);
                    hasEvent = await events.MoveNextAsync();
                }
            }
            catch (ProviderException pex)
            {
                await WriteAsync(ChatChunkWriter.ErrorChunk(ErrorCodes.ProviderError, "Upstream failure."), ct);
                await WriteAsync(ChatChunkWriter.DoneLine, ct);
                await _recorder.RecordAsync(Failed(pex), ct);
                return Results.Empty;
            }
            catch (OperationCanceledException)
            {
                // The upstream request is cancelled with the request token; usage is unknown.
                await _recorder.RecordAsync(LlmCallOutcome.Cancelled(ElapsedMs, _ttftMs), CancellationToken.None);
                return Results.Empty;
            }

            await WriteAsync(_writer.FinishChunk(_finishReason), ct);
            if (_usageKnown)
                await WriteAsync(_writer.UsageChunk(_promptTokens, _completionTokens), ct);
            else
                _logger.LogWarning("Provider reported no usage for correlation {CorrelationId}", _correlationId);
            await WriteAsync(ChatChunkWriter.DoneLine, ct);

            await _recorder.RecordAsync(Succeeded(), ct, _toolCalls);
            return Results.Empty;
        }

        private async Task HandleAsync(ChatStreamEvent ev, CancellationToken ct)
        {
            switch (ev)
            {
                case ChatStreamEvent.Delta d:
                    _ttftMs ??= ElapsedMs;
                    await WriteAsync(_writer.ContentChunk(d.Text), ct);
                    Publish(SseEventNames.Delta, new DeltaEvent(_correlationId, d.Text));
                    break;
                case ChatStreamEvent.ToolCall call:
                    _ttftMs ??= ElapsedMs;
                    await WriteAsync(_writer.ToolCallChunk(_toolCalls.Count, call.Id, call.Name, call.ArgumentsJson), ct);
                    _toolCalls.Add(call);
                    // Only the tool name is published: arguments may contain content.
                    Publish(SseEventNames.ToolRequested, new ToolRequestedEvent(_correlationId, call.Id, call.Name));
                    break;
                case ChatStreamEvent.Usage u:
                    _promptTokens = u.PromptTokens;
                    _completionTokens = u.CompletionTokens;
                    _costUsd = u.CostUsd ?? 0;
                    _usageKnown = true;
                    break;
                case ChatStreamEvent.Done done:
                    _finishReason = done.FinishReason;
                    break;
            }
        }

        private void Publish<T>(string eventName, T payload)
        {
            if (_teeToThread)
                _bus.Publish(_threadId, new ThreadSseEvent(eventName, JsonSerializer.Serialize(payload, SharpClawJson.Options)));
        }

        private LlmCallOutcome Succeeded() => new()
        {
            Status = LlmCallStatus.Ok,
            PromptTokens = _promptTokens,
            CompletionTokens = _completionTokens,
            CostUsd = _costUsd,
            UsageKnown = _usageKnown,
            LatencyMs = ElapsedMs,
            TtftMs = _ttftMs,
        };

        private LlmCallOutcome Failed(ProviderException pex) => Succeeded() with
        {
            Status = LlmCallStatus.Error,
            ErrorCode = pex.Kind == ProviderErrorKind.Timeout ? ErrorCodes.ProviderTimeout : ErrorCodes.ProviderError,
            ErrorDetail = pex.Message,
        };

        private async Task WriteAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _response.Body.WriteAsync(bytes, ct);
            await _response.Body.FlushAsync(ct);
        }
    }
}
