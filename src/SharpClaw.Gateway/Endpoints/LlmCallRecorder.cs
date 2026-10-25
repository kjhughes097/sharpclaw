using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>How one LLM call ended. Replaces a long positional parameter list.</summary>
public sealed record LlmCallOutcome
{
    public required string Status { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double CostUsd { get; init; }
    public bool UsageKnown { get; init; }
    public int LatencyMs { get; init; }
    public int? TtftMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorDetail { get; init; }

    public static LlmCallOutcome Cancelled(int latencyMs, int? ttftMs = null) =>
        new() { Status = LlmCallStatus.Cancelled, LatencyMs = latencyMs, TtftMs = ttftMs };

    public static LlmCallOutcome Rejected(string errorCode) =>
        new() { Status = LlmCallStatus.Rejected, ErrorCode = errorCode };
}

/// <summary>
/// Records the <c>llm_calls</c> row, the summary log line and the metrics for one chat call.
/// Created once per request with the call's fixed context, so callers only pass the outcome.
/// </summary>
public sealed class LlmCallRecorder
{
    private readonly SharpClawDbContext _db;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger _logger;
    private readonly AuthenticatedAgent _agent;
    private readonly Guid _threadId;
    private readonly Guid _correlationId;
    private readonly DateTime _startedAt;
    private readonly string _purpose;

    public LlmCallRecorder(
        SharpClawDbContext db, GatewayMetrics metrics, ILogger logger,
        AuthenticatedAgent agent, Guid threadId, Guid correlationId, DateTime startedAt,
        string purpose = LlmCallPurpose.Chat)
    {
        _purpose = purpose;
        _db = db;
        _metrics = metrics;
        _logger = logger;
        _agent = agent;
        _threadId = threadId;
        _correlationId = correlationId;
        _startedAt = startedAt;
    }

    /// <summary>Persists the call and emits telemetry. Returns the stored row.</summary>
    public async Task<LlmCall> RecordAsync(LlmCallOutcome outcome, CancellationToken ct)
    {
        // Cost is only meaningful when the provider reported usage.
        var cost = outcome.UsageKnown ? outcome.CostUsd : 0;

        var row = new LlmCall
        {
            Id = Guid.CreateVersion7(),
            AgentId = _agent.Id,
            ThreadId = _threadId,
            CorrelationId = _correlationId,
            Model = _agent.Model,
            Status = outcome.Status,
            PromptTokens = outcome.PromptTokens,
            CompletionTokens = outcome.CompletionTokens,
            CostUsd = cost,
            UsageKnown = outcome.UsageKnown,
            LatencyMs = outcome.LatencyMs,
            TtftMs = outcome.TtftMs,
            ErrorCode = outcome.ErrorCode,
            ErrorDetail = Truncate(outcome.ErrorDetail, 200),
            Purpose = _purpose,
            CreatedAt = _startedAt,
        };
        _db.LlmCalls.Add(row);
        await _db.SaveChangesAsync(ct);

        if (outcome.Status == LlmCallStatus.Rejected)
        {
            // Rejected calls never reached the provider: count them, nothing else.
            _metrics.RecordCall(_agent.Name, _agent.Model, outcome.Status);
            return row;
        }

        _logger.LogInformation(
            "llm call finished: status={Status} purpose={Purpose} model={Model} prompt_tokens={PromptTokens} completion_tokens={CompletionTokens} cost_usd={CostUsd} usage_known={UsageKnown} latency_ms={LatencyMs} ttft_ms={TtftMs} error_code={ErrorCode}",
            outcome.Status, _purpose, _agent.Model, outcome.PromptTokens, outcome.CompletionTokens, cost,
            outcome.UsageKnown, outcome.LatencyMs, outcome.TtftMs, outcome.ErrorCode);

        _metrics.RecordCall(_agent.Name, _agent.Model, outcome.Status);
        _metrics.RecordTokens(_agent.Name, _agent.Model, outcome.PromptTokens, outcome.CompletionTokens);
        _metrics.RecordCost(_agent.Name, _agent.Model, cost);
        _metrics.RecordDuration(_agent.Name, _agent.Model, outcome.LatencyMs / 1000.0);
        if (outcome.TtftMs is { } ttft)
            _metrics.RecordTtft(_agent.Name, _agent.Model, ttft / 1000.0);

        return row;
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];
}
