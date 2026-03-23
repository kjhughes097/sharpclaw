namespace SharpClaw.Core;

/// <summary>
/// Truncates conversation history to stay within a token budget.
/// Uses a simple character-based estimate (1 token ≈ 4 chars) and drops
/// the oldest messages first, always preserving the most recent turn.
/// </summary>
public static class HistoryTruncator
{
    private const int CharsPerToken = 4;

    /// <summary>
    /// Returns a truncated copy of the messages that fits within the token limit.
    /// Drops oldest messages first but always keeps at least the last user message.
    /// </summary>
    /// <param name="messages">Full message history.</param>
    /// <param name="systemPrompt">System prompt (counts toward the budget).</param>
    /// <param name="maxTokens">Maximum token budget for context (default 100k).</param>
    public static IReadOnlyList<ChatMessage> Truncate(
        IReadOnlyList<ChatMessage> messages,
        string systemPrompt,
        int maxTokens = 100_000)
    {
        var budgetChars = maxTokens * CharsPerToken;
        var systemChars = systemPrompt.Length;
        var availableChars = budgetChars - systemChars;

        if (availableChars <= 0)
            return messages.Count > 0 ? [messages[^1]] : [];

        // Walk backward, accumulating messages until we exhaust the budget.
        var kept = new List<ChatMessage>();
        var usedChars = 0;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msgChars = messages[i].Content.Length;
            if (usedChars + msgChars > availableChars && kept.Count > 0)
                break;

            kept.Add(messages[i]);
            usedChars += msgChars;
        }

        kept.Reverse();
        return kept;
    }
}
