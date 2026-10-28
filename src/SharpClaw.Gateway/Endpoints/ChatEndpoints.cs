using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Providers;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Services.Chat;
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

    /// <summary>What a completion request needs from DI, grouped so the handler reads as its steps.</summary>
    public sealed record ChatServices(
        SharpClawDbContext Db, TimeProvider Time, IChatProvider Provider, IEventPublisher Events,
        ActiveTurnBuffers Buffers, GatewayMetrics Metrics, IOptions<ChatOptions> Chat,
        IOptions<ContextOptions> Context, ILoggerFactory LoggerFactory);

    private static async Task<IResult> Completions(HttpContext http, [AsParameters] ChatServices svc)
    {
        var logger = svc.LoggerFactory.CreateLogger("SharpClaw.Gateway.Chat");
        var agent = AuthContext.Agent(http);
        var db = svc.Db;
        var ct = http.RequestAborted;

        if (!TryHeaderGuid(http, "X-Correlation-Id", out var correlationId))
            return ApiResults.ValidationFailed("X-Correlation-Id header is required and must be a GUID.");
        if (!TryHeaderGuid(http, "X-Thread-Id", out var threadId))
            return ApiResults.ValidationFailed("X-Thread-Id header is required and must be a GUID.");

        var purposeHeader = http.Request.Headers["X-SharpClaw-Purpose"].ToString();
        var purpose = string.IsNullOrEmpty(purposeHeader) ? LlmCallPurpose.Chat : purposeHeader;
        if (purpose is not (LlmCallPurpose.Chat or LlmCallPurpose.RiskAssessment))
            return ApiResults.ValidationFailed("X-SharpClaw-Purpose must be 'chat' or 'risk_assessment'.");

        using var scope = LogScopes.Turn(logger, threadId, correlationId);

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

            var now = svc.Time.GetUtcNow().UtcDateTime;
            // Claim heartbeat: a multi-round tool turn must not be requeued as if the agent had died.
            await InboxRepository.TouchClaimAsync(db, agent.Id, correlationId, now, ct);
            var recorder = new LlmCallRecorder(db, svc.Metrics, logger, agent, threadId, correlationId, now, purpose)
            {
                EstimatedPromptTokens = EstimatePrompt(parsed!, bodyDoc.RootElement, svc.Context.Value),
            };

            if (await CheckLimitsAsync(svc, agent, correlationId, recorder, logger, now, ct) is { } rejected)
                return rejected;

            // Risk-assessment calls are internal to the agent: never show their output to the user.
            var teeToThread = purpose == LlmCallPurpose.Chat;
            var stream = new ChatStream(http.Response, svc.Events, svc.Buffers, recorder, logger, agent.Model,
                threadId, correlationId, teeToThread);
            return await stream.RunAsync(svc.Provider, parsed!.ToProviderRequest(agent.Model), ct);
        }
    }

    /// <summary>
    /// The guards in front of the provider: calls per message, the daily budget, and the context
    /// window. A rejected call is still recorded in <c>llm_calls</c>. Null means go ahead.
    /// </summary>
    private static async Task<IResult?> CheckLimitsAsync(
        ChatServices svc, AuthenticatedAgent agent, Guid correlationId, LlmCallRecorder recorder, ILogger logger,
        DateTime now, CancellationToken ct)
    {
        async Task<IResult> Reject(string code, IResult result)
        {
            await recorder.RecordAsync(LlmCallOutcome.Rejected(code), ct);
            return result;
        }

        var maxCalls = svc.Chat.Value.MaxLlmCallsPerMessage;
        var callsSoFar = await svc.Db.LlmCalls.CountAsync(c => c.CorrelationId == correlationId, ct);
        if (callsSoFar >= maxCalls)
        {
            logger.LogWarning("llm call rejected: loop_limit_exceeded calls={Calls} max={Max}", callsSoFar, maxCalls);
            return await Reject(ErrorCodes.LoopLimitExceeded, ApiResults.Error(429, ErrorCodes.LoopLimitExceeded,
                $"This message has reached the limit of {maxCalls} LLM calls."));
        }

        var spent = await AgentQueries.TodaySpendAsync(svc.Db, svc.Time, agent.Id, ct);
        if (spent >= agent.DailyBudgetUsd)
        {
            logger.LogWarning("llm call rejected: budget_exceeded spent_usd={SpentUsd} budget_usd={BudgetUsd}",
                spent, agent.DailyBudgetUsd);
            svc.Metrics.RecordBudgetExceeded(agent.Name);
            return await Reject(ErrorCodes.BudgetExceeded, BudgetExceeded(now));
        }

        // Preflight: a request that can't fit the window would only fail (and cost) upstream.
        var context = svc.Context.Value;
        var limit = context.WindowFor(agent.ContextWindowTokens) - context.ReplyReserveTokens;
        if (recorder.EstimatedPromptTokens > limit)
        {
            logger.LogWarning("llm call rejected: context_too_large estimated_tokens={EstimatedTokens} limit_tokens={Limit}",
                recorder.EstimatedPromptTokens, limit);
            svc.Metrics.RecordContextRejection(agent.Name);
            return await Reject(ErrorCodes.ContextTooLarge, ContextTooLarge());
        }

        return null;
    }

    /// <summary>The thread and the claimed user message must both belong to this agent (else 404).</summary>
    private static async Task<IResult?> CheckOwnershipAsync(
        SharpClawDbContext db, AuthenticatedAgent agent, Guid threadId, Guid correlationId, CancellationToken ct)
    {
        if (!await TurnGuard.OwnsThreadAsync(db, agent.Id, threadId, ct))
            return ApiResults.NotFound("Thread not found.");
        if (await TurnGuard.FindClaimedAsync(db, agent.Id, correlationId, threadId, ct) is null)
            return ApiResults.NotFound("Correlation message not found.");
        return null;
    }

    /// <summary>Estimate of the prompt: the messages plus the tool schemas offered with them.</summary>
    private static int EstimatePrompt(ParsedChatRequest request, JsonElement root, ContextOptions options)
    {
        var estimator = new TokenEstimator(options);
        var messages = request.Messages.Select(m => new ContextMessage(m.Role, m.Content,
            m.ToolCalls?.Select(c => new ContextToolCall(c.Id, "function", new ContextFunction(c.Name, c.ArgumentsJson))).ToList(),
            m.ToolCallId));
        var schemaChars = root.TryGetProperty("tools", out var tools) ? tools.GetRawText().Length : 0;
        return estimator.Estimate(messages) + estimator.EstimateChars(schemaChars);
    }

    private static IResult ContextTooLarge() =>
        ApiResults.Error(400, ErrorCodes.ContextTooLarge, "The request does not fit in the agent's context window.");

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
        private readonly IEventPublisher _events;
        private readonly ActiveTurnBuffers _buffers;
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

        public ChatStream(HttpResponse response, IEventPublisher events, ActiveTurnBuffers buffers, LlmCallRecorder recorder,
            ILogger logger, string model, Guid threadId, Guid correlationId, bool teeToThread)
        {
            _teeToThread = teeToThread;
            _response = response;
            _events = events;
            _buffers = buffers;
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
                return pex.Kind switch
                {
                    ProviderErrorKind.Timeout => ApiResults.Error(504, ErrorCodes.ProviderTimeout, "Upstream timed out."),
                    ProviderErrorKind.ContextLength => ContextTooLarge(),
                    _ => ApiResults.Error(502, ErrorCodes.ProviderError, "Upstream failure."),
                };
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

            // Record before [DONE]: clients read until [DONE], so the llm_calls and tool_calls rows
            // exist by the time an agent reports a tool call's outcome.
            await _recorder.RecordAsync(Succeeded(), ct, _toolCalls);
            await WriteAsync(ChatChunkWriter.DoneLine, ct);
            return Results.Empty;
        }

        private async Task HandleAsync(ChatStreamEvent ev, CancellationToken ct)
        {
            switch (ev)
            {
                case ChatStreamEvent.Delta d:
                    _ttftMs ??= ElapsedMs;
                    await WriteAsync(_writer.ContentChunk(d.Text), ct);
                    if (_teeToThread)
                    {
                        // Buffer first, then publish: a stream's snapshot then covers exactly the deltas up to its length.
                        var endOffset = _buffers.Append(_threadId, _correlationId, d.Text);
                        _events.PublishDelta(_threadId, _correlationId, d.Text, endOffset);
                    }
                    break;
                case ChatStreamEvent.ToolCall call:
                    _ttftMs ??= ElapsedMs;
                    await WriteAsync(_writer.ToolCallChunk(_toolCalls.Count, call.Id, call.Name, call.ArgumentsJson), ct);
                    _toolCalls.Add(call);
                    // Only the tool name is published: arguments may contain content.
                    if (_teeToThread)
                        await _events.PublishDurableAsync(_threadId, SseEventNames.ToolRequested,
                            new ToolRequestedEvent(_correlationId, call.Id, call.Name, _threadId), ct);
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
            ErrorCode = pex.Kind switch
            {
                ProviderErrorKind.Timeout => ErrorCodes.ProviderTimeout,
                ProviderErrorKind.ContextLength => ErrorCodes.ContextTooLarge,
                _ => ErrorCodes.ProviderError,
            },
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
