using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpClaw.Contracts;

/// <summary>
/// Truncation of tool exchanges stored in thread history. The agent applies the defaults before
/// posting; the gateway re-applies its configured limits to whatever it receives.
/// </summary>
public static class ToolHistoryLimits
{
    public const int DefaultMaxStoredArgumentStringChars = 500;
    public const int DefaultMaxStoredResultChars = 4000;

    /// <summary>
    /// Shortens every JSON string value longer than <paramref name="maxStringChars"/>, keeping the
    /// arguments valid JSON. Arguments that aren't valid JSON are shortened as one string.
    /// </summary>
    public static string TruncateArguments(string arguments, int maxStringChars, out bool truncated)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(arguments);
        }
        catch (JsonException)
        {
            return TruncateText(arguments, maxStringChars, out truncated);
        }

        var changed = false;
        var result = Walk(root, maxStringChars, ref changed);
        truncated = changed;
        return changed ? result?.ToJsonString() ?? "null" : arguments;
    }

    /// <summary>Cuts <paramref name="text"/> to <paramref name="maxChars"/>, appending a marker.</summary>
    public static string TruncateText(string text, int maxChars, out bool truncated)
    {
        truncated = text.Length > maxChars;
        return truncated ? text[..maxChars] + $"…[truncated {text.Length - maxChars} chars]" : text;
    }

    private static JsonNode? Walk(JsonNode? node, int max, ref bool changed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    var replaced = Walk(child, max, ref changed);
                    if (!ReferenceEquals(replaced, child))
                        obj[key] = replaced;
                }
                return obj;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var child = arr[i];
                    var replaced = Walk(child, max, ref changed);
                    if (!ReferenceEquals(replaced, child))
                        arr[i] = replaced;
                }
                return arr;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var s = value.GetValue<string>();
                if (s.Length <= max)
                    return value;
                changed = true;
                return JsonValue.Create(TruncateText(s, max, out _));
            default:
                return node;
        }
    }
}
