using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Agent;

/// <summary>
/// Keeps a turn's model calls inside the context window. Within a turn every tool result stays in
/// the request, so a few large ones can outgrow the window and the gateway would reject the next
/// call. Before each call the agent estimates the request exactly as the gateway does and, while it
/// doesn't fit, replaces this turn's oldest tool results with a short stub. Large results are saved
/// in the workspace (<c>savedPath</c> / <c>logPath</c>), so the stub says where to read them again.
/// </summary>
public static class TurnBudget
{
    /// <summary>Kept free for the reply: the gateway's default <c>Context:ReplyReserveTokens</c>.</summary>
    public const int ReplyReserveTokens = 2000;

    private static readonly string[] SavedPathFields = ["savedPath", "logPath"];
    private const string StubPrefix = """{"ok":true,"compacted":true""";

    /// <summary>The request's estimate: the messages plus the tool schemas sent with them.</summary>
    public static int Estimate(IEnumerable<ChatMessage> messages, int schemaChars) =>
        messages.Sum(Estimate) + ContextDefaults.Tokens(schemaChars);

    /// <summary>
    /// Stubs tool results at or after <paramref name="turnStart"/>, oldest first, until the request
    /// fits <c>window − reply reserve</c>. Earlier messages (soul, history, the user's message) are
    /// never touched: the gateway already fitted those.
    /// </summary>
    public static (int Compacted, bool Fits) Fit(List<ChatMessage> messages, int turnStart, int schemaChars, int windowTokens)
    {
        var limit = windowTokens - ReplyReserveTokens;
        var total = Estimate(messages, schemaChars);
        var compacted = 0;
        for (var i = turnStart; i < messages.Count && total > limit; i++)
        {
            var m = messages[i];
            if (m.Role != Roles.Tool || m.Content is null || m.Content.StartsWith(StubPrefix, StringComparison.Ordinal))
                continue;
            var stub = Stub(m.Content);
            if (stub.Length >= m.Content.Length)
                continue; // already smaller than its stub
            var before = Estimate(m);
            messages[i] = m with { Content = stub };
            total += Estimate(messages[i]) - before;
            compacted++;
        }
        return (compacted, total <= limit);
    }

    /// <summary>The stub for a tool result, pointing at its saved copy when it has one.</summary>
    internal static string Stub(string content)
    {
        var path = SavedPath(content);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            compacted = true,
            note = path is null
                ? "This earlier result was removed to keep the turn within the context window. Run the tool again if you still need it."
                : $"This earlier result was removed to keep the turn within the context window. The full output is in {path}: read it with workspace_read if you still need it.",
        }, SharpClawJson.Options);
    }

    private static string? SavedPath(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var field in SavedPathFields)
                if (doc.RootElement.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } path)
                    return path;
        }
        catch (JsonException)
        {
        }
        return null;
    }

    // Mirrors the gateway's TokenEstimator (ChatEndpoints.EstimatePrompt), so both agree on what fits.
    private static int Estimate(ChatMessage m)
    {
        long chars = m.Content?.Length ?? 0;
        if (m.ToolCalls is { Count: > 0 })
            chars += JsonSerializer.Serialize(
                m.ToolCalls.Select(c => new ContextToolCall(c.Id, "function", new ContextFunction(c.Name, c.Arguments))),
                SharpClawJson.Options).Length;
        return ContextDefaults.Tokens(chars) + ContextDefaults.MessageOverheadTokens +
            (m.ImageIds?.Count ?? 0) * ContextDefaults.ImageTokens;
    }
}
