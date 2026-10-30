using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpClaw.Gateway.Providers;

public sealed class FakeUsageSpec
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double? CostUsd { get; init; }
}

public enum FakeErrorType { Http, Timeout, Midstream, ContextLength }

public sealed class FakeErrorSpec
{
    public FakeErrorType Type { get; init; }
    public int? Status { get; init; }
    public int? AfterChars { get; init; }
}

/// <summary>A tool call a script rule returns. <c>ArgumentsJson</c> is a JSON object as a string.</summary>
public sealed record FakeToolCall(string Name, string ArgumentsJson);

public sealed class FakeRule
{
    /// <summary>Tested against the content of the request's last message.</summary>
    public required Regex Match { get; init; }

    /// <summary>When set, the last message must also have this role (e.g. <c>tool</c>).</summary>
    public string? Role { get; init; }

    /// <summary>Tool calls to return after any response text (finish reason <c>tool_calls</c>).</summary>
    public IReadOnlyList<FakeToolCall> ToolCalls { get; init; } = Array.Empty<FakeToolCall>();
    public string? Response { get; init; }
    public int? ChunkSize { get; init; }
    public int DelayMs { get; init; }

    /// <summary>True when the rule declared a <c>usage</c> key (object or explicit null).</summary>
    public bool UsageProvided { get; init; }

    /// <summary>The usage object when provided and non-null; null when omitted or explicitly null.</summary>
    public FakeUsageSpec? Usage { get; init; }

    public FakeErrorSpec? Error { get; init; }

    /// <summary>For web search: the citations a matching query returns (null: not a web search rule).</summary>
    public IReadOnlyList<WebSearchResult>? Citations { get; init; }
}

public sealed class FakeScript
{
    public IReadOnlyList<FakeRule> Rules { get; }

    private FakeScript(IReadOnlyList<FakeRule> rules) => Rules = rules;

    public static FakeScript Empty { get; } = new(Array.Empty<FakeRule>());

    public static FakeScript Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    public static FakeScript Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Fake script must be a JSON object.");
        var rulesNode = root["rules"]?.AsArray() ?? new JsonArray();
        var rules = new List<FakeRule>();

        foreach (var item in rulesNode)
        {
            if (item is not JsonObject r) continue;
            var match = (string?)r["match"] ?? ".*";

            FakeErrorSpec? error = null;
            if (r["error"] is JsonObject e)
            {
                var type = ((string?)e["type"] ?? "http").ToLowerInvariant() switch
                {
                    "timeout" => FakeErrorType.Timeout,
                    "midstream" => FakeErrorType.Midstream,
                    "context_length" => FakeErrorType.ContextLength,
                    _ => FakeErrorType.Http,
                };
                error = new FakeErrorSpec
                {
                    Type = type,
                    Status = (int?)e["status"],
                    AfterChars = (int?)e["afterChars"],
                };
            }

            var usageProvided = r.ContainsKey("usage");
            FakeUsageSpec? usage = null;
            if (r["usage"] is JsonObject u)
            {
                usage = new FakeUsageSpec
                {
                    PromptTokens = (int?)u["promptTokens"] ?? 0,
                    CompletionTokens = (int?)u["completionTokens"] ?? 0,
                    CostUsd = (double?)u["costUsd"],
                };
            }

            var toolCalls = new List<FakeToolCall>();
            if (r["toolCalls"] is JsonArray calls)
            {
                foreach (var c in calls.OfType<JsonObject>())
                {
                    var name = (string?)c["name"]
                        ?? throw new InvalidOperationException("Fake script toolCalls entries need a name.");
                    var args = c["arguments"]?.ToJsonString() ?? "{}";
                    toolCalls.Add(new FakeToolCall(name, args));
                }
            }

            List<WebSearchResult>? citations = null;
            if (r["citations"] is JsonArray cites)
                citations = cites.OfType<JsonObject>()
                    .Select(c => new WebSearchResult((string?)c["title"] ?? "", (string?)c["url"] ?? "", (string?)c["content"] ?? ""))
                    .ToList();

            rules.Add(new FakeRule
            {
                Citations = citations,
                Match = new Regex(match, RegexOptions.Compiled),
                Role = (string?)r["role"],
                ToolCalls = toolCalls,
                Response = (string?)r["response"],
                ChunkSize = (int?)r["chunkSize"],
                DelayMs = (int?)r["delayMs"] ?? 0,
                UsageProvided = usageProvided,
                Usage = usage,
                Error = error,
            });
        }

        return new FakeScript(rules);
    }
}
