using System.Text.Json.Serialization;

namespace SharpClaw.Contracts;

/// <summary>
/// A model-ready message in the OpenAI wire shape (snake_case <c>tool_calls</c> and
/// <c>tool_call_id</c>), as returned by the agent context endpoint.
/// </summary>
public sealed record ContextMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ContextToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolCallId = null)
{
    public ChatMessage ToChatMessage() => new(Role, Content,
        ToolCalls?.Select(c => new ChatToolCall(c.Id, c.Function.Name, c.Function.Arguments)).ToList(), ToolCallId);
}

public sealed record ContextToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ContextFunction Function);

public sealed record ContextFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);
