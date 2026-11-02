using System.Text.Json.Serialization;

namespace SharpClaw.Contracts;

/// <summary>
/// A model-ready message in the OpenAI wire shape (snake_case <c>tool_calls</c> and
/// <c>tool_call_id</c>), as returned by the agent context endpoint. <c>ImageIds</c> (a SharpClaw
/// extension, attachments spec section 4) are images attached to a user message; the gateway
/// resolves them to image content only when it calls the provider.
/// </summary>
public sealed record ContextMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ContextToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolCallId = null,
    [property: JsonPropertyName("image_ids"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<Guid>? ImageIds = null)
{
    public ChatMessage ToChatMessage() => new(Role, Content,
        ToolCalls?.Select(c => new ChatToolCall(c.Id, c.Function.Name, c.Function.Arguments)).ToList(), ToolCallId, ImageIds);
}

public sealed record ContextToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] ContextFunction Function);

public sealed record ContextFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

/// <summary>
/// <c>GET /agent/threads/{id}/context</c>: the model-ready history that fits the agent's window.
/// <c>EstimatedTokens</c> covers <c>Messages</c> only (not the caller's reserve).
/// </summary>
public sealed record ContextResponse(
    IReadOnlyList<ContextMessage> Messages, bool Truncated, int DroppedTurns, int EstimatedTokens, int WindowTokens);
