namespace SharpClaw.Core;

/// <summary>
/// An ordered, mutable conversation that tracks user/assistant turns.
/// Wraps a list of <see cref="ChatMessage"/> and provides convenience methods
/// for appending turns and producing the read-only view that backends expect.
/// </summary>
public sealed class ConversationHistory
{
    private readonly List<ChatMessage> _messages = [];

    public string SessionId { get; }
    public string AgentFile { get; }

    public ConversationHistory(string sessionId, string agentFile)
    {
        SessionId = sessionId;
        AgentFile = agentFile;
    }

    public IReadOnlyList<ChatMessage> Messages => _messages;
    public int Count => _messages.Count;

    public void AddUser(string content) =>
        _messages.Add(new ChatMessage(ChatRole.User, content));

    public void AddAssistant(string content) =>
        _messages.Add(new ChatMessage(ChatRole.Assistant, content));

    public void AddRange(IEnumerable<ChatMessage> messages) =>
        _messages.AddRange(messages);

    public void ReplaceLastUser(string content)
    {
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Role == ChatRole.User)
            {
                _messages[i] = new ChatMessage(ChatRole.User, content);
                return;
            }
        }
    }

    /// <summary>
    /// Replaces the in-memory messages with a truncated set.
    /// </summary>
    internal void ReplaceWith(IReadOnlyList<ChatMessage> truncated)
    {
        _messages.Clear();
        _messages.AddRange(truncated);
    }
}
