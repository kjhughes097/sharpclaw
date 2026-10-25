using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SharpClaw.Gateway.Providers;

public sealed class FakeUsageSpec
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public double? CostUsd { get; init; }
}

public enum FakeErrorType { Http, Timeout, Midstream }

public sealed class FakeErrorSpec
{
    public FakeErrorType Type { get; init; }
    public int? Status { get; init; }
    public int? AfterChars { get; init; }
}

public sealed class FakeRule
{
    public required Regex Match { get; init; }
    public string? Response { get; init; }
    public int? ChunkSize { get; init; }
    public int DelayMs { get; init; }

    /// <summary>True when the rule declared a <c>usage</c> key (object or explicit null).</summary>
    public bool UsageProvided { get; init; }

    /// <summary>The usage object when provided and non-null; null when omitted or explicitly null.</summary>
    public FakeUsageSpec? Usage { get; init; }

    public FakeErrorSpec? Error { get; init; }
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

            rules.Add(new FakeRule
            {
                Match = new Regex(match, RegexOptions.Compiled),
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
