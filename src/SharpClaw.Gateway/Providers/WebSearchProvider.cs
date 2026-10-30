using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Providers;

public sealed record WebSearchResult(string Title, string Url, string Excerpt);

/// <summary>The citations found and what the call cost (null when the provider didn't say).</summary>
public sealed record WebSearchResponse(IReadOnlyList<WebSearchResult> Results, ChatStreamEvent.Usage? Usage);

/// <summary>
/// One web search (external tools spec section 7.2): a separate, non-streaming completion with
/// OpenRouter's <c>web</c> plugin, reduced to its <c>url_citation</c> annotations. Throws
/// <see cref="ProviderException"/> like the chat provider.
/// </summary>
public interface IWebSearchProvider
{
    Task<WebSearchResponse> SearchAsync(string model, string query, int maxResults, CancellationToken ct);
}

public sealed class OpenRouterWebSearch(HttpClient http, IOptions<OpenRouterOptions> options) : IWebSearchProvider
{
    public const int MaxExcerptChars = 1000;

    private const string Instructions =
        "Search the web for pages relevant to the user's query and answer in one or two sentences, citing your sources.";

    public async Task<WebSearchResponse> SearchAsync(string model, string query, int maxResults, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["model"] = model,
            ["stream"] = false,
            ["messages"] = new object[]
            {
                new Dictionary<string, string> { ["role"] = "system", ["content"] = Instructions },
                new Dictionary<string, string> { ["role"] = "user", ["content"] = query },
            },
            ["plugins"] = new object[] { new Dictionary<string, object> { ["id"] = "web", ["max_results"] = maxResults } },
            ["usage"] = new Dictionary<string, bool> { ["include"] = true },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl}/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {options.Value.ApiKey}");
        request.Headers.TryAddWithoutValidation("X-Title", "SharpClaw");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException(ProviderErrorKind.Upstream, null, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(ProviderErrorKind.Timeout, null, "web search timeout");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Upstream, (int)response.StatusCode, "upstream non-2xx");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return Parse(doc.RootElement, maxResults);
        }
    }

    /// <summary>Typed reduction of the response: citations (deduplicated by URL, capped) and usage. Nothing else is read.</summary>
    public static WebSearchResponse Parse(JsonElement root, int maxResults)
    {
        var results = new List<WebSearchResult>();
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("message", out var message) ||
                    !message.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in annotations.EnumerateArray())
                {
                    if (Str(a, "type") != "url_citation" || !a.TryGetProperty("url_citation", out var c))
                        continue;
                    var url = Str(c, "url");
                    if (string.IsNullOrEmpty(url) || results.Any(r => r.Url == url) || results.Count >= maxResults)
                        continue;
                    results.Add(new WebSearchResult(Cap(Str(c, "title") ?? url, 300), Cap(url, 2048), Cap(Str(c, "content") ?? "", MaxExcerptChars)));
                }
            }
        }

        ChatStreamEvent.Usage? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            var prompt = u.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
            var completion = u.TryGetProperty("completion_tokens", out var co) && co.TryGetInt32(out var cv) ? cv : 0;
            double? cost = u.TryGetProperty("cost", out var cost0) && cost0.ValueKind == JsonValueKind.Number ? cost0.GetDouble() : null;
            usage = new ChatStreamEvent.Usage(prompt, completion, cost);
        }
        return new WebSearchResponse(results, usage);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// Offline web search for tests and the dev stack. A script rule with <c>citations</c> whose
/// <c>match</c> fits the query answers it (with its <c>usage</c> or <c>error</c>); otherwise one
/// fixed example result.
/// </summary>
public sealed class FakeWebSearch : IWebSearchProvider
{
    private readonly FakeScript _script;
    private readonly double _costPerToken;

    public FakeWebSearch(IOptions<FakeOptions> options)
    {
        var opt = options.Value;
        _costPerToken = opt.CostPerTokenUsd;
        _script = !string.IsNullOrWhiteSpace(opt.ScriptPath) && File.Exists(opt.ScriptPath)
            ? FakeScript.Load(opt.ScriptPath)
            : FakeScript.Empty;
    }

    public Task<WebSearchResponse> SearchAsync(string model, string query, int maxResults, CancellationToken ct)
    {
        var rule = _script.Rules.FirstOrDefault(r => r.Citations is not null && r.Match.IsMatch(query));
        if (rule?.Error is { Type: FakeErrorType.Timeout })
            throw new ProviderException(ProviderErrorKind.Timeout, null, "fake timeout");
        if (rule?.Error is not null)
            throw new ProviderException(ProviderErrorKind.Upstream, rule.Error.Status ?? 500, "fake http error");

        var results = rule?.Citations?.Take(maxResults).ToList()
            ?? [new WebSearchResult("Example Domain", "https://example.com/", "This domain is for use in documentation examples.")];
        var usage = rule is { UsageProvided: true }
            ? rule.Usage is { } u ? new ChatStreamEvent.Usage(u.PromptTokens, u.CompletionTokens, u.CostUsd) : null
            : new ChatStreamEvent.Usage(query.Length, 20, (query.Length + 20) * _costPerToken);
        return Task.FromResult(new WebSearchResponse(results, usage));
    }
}
