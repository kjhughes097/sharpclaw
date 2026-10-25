namespace SharpClaw.Contracts;

/// <summary>Names of the Server-Sent Events emitted on the client thread stream.</summary>
public static class SseEventNames
{
    public const string Delta = "delta";
    public const string MessageCompleted = "message.completed";
    public const string Error = "error";
    public const string ToolRequested = "tool.requested";
}

public sealed record DeltaEvent(Guid CorrelationId, string Content);

public sealed record MessageCompletedEvent(Guid CorrelationId, Guid MessageId, string Content);

public sealed record ErrorEvent(Guid CorrelationId, string Code, string Message);

/// <summary>The model asked for a tool call. Never carries the arguments.</summary>
public sealed record ToolRequestedEvent(Guid CorrelationId, string ToolCallId, string ToolName);
