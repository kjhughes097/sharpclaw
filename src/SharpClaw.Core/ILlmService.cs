namespace SharpClaw.Core;

/// <summary>
/// Common interface for LLM service backends (Copilot SDK, Anthropic, OpenAI, etc.).
/// </summary>
public interface ILlmService
{
    /// <summary>Unique name of this service (e.g. "copilot", "llm").</summary>
    string ServiceName { get; }

    /// <summary>
    /// Streams a response from the LLM.
    /// </summary>
    /// <param name="model">Model identifier (e.g. "claude-opus-4.6").</param>
    /// <param name="systemPrompt">Full assembled system prompt.</param>
    /// <param name="history">Conversation history for this turn.</param>
    /// <param name="tools">Tool schemas available to the model.</param>
    /// <param name="toolDispatcher">Callback to execute tool calls.</param>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<AgentEvent> StreamAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<ToolSchema> tools,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        CancellationToken ct = default);
}
