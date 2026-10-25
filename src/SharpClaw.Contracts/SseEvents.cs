namespace SharpClaw.Contracts;

/// <summary>Names of the Server-Sent Events emitted on the client thread stream.</summary>
public static class SseEventNames
{
    public const string Delta = "delta";
    public const string MessageCompleted = "message.completed";
    public const string Error = "error";
    public const string ToolRequested = "tool.requested";
    public const string ApprovalRequested = "approval.requested";
}

public sealed record DeltaEvent(Guid CorrelationId, string Content);

public sealed record MessageCompletedEvent(Guid CorrelationId, Guid MessageId, string Content);

public sealed record ErrorEvent(Guid CorrelationId, string Code, string Message);

/// <summary>The model asked for a tool call. Never carries the arguments.</summary>
public sealed record ToolRequestedEvent(Guid CorrelationId, string ToolCallId, string ToolName);

/// <summary>The agent needs the human to allow or deny a tool call.</summary>
public sealed record ApprovalRequestedEvent(
    Guid ApprovalId, Guid CorrelationId, string ToolName, string Summary, string Risk, string Reason, DateTime ExpiresAt);
