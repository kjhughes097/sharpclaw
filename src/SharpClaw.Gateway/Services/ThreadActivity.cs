using System.Text;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// Keeps the thread-list columns (<c>last_message_at</c>, <c>last_preview</c>) and the auto-title in
/// step with <c>kind = 'message'</c> rows (3.3 spec section 5.1).
/// </summary>
public static class ThreadActivity
{
    public const int MaxPreviewChars = 120;
    public const int MaxAutoTitleChars = 60;
    public const int MaxTitleChars = 120;

    /// <summary>Call when a user message or final reply is inserted into the thread.</summary>
    public static void OnMessage(ConversationThread thread, string content, DateTime at, bool isFirstUserMessage)
    {
        thread.LastMessageAt = at;
        thread.LastPreview = Collapse(content, MaxPreviewChars);
        if (isFirstUserMessage && thread.Title is null)
        {
            var title = Collapse(content, MaxAutoTitleChars);
            if (title.Length > 0)
                thread.Title = title;
        }
    }

    /// <summary>Collapses runs of whitespace to one space, trims, and cuts to <paramref name="max"/> characters.</summary>
    public static string Collapse(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                if (sb.Length + 1 >= max) break;
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
            if (sb.Length >= max) break;
        }
        return sb.ToString();
    }
}
