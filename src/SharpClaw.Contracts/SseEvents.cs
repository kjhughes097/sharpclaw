namespace SharpClaw.Contracts;

/// <summary>
/// Names of the Server-Sent Events emitted on the client streams. Durable events carry an
/// <c>id:</c> and are replayable; <c>delta</c>, <c>snapshot</c> and <c>reset</c> are ephemeral.
/// </summary>
public static class SseEventNames
{
    public const string Delta = "delta";
    public const string MessageCreated = "message.created";
    public const string MessageCompleted = "message.completed";
    public const string Error = "error";
    public const string ToolRequested = "tool.requested";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalDecided = "approval.decided";
    public const string Snapshot = "snapshot";
    public const string Ready = "ready";
    public const string Reset = "reset";
    public const string ThreadCreated = "thread.created";
    public const string ThreadUpdated = "thread.updated";
}

// Every payload carries ThreadId so a single global stream can route events (3.3 spec section 5.7).
// It is a trailing optional parameter so older positional call sites keep compiling; the gateway
// always sets it.

public sealed record DeltaEvent(Guid CorrelationId, string Content, Guid ThreadId = default);

/// <summary>A user message was posted. <c>CorrelationId</c> equals <c>MessageId</c>.</summary>
public sealed record MessageCreatedEvent(Guid MessageId, Guid ThreadId, Guid CorrelationId);

public sealed record MessageCompletedEvent(Guid CorrelationId, Guid MessageId, string Content, Guid ThreadId = default);

public sealed record ErrorEvent(Guid CorrelationId, string Code, string Message, Guid ThreadId = default);

/// <summary>The model asked for a tool call. Never carries the arguments.</summary>
public sealed record ToolRequestedEvent(Guid CorrelationId, string ToolCallId, string ToolName, Guid ThreadId = default);

/// <summary>The agent needs the human to allow or deny a tool call.</summary>
public sealed record ApprovalRequestedEvent(
    Guid ApprovalId, Guid CorrelationId, string ToolName, string Summary, string Risk, string Reason, DateTime ExpiresAt,
    Guid ThreadId = default);

/// <summary>An approval was decided by the user or by the system (expiry, unattended, orphaned turn).</summary>
/// <summary><c>DecidedByUser</c>: the deciding user's name (or <c>admin_key</c>) when <c>DecidedBy</c> is <c>user</c>.</summary>
public sealed record ApprovalDecidedEvent(
    Guid ApprovalId, Guid CorrelationId, string Status, string DecidedBy, Guid ThreadId = default, string? DecidedByUser = null);

/// <summary>The text streamed so far for an in-flight turn, sent on (re)connect.</summary>
public sealed record SnapshotEvent(Guid CorrelationId, string Content, Guid ThreadId = default);

/// <summary>The requested replay point was pruned; the client must reload history.</summary>
public sealed record ResetEvent(string Reason, Guid? ThreadId = null);

/// <summary>Replay is done; the event's <c>id:</c> is the baseline for the next reconnect. Null thread on the global stream.</summary>
public sealed record ReadyEvent(Guid? ThreadId);

public sealed record ThreadCreatedEvent(Guid ThreadId, Guid AgentId);

public sealed record ThreadUpdatedEvent(Guid ThreadId, string? Title, bool Archived);

public static class ResetReasons
{
    public const string HistoryPruned = "history_pruned";
}
