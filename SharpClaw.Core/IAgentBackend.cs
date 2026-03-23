namespace SharpClaw.Core;

/// <summary>
/// Abstraction over LLM backends. Each implementation handles the model-specific
/// conversation protocol and internal tool-use loop.
/// </summary>
public interface IAgentBackend : IAsyncDisposable
{
    /// <summary>
    /// Runs a conversation turn: sends the history to the model, executes any
    /// tool calls via <paramref name="toolDispatcher"/>, and returns the final
    /// text response.
    /// </summary>
    /// <param name="systemPrompt">System prompt for the model.</param>
    /// <param name="tools">Available tool schemas to advertise to the model.</param>
    /// <param name="history">Conversation history (user and assistant turns).</param>
    /// <param name="toolDispatcher">Callback to execute tool calls (handles MCP routing + permission gating).</param>
    /// <param name="onProgress">Optional callback for progress messages (e.g. "Calling API…", "Running tool X…").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model's final text response for this turn.</returns>
    Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<ToolSchema> tools,
        IReadOnlyList<ChatMessage> history,
        Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default);
}
