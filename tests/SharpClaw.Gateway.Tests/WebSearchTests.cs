using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Providers;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>web_search</c> in the gateway (external tools spec section 7.2).</summary>
public class WebSearchTests
{
    private const string Script = """
        {"rules":[
          {"match":"dotnet", "citations":[
              {"title":"Docs","url":"https://learn.microsoft.com/dotnet","content":"Official docs"},
              {"title":"Blog","url":"https://devblogs.microsoft.com/dotnet","content":"News"},
              {"title":"Repo","url":"https://github.com/dotnet/runtime","content":"Source"}],
           "usage":{"promptTokens":100,"completionTokens":20,"costUsd":0.004}},
          {"match":"expensive", "citations":[], "usage":{"promptTokens":1,"completionTokens":1,"costUsd":5.0}},
          {"match":"broken", "citations":[], "error":{"type":"http","status":503}},
          {"match":".*", "response":"chat rules do not answer searches"}
        ]}
        """;

    private static async Task<(GatewayFixture Fx, Turn Turn)> SetupAsync(double budget = 1.0)
    {
        var config = Config("assistant", BrokerTools.WebSearch);
        config["Fake:ScriptPath"] = ChatTestHelpers.WriteScript(Script);
        config["WebSearch:Model"] = "cheap/search-model";
        var fx = await GatewayAsync(config);
        return (fx, await fx.ClaimedTurnAsync(budget: budget));
    }

    [Fact]
    public async Task Returns_citations_and_records_the_call_against_the_budget()
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;

        var resp = await t.InvokeAsync(BrokerTools.WebSearch, new { query = "dotnet SECRET_QUERY", maxResults = 2 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var results = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results");
        Assert.Equal(new[] { "https://learn.microsoft.com/dotnet", "https://devblogs.microsoft.com/dotnet" },
            results.EnumerateArray().Select(r => r.GetProperty("url").GetString()));
        Assert.Equal("Official docs", results[0].GetProperty("excerpt").GetString());

        await using var db = fx.NewDb();
        var call = await db.LlmCalls.SingleAsync();
        Assert.Equal((LlmCallPurpose.WebSearch, "cheap/search-model", LlmCallStatus.Ok, 0.004, t.Corr),
            (call.Purpose, call.Model, call.Status, call.CostUsd, call.CorrelationId));
        var broker = await db.BrokerCalls.SingleAsync();
        Assert.Equal(("openrouter.ai", "succeeded"), (broker.TargetHost, broker.Status));
        Assert.DoesNotContain("SECRET_QUERY", JsonSerializer.Serialize(call) + JsonSerializer.Serialize(broker));
    }

    [Fact]
    public async Task Over_budget_searches_are_rejected_and_recorded()
    {
        var (fx, t) = await SetupAsync(budget: 1.0);
        await using var _ = fx;

        Assert.Equal(HttpStatusCode.OK, (await t.InvokeAsync(BrokerTools.WebSearch, new { query = "expensive" })).StatusCode);
        var rejected = await t.InvokeAsync(BrokerTools.WebSearch, new { query = "dotnet" });

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(ErrorCodes.BudgetExceeded, await ClientApiHelpers.ErrorCodeAsync(rejected));
        await using var db = fx.NewDb();
        Assert.Equal(new[] { LlmCallStatus.Ok, LlmCallStatus.Rejected },
            await db.LlmCalls.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).Select(c => c.Status).ToListAsync());
    }

    [Fact]
    public async Task A_provider_failure_is_a_tool_failure_and_an_error_row()
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;

        var resp = await t.InvokeAsync(BrokerTools.WebSearch, new { query = "broken" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        await using var db = fx.NewDb();
        var call = await db.LlmCalls.SingleAsync();
        Assert.Equal((LlmCallStatus.Error, ErrorCodes.ProviderError), (call.Status, call.ErrorCode));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"x","maxResults":11}""")]
    [InlineData("""{"query":"x","maxResults":"5"}""")]
    public async Task Bad_arguments_are_a_tool_error(string args)
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;

        var resp = await t.InvokeAsync(BrokerTools.WebSearch, JsonDocument.Parse(args).RootElement);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ErrorCodes.InvalidArguments, body.GetProperty("error").GetString());
        await using var db = fx.NewDb();
        Assert.Empty(await db.LlmCalls.ToListAsync());
    }

    [Fact]
    public void OpenRouter_annotations_are_reduced_to_capped_deduplicated_citations()
    {
        var json = $$$"""
            {"choices":[{"message":{"content":"…","annotations":[
              {"type":"url_citation","url_citation":{"url":"https://a.org/","title":"A","content":"{{{new string('x', 2000)}}}"}},
              {"type":"url_citation","url_citation":{"url":"https://a.org/","title":"A again","content":"dup"}},
              {"type":"file","file":{"name":"ignored"}},
              {"type":"url_citation","url_citation":{"url":"https://b.org/","content":"B"}},
              {"type":"url_citation","url_citation":{"url":"https://c.org/","title":"C"}}]}}],
             "usage":{"prompt_tokens":10,"completion_tokens":5,"cost":0.0123}}
            """;

        var parsed = OpenRouterWebSearch.Parse(JsonDocument.Parse(json).RootElement, maxResults: 2);

        Assert.Equal(new[] { "https://a.org/", "https://b.org/" }, parsed.Results.Select(r => r.Url));
        Assert.Equal(OpenRouterWebSearch.MaxExcerptChars, parsed.Results[0].Excerpt.Length);
        Assert.Equal("https://b.org/", parsed.Results[1].Title); // no title: the URL stands in
        Assert.Equal(new ChatStreamEvent.Usage(10, 5, 0.0123), parsed.Usage);
    }

    [Fact]
    public async Task OpenRouter_request_is_non_streaming_with_the_web_plugin()
    {
        string? sent = null;
        var http = new HttpClient(new Stub(async r =>
        {
            sent = await r.Content!.ReadAsStringAsync();
            Assert.Equal("Bearer or-key", r.Headers.Authorization!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"choices":[]}""", Encoding.UTF8, "application/json") };
        }));
        var search = new OpenRouterWebSearch(http, Options.Create(new OpenRouterOptions { ApiKey = "or-key", BaseUrl = "https://or.test/api/v1" }));

        await search.SearchAsync("cheap/model", "query", 3, default);

        var body = JsonDocument.Parse(sent!).RootElement;
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("cheap/model", body.GetProperty("model").GetString());
        Assert.Equal("""[{"id":"web","max_results":3}]""", body.GetProperty("plugins").GetRawText());
        Assert.False(body.TryGetProperty("tools", out _));
    }

    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
