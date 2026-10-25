namespace SharpClaw.Contracts;

/// <summary>A tool call the model requested. <c>Arguments</c> is a JSON object as a string.</summary>
public sealed record ChatToolCall(string Id, string Name, string Arguments);

/// <summary>
/// OpenAI-style chat message with string content. An assistant message may carry
/// <c>ToolCalls</c> (with null content); a <c>tool</c> message answers <c>ToolCallId</c>.
/// </summary>
public sealed record ChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>
/// OpenAI-compatible chat completion request. <c>Model</c> is accepted but ignored by the
/// gateway (the agent's configured model is always used). <c>Stream</c> must be true.
/// </summary>
public sealed record ChatCompletionRequest
{
    public string? Model { get; init; }
    public List<ChatMessage> Messages { get; init; } = new();
    public bool Stream { get; init; }
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public double? TopP { get; init; }
    public List<string>? Stop { get; init; }
}
