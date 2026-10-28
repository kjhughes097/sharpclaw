namespace SharpClaw.Contracts;

// ----- Audit -----

public sealed record LlmCallDto(
    Guid Id,
    Guid AgentId,
    Guid ThreadId,
    Guid CorrelationId,
    string Model,
    string Status,
    int PromptTokens,
    int CompletionTokens,
    double CostUsd,
    bool UsageKnown,
    int LatencyMs,
    int? TtftMs,
    string? ErrorCode,
    string? ErrorDetail,
    DateTime CreatedAt,
    int? EstimatedPromptTokens = null,
    string Purpose = LlmCallPurpose.Chat);

/// <summary>Newest first. Pass <c>NextCursor</c> as <c>before</c> for the next page; null on the last page.</summary>
public sealed record AuditResponse(IReadOnlyList<LlmCallDto> Calls, string? NextCursor = null);

// ----- Tool calls (3.2) -----

/// <summary>The agent's report of what happened to a tool call the model requested.</summary>
public sealed record ToolCallReportRequest(
    Guid CorrelationId,
    string ToolCallId,
    string Status,
    string? Decision,
    string? Risk,
    bool Tainted,
    Guid? ApprovalId,
    string? ErrorCode,
    int? DurationMs);

public sealed record ToolCallDto(
    Guid Id,
    Guid AgentId,
    Guid ThreadId,
    Guid CorrelationId,
    Guid LlmCallId,
    string ToolCallId,
    string ToolName,
    string Arguments,
    string Status,
    string? Decision,
    string? Risk,
    bool Tainted,
    Guid? ApprovalId,
    string? ErrorCode,
    int? DurationMs,
    DateTime CreatedAt,
    DateTime? ReportedAt);

/// <summary>Newest first. Pass <c>NextCursor</c> as <c>before</c> for the next page; null on the last page.</summary>
public sealed record ToolCallAuditResponse(IReadOnlyList<ToolCallDto> Calls, string? NextCursor = null);
