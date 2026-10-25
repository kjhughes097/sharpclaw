using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

public class ChatCompletionsTests
{
    private static async Task<(GatewayFixture fx, AgentDto agent, string token, Guid thread, Guid corr)>
        SetupAsync(IDictionary<string, string?>? extra = null, double budget = 100.0)
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: extra);
        var (agent, token) = await fx.CreateAgentAsync(budget: budget);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var claimed = await fx.ClaimAsync(token);
        return (fx, agent, token, thread, claimed!.Id);
    }

    [Fact]
    public async Task Happy_path_streams_openai_sse_with_usage()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;

        var resp = await fx.ChatAsync(token, corr, thread);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        var parsed = ChatTestHelpers.ParseChat(body);
        Assert.Equal("Fake reply to: hello", parsed.Content);
        Assert.True(parsed.HasUsage);
        Assert.True(parsed.HasDone);
        Assert.Equal(agent.Model, parsed.Model);

        using var db = fx.NewDb();
        var call = await db.LlmCalls.AsNoTracking().SingleAsync();
        Assert.Equal(LlmCallStatus.Ok, call.Status);
        Assert.True(call.UsageKnown);
        Assert.True(call.CompletionTokens > 0);
    }

    [Fact]
    public async Task Configured_model_used_regardless_of_request_model()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;

        var resp = await fx.ChatAsync(token, corr, thread, model: "some/other-model");
        var body = await resp.Content.ReadAsStringAsync();
        var parsed = ChatTestHelpers.ParseChat(body);
        Assert.Equal(agent.Model, parsed.Model);
    }

    [Fact]
    public async Task Stream_false_and_tools_rejected()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;

        var noStream = await fx.ChatAsync(token, corr, thread, stream: false);
        Assert.Equal(HttpStatusCode.BadRequest, noStream.StatusCode);

        var withTools = await fx.ChatAsync(token, corr, thread, includeTools: true);
        Assert.Equal(HttpStatusCode.BadRequest, withTools.StatusCode);
    }

    [Fact]
    public async Task Missing_headers_rejected()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;

        using var c = fx.AgentClient(token);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { stream = true, messages = new[] { new { role = "user", content = "x" } } }),
        };
        var resp = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Usage_unknown_records_usage_known_false_and_no_usage_chunk()
    {
        var script = ChatTestHelpers.WriteScript("""
        { "rules": [ { "match": ".*", "response": "hi", "usage": null } ] }
        """);
        var (fx, agent, token, thread, corr) = await SetupAsync(
            new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        await using var _ = fx;

        var resp = await fx.ChatAsync(token, corr, thread);
        var body = await resp.Content.ReadAsStringAsync();
        var parsed = ChatTestHelpers.ParseChat(body);
        Assert.False(parsed.HasUsage);
        Assert.True(parsed.HasDone);

        using var db = fx.NewDb();
        var call = await db.LlmCalls.AsNoTracking().SingleAsync();
        Assert.False(call.UsageKnown);
        Assert.Equal(0, call.CostUsd);
    }

    [Fact]
    public async Task Provider_error_before_streaming_returns_502_and_records_error()
    {
        var script = ChatTestHelpers.WriteScript("""
        { "rules": [ { "match": "^fail-http$", "error": { "type": "http", "status": 500 } } ] }
        """);
        var (fx, agent, token, thread, corr) = await SetupAsync(
            new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        await using var _ = fx;

        var resp = await fx.ChatAsync(token, corr, thread, userText: "fail-http");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);

        using var db = fx.NewDb();
        var call = await db.LlmCalls.AsNoTracking().SingleAsync();
        Assert.Equal(LlmCallStatus.Error, call.Status);
        Assert.Equal(ErrorCodes.ProviderError, call.ErrorCode);
    }

    [Fact]
    public async Task Provider_timeout_before_streaming_returns_504()
    {
        var script = ChatTestHelpers.WriteScript("""
        { "rules": [ { "match": "^fail-timeout$", "error": { "type": "timeout" } } ] }
        """);
        var (fx, agent, token, thread, corr) = await SetupAsync(
            new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        await using var _ = fx;

        var resp = await fx.ChatAsync(token, corr, thread, userText: "fail-timeout");
        Assert.Equal(HttpStatusCode.GatewayTimeout, resp.StatusCode);
    }

    [Fact]
    public async Task Midstream_failure_emits_error_chunk_then_done()
    {
        var script = ChatTestHelpers.WriteScript("""
        { "rules": [ { "match": "^fail-midstream$", "response": "partial", "error": { "type": "midstream", "afterChars": 4 } } ] }
        """);
        var (fx, agent, token, thread, corr) = await SetupAsync(
            new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        await using var _ = fx;

        var resp = await fx.ChatAsync(token, corr, thread, userText: "fail-midstream");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // already streaming
        var body = await resp.Content.ReadAsStringAsync();
        var parsed = ChatTestHelpers.ParseChat(body);
        Assert.True(parsed.HasErrorChunk);
        Assert.True(parsed.HasDone);

        using var db = fx.NewDb();
        var call = await db.LlmCalls.AsNoTracking().SingleAsync();
        Assert.Equal(LlmCallStatus.Error, call.Status);
    }

    [Fact]
    public async Task Deltas_reach_thread_sse_subscribers()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;

        using var streamClient = fx.ClientRaw();
        streamClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GatewayFixture.AdminKey}");
        var streamResp = await streamClient.GetAsync($"/api/threads/{thread}/stream",
            HttpCompletionOption.ResponseHeadersRead);
        var stream = await streamResp.Content.ReadAsStreamAsync();
        var readTask = TestHelpers.ReadSseEventsAsync(stream, 1, TimeSpan.FromSeconds(5));

        await Task.Delay(200);
        var resp = await fx.ChatAsync(token, corr, thread);
        await resp.Content.ReadAsStringAsync();

        var events = await readTask;
        Assert.Contains(events, e => e.Event == SseEventNames.Delta);
    }
}
