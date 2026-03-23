using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Core;

/// <summary>
/// Represents a single event in the agent streaming pipeline.
/// Each variant maps to an SSE event type.
/// </summary>
[JsonDerivedType(typeof(TokenEvent), "token")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ToolResultEvent), "tool_result")]
[JsonDerivedType(typeof(PermissionRequestEvent), "permission_request")]
[JsonDerivedType(typeof(DoneEvent), "done")]
public abstract record AgentEvent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public sealed record TokenEvent(
    [property: JsonPropertyName("text")] string Text) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "token";
}

public sealed record ToolCallEvent(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("input")] IReadOnlyDictionary<string, object?>? Input) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "tool_call";
}

public sealed record ToolResultEvent(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("isError")] bool IsError) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "tool_result";
}

public sealed record PermissionRequestEvent(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("input")] IReadOnlyDictionary<string, object?>? Input,
    [property: JsonPropertyName("requestId")] string RequestId) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "permission_request";
}

public sealed record DoneEvent(
    [property: JsonPropertyName("content")] string Content) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "done";
}
