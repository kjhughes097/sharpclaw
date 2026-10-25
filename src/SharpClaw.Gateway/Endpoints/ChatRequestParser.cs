using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>A validated <c>/v1/chat/completions</c> request body.</summary>
public sealed record ParsedChatRequest(
    List<ProviderChatMessage> Messages, double? Temperature, int? MaxTokens, double? TopP, List<string>? Stop)
{
    /// <summary>Builds the provider request. The agent's configured model always wins.</summary>
    public ProviderChatRequest ToProviderRequest(string model) =>
        new(model, Messages, Temperature, MaxTokens, TopP, Stop);
}

/// <summary>
/// Validates the OpenAI-shaped request body. Returns either a parsed request or an error result in
/// the single error shape — never both.
/// </summary>
public static class ChatRequestParser
{
    public static (ParsedChatRequest? Request, IResult? Error) Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Fail(ApiResults.ValidationFailed("Body must be a JSON object."));

        if (root.TryGetProperty("tools", out _) || root.TryGetProperty("tool_choice", out _))
            return Fail(ApiResults.Unsupported("tools are not supported in this phase."));

        if (!root.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
            return Fail(ApiResults.Unsupported("Only stream:true is supported."));

        if (!root.TryGetProperty("messages", out var messagesEl) || messagesEl.ValueKind != JsonValueKind.Array)
            return Fail(ApiResults.ValidationFailed("messages is required."));

        var messages = new List<ProviderChatMessage>();
        foreach (var m in messagesEl.EnumerateArray())
        {
            var (message, error) = ParseMessage(m);
            if (error is not null)
                return Fail(error);
            messages.Add(message!);
        }
        if (messages.Count == 0)
            return Fail(ApiResults.ValidationFailed("messages must be non-empty."));

        var request = new ParsedChatRequest(
            messages,
            TryNumber(root, "temperature"),
            TryInt(root, "max_tokens"),
            TryNumber(root, "top_p"),
            ParseStop(root));
        return (request, null);
    }

    private static (ProviderChatMessage? Message, IResult? Error) ParseMessage(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object ||
            !m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
            return (null, ApiResults.ValidationFailed("each message needs a role."));

        var role = roleEl.GetString()!;
        if (role is not (Roles.System or Roles.User or Roles.Assistant))
            return (null, ApiResults.ValidationFailed($"unsupported role '{role}'."));

        if (!m.TryGetProperty("content", out var contentEl))
            return (null, ApiResults.ValidationFailed("each message needs content."));
        if (contentEl.ValueKind != JsonValueKind.String)
            return (null, ApiResults.Unsupported("only string message content is supported."));

        return (new ProviderChatMessage(role, contentEl.GetString() ?? ""), null);
    }

    private static List<string>? ParseStop(JsonElement root)
    {
        if (!root.TryGetProperty("stop", out var stopEl))
            return null;
        if (stopEl.ValueKind == JsonValueKind.String)
            return new List<string> { stopEl.GetString()! };
        if (stopEl.ValueKind == JsonValueKind.Array)
            return stopEl.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        return null;
    }

    private static (ParsedChatRequest?, IResult?) Fail(IResult error) => (null, error);

    private static double? TryNumber(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : null;

    private static int? TryInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v)
            ? v : null;
}
