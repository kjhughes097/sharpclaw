using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Core;

/// <summary>
/// Represents a single event in the agent streaming pipeline.
/// Each variant maps to an SSE event type.
/// </summary>
[JsonConverter(typeof(AgentEventJsonConverter))]
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

public sealed record StatusEvent(
    [property: JsonPropertyName("message")] string Message) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "status";
}

public sealed record UsageEvent(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("inputTokens")] long InputTokens,
    [property: JsonPropertyName("outputTokens")] long OutputTokens) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "usage";

    [JsonPropertyName("totalTokens")]
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record DoneEvent(
    [property: JsonPropertyName("content")] string Content) : AgentEvent
{
    [JsonPropertyName("type")]
    public override string Type => "done";
}

internal sealed class AgentEventJsonConverter : JsonConverter<AgentEvent>
{
    public override AgentEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var eventType = root.TryGetProperty("type", out var typeProperty) && typeProperty.ValueKind == JsonValueKind.String
            ? typeProperty.GetString()
            : InferType(root);

        return eventType switch
        {
            "token" => (AgentEvent?)root.Deserialize<TokenEvent>(options),
            "tool_call" => (AgentEvent?)root.Deserialize<ToolCallEvent>(options),
            "tool_result" => (AgentEvent?)root.Deserialize<ToolResultEvent>(options),
            "permission_request" => (AgentEvent?)root.Deserialize<PermissionRequestEvent>(options),
            "status" => (AgentEvent?)root.Deserialize<StatusEvent>(options),
            "usage" => (AgentEvent?)root.Deserialize<UsageEvent>(options),
            "done" => (AgentEvent?)root.Deserialize<DoneEvent>(options),
            _ => throw new JsonException($"Unknown agent event type '{eventType ?? "<null>"}'."),
        } ?? throw new JsonException("Failed to deserialize agent event.");
    }

    public override void Write(Utf8JsonWriter writer, AgentEvent value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }

    private static string? InferType(JsonElement root)
    {
        if (root.TryGetProperty("text", out _))
            return "token";

        if (root.TryGetProperty("requestId", out _))
            return "permission_request";

        if (root.TryGetProperty("result", out _))
            return "tool_result";

        if (root.TryGetProperty("tool", out _))
            return "tool_call";

        if (root.TryGetProperty("message", out _))
            return "status";

        if (root.TryGetProperty("totalTokens", out _) || root.TryGetProperty("provider", out _))
            return "usage";

        if (root.TryGetProperty("content", out _))
            return "done";

        return null;
    }
}
