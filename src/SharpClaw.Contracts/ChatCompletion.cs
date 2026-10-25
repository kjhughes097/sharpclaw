namespace SharpClaw.Contracts;

/// <summary>OpenAI-style chat message (string content only in 3.1).</summary>
public sealed record ChatMessage(string Role, string Content);

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
