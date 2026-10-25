using System.Text.Json;
using System.Text.RegularExpressions;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Providers;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>A validated <c>/v1/chat/completions</c> request body.</summary>
public sealed record ParsedChatRequest(
    List<ProviderChatMessage> Messages, double? Temperature, int? MaxTokens, double? TopP, List<string>? Stop,
    List<ProviderTool>? Tools = null, string? ToolChoice = null)
{
    /// <summary>Builds the provider request. The agent's configured model always wins.</summary>
    public ProviderChatRequest ToProviderRequest(string model) =>
        new(model, Messages, Temperature, MaxTokens, TopP, Stop, Tools, ToolChoice);
}

/// <summary>
/// Validates the OpenAI-shaped request body. Returns either a parsed request or an error result in
/// the single error shape — never both.
/// </summary>
public static partial class ChatRequestParser
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNameRegex();

    public static (ParsedChatRequest? Request, IResult? Error) Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Fail(ApiResults.ValidationFailed("Body must be a JSON object."));

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

        var (tools, toolsError) = ParseTools(root);
        if (toolsError is not null)
            return Fail(toolsError);

        string? toolChoice = null;
        if (root.TryGetProperty("tool_choice", out var choiceEl))
        {
            toolChoice = choiceEl.ValueKind == JsonValueKind.String ? choiceEl.GetString() : null;
            if (toolChoice is not ("auto" or "none"))
                return Fail(ApiResults.Unsupported("tool_choice must be \"auto\" or \"none\"."));
        }

        var request = new ParsedChatRequest(
            messages,
            TryNumber(root, "temperature"),
            TryInt(root, "max_tokens"),
            TryNumber(root, "top_p"),
            ParseStop(root),
            tools,
            toolChoice);
        return (request, null);
    }

    private static (ProviderChatMessage? Message, IResult? Error) ParseMessage(JsonElement m)
    {
        if (m.ValueKind != JsonValueKind.Object ||
            !m.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
            return (null, ApiResults.ValidationFailed("each message needs a role."));

        var role = roleEl.GetString()!;
        if (role is not (Roles.System or Roles.User or Roles.Assistant or Roles.Tool))
            return (null, ApiResults.ValidationFailed($"unsupported role '{role}'."));

        List<ProviderToolCall>? toolCalls = null;
        if (role == Roles.Assistant && m.TryGetProperty("tool_calls", out var callsEl))
        {
            var (calls, callsError) = ParseToolCalls(callsEl);
            if (callsError is not null)
                return (null, callsError);
            toolCalls = calls;
        }

        // Content may only be missing or null on an assistant message that carries tool calls.
        var hasContent = m.TryGetProperty("content", out var contentEl) && contentEl.ValueKind != JsonValueKind.Null;
        if (!hasContent && toolCalls is not { Count: > 0 })
            return (null, ApiResults.ValidationFailed("each message needs content."));
        if (hasContent && contentEl.ValueKind != JsonValueKind.String)
            return (null, ApiResults.Unsupported("only string message content is supported."));
        var content = hasContent ? contentEl.GetString() ?? "" : null;

        string? toolCallId = null;
        if (role == Roles.Tool)
        {
            if (!m.TryGetProperty("tool_call_id", out var idEl) || idEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(idEl.GetString()))
                return (null, ApiResults.ValidationFailed("tool messages need a tool_call_id."));
            toolCallId = idEl.GetString();
        }

        return (new ProviderChatMessage(role, content, toolCalls, toolCallId), null);
    }

    private static (List<ProviderToolCall>? Calls, IResult? Error) ParseToolCalls(JsonElement callsEl)
    {
        if (callsEl.ValueKind != JsonValueKind.Array)
            return (null, ApiResults.ValidationFailed("tool_calls must be an array."));

        var calls = new List<ProviderToolCall>();
        foreach (var c in callsEl.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object ||
                !TryString(c, "id", out var id) ||
                !c.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object ||
                !TryString(fn, "name", out var name) ||
                !TryString(fn, "arguments", out var args))
                return (null, ApiResults.ValidationFailed("each tool call needs id, function.name and function.arguments."));
            calls.Add(new ProviderToolCall(id, name, args));
        }
        return (calls, null);
    }

    private static (List<ProviderTool>? Tools, IResult? Error) ParseTools(JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var toolsEl))
            return (null, null);
        if (toolsEl.ValueKind != JsonValueKind.Array)
            return (null, ApiResults.ValidationFailed("tools must be an array."));

        var tools = new List<ProviderTool>();
        foreach (var t in toolsEl.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object || !TryString(t, "type", out var type))
                return (null, ApiResults.ValidationFailed("each tool needs a type."));
            if (type != "function")
                return (null, ApiResults.Unsupported("only function tools are supported."));
            if (!t.TryGetProperty("function", out var fn) || fn.ValueKind != JsonValueKind.Object ||
                !TryString(fn, "name", out var name) || !ToolNameRegex().IsMatch(name))
                return (null, ApiResults.ValidationFailed("each tool needs a function.name matching ^[a-zA-Z0-9_-]{1,64}$."));

            string? parameters = null;
            if (fn.TryGetProperty("parameters", out var p))
            {
                if (p.ValueKind != JsonValueKind.Object)
                    return (null, ApiResults.ValidationFailed("function.parameters must be a JSON object."));
                parameters = p.GetRawText();
            }
            TryString(fn, "description", out var description);
            tools.Add(new ProviderTool(name, string.IsNullOrEmpty(description) ? null : description, parameters));
        }
        return (tools, null);
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? "";
        return true;
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
