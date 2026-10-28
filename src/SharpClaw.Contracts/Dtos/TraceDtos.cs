namespace SharpClaw.Contracts;

// ----- Thread trace (3.3 spec section 5.3). Metadata only: never prompts, replies, arguments or results. -----

public sealed record TraceResponse(IReadOnlyList<TraceTurnDto> Turns);

/// <summary>One user turn, joined by correlation id (the user message id).</summary>
public sealed record TraceTurnDto(
    Guid CorrelationId,
    Guid UserMessageId,
    string Status,
    string? FailReason,
    DateTime CreatedAt,
    DateTime? ClaimedAt,
    DateTime? CompletedAt,
    IReadOnlyList<TraceLlmCallDto> LlmCalls,
    IReadOnlyList<TraceToolCallDto> ToolCalls,
    IReadOnlyList<TraceApprovalDto> Approvals,
    TraceTotalsDto Totals);

public sealed record TraceLlmCallDto(
    Guid Id,
    string Model,
    string Status,
    string Purpose,
    int PromptTokens,
    int CompletionTokens,
    int? EstimatedPromptTokens,
    double CostUsd,
    bool UsageKnown,
    int LatencyMs,
    int? TtftMs,
    string? ErrorCode,
    DateTime CreatedAt);

/// <summary><c>ToolCallId</c> is the provider's id, matching <c>MessageDto.ToolCallId</c>; <c>Outcome</c> is the row status.</summary>
public sealed record TraceToolCallDto(
    Guid Id,
    string ToolCallId,
    string ToolName,
    string? Risk,
    string? Decision,
    string Outcome,
    Guid? ApprovalId,
    int? DurationMs,
    DateTime CreatedAt,
    DateTime? ReportedAt);

public sealed record TraceApprovalDto(
    Guid Id,
    string ToolCallId,
    string ToolName,
    string Risk,
    string Status,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    string? DecidedBy);

public sealed record TraceTotalsDto(double CostUsd, int PromptTokens, int CompletionTokens, int LlmCalls, int ToolCalls);
