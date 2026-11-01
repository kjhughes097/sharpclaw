namespace SharpClaw.Contracts;

// ----- Threads -----

/// <summary><c>Model</c>: optional override, one of <c>Models:Allowed</c> from <c>GET /api/system</c>.</summary>
public sealed record CreateThreadRequest(string AgentId, string? Title, string? Model = null);

/// <summary>
/// <c>Context</c>: how the thread fit the model's window the last time the agent built its context.
/// <c>DroppedTurns</c> &gt; 0 means older messages are no longer seen by the model.
/// <c>Model</c>: the thread's model override; null means the agent's model.
/// <c>OriginThreadId</c>/<c>OriginAgentName</c>: on a linked thread (another agent's questions, 3.5
/// spec), the asking agent's thread and name; null on normal threads.
/// </summary>
public sealed record ThreadDto(
    Guid Id,
    Guid AgentId,
    string? Title,
    DateTime CreatedAt,
    ThreadContextDto? Context = null,
    bool Archived = false,
    DateTime? LastMessageAt = null,
    string? Model = null,
    Guid? OriginThreadId = null,
    string? OriginAgentName = null);

public sealed record ThreadContextDto(int DroppedTurns, int EstimatedTokens, int WindowTokens);

public sealed record CreatedThreadResponse(ThreadDto Thread);

/// <summary>
/// A thread-list row. <c>InFlight</c>: a user message is claimed; <c>Queued</c>: user messages still
/// pending; <c>PendingApprovals</c>: approvals waiting for the human. <c>OriginThreadId</c>/<c>OriginAgentName</c>
/// as on <see cref="ThreadDto"/>.
/// </summary>
public sealed record ThreadSummaryDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    string? Title,
    bool Archived,
    DateTime CreatedAt,
    DateTime LastMessageAt,
    string? Preview,
    int PendingApprovals,
    bool InFlight,
    int Queued,
    string? Model = null,
    Guid? OriginThreadId = null,
    string? OriginAgentName = null);

/// <summary>Newest <c>LastMessageAt</c> first. <c>NextCursor</c> is opaque; null on the last page.</summary>
public sealed record ThreadListResponse(IReadOnlyList<ThreadSummaryDto> Threads, string? NextCursor = null);

/// <summary>
/// Every field optional. <c>Title</c> is trimmed and must be 1 to 120 characters. <c>Model</c>: null
/// leaves it unchanged, <c>""</c> clears the override, anything else must be in <c>Models:Allowed</c>.
/// </summary>
public sealed record UpdateThreadRequest(string? Title, bool? Archived, string? Model = null);

public sealed record ThreadResponse(ThreadDto Thread);

/// <summary>The claimed user message of the turn in progress.</summary>
public sealed record InFlightTurnDto(Guid CorrelationId, DateTime? ClaimedAt);

/// <summary>A pending approval as shown in the thread view. Never carries tool arguments.</summary>
public sealed record PendingApprovalDto(
    Guid Id, Guid CorrelationId, string ToolName, string Risk, string Summary, string Reason, DateTime CreatedAt,
    DateTime ExpiresAt);

/// <summary>Everything the thread view needs besides the messages. <c>InFlight</c> is null when idle.</summary>
public sealed record ThreadDetailResponse(
    ThreadDto Thread,
    AgentDto Agent,
    InFlightTurnDto? InFlight,
    int Queued,
    IReadOnlyList<PendingApprovalDto> PendingApprovals);
