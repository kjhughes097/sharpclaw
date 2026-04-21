using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpClaw.Core;

namespace SharpClaw.Api.Tools;

/// <summary>
/// Web search tool provider using DuckDuckGo HTML search (no API key required).
/// </summary>
public sealed partial class WebSearchToolProvider : IToolProvider
{
    private readonly HttpClient _http;

    public string Name => "web-search";

    public WebSearchToolProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("SharpClaw/1.0 (Bot)");
    }

    public IReadOnlyList<ToolSchema> GetSchemas() =>
    [
        new ToolSchema("web_search", "Search the web using DuckDuckGo and return the top results.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Search query"},"max_results":{"type":"integer","description":"Maximum results to return (default 5)"}},"required":["query"]}"""),
    ];

    public async Task<ToolCallResult> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var args = doc.RootElement;
            var query = args.GetProperty("query").GetString()!;
            var maxResults = args.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 5;

            var results = await SearchAsync(query, maxResults, ct);
            return new ToolCallResult(results);
        }
        catch (Exception ex)
        {
            return new ToolCallResult($"Search error: {ex.Message}", IsError: true);
        }
    }

    private async Task<string> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var encoded = WebUtility.UrlEncode(query);
        var url = $"https://html.duckduckgo.com/html/?q={encoded}";

        var html = await _http.GetStringAsync(url, ct);

        var results = new List<string>();

        // Extract results from DDG HTML response
        var resultMatches = ResultPattern().Matches(html);
        foreach (Match match in resultMatches)
        {
            if (results.Count >= maxResults) break;

            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            var title = StripHtml(WebUtility.HtmlDecode(match.Groups[2].Value));

            // Skip DDG internal links
            if (href.Contains("duckduckgo.com")) continue;

            // Extract actual URL from DDG redirect
            if (href.Contains("uddg="))
            {
                var uddgMatch = UddgPattern().Match(href);
                if (uddgMatch.Success)
                    href = WebUtility.UrlDecode(uddgMatch.Groups[1].Value);
            }

            results.Add($"[{title}]({href})");
        }

        if (results.Count == 0)
            return "No results found.";

        return string.Join("\n\n", results);
    }

    private static string StripHtml(string html) =>
        HtmlTagPattern().Replace(html, "").Trim();

    [GeneratedRegex("""<a[^>]+href="([^"]+)"[^>]*class="result__a"[^>]*>(.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex ResultPattern();

    [GeneratedRegex("""uddg=([^&]+)""")]
    private static partial Regex UddgPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagPattern();
}
