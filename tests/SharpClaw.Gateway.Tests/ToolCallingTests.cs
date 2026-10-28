using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Tool calling through <c>/v1/chat/completions</c> (3.2 spec sections 5 and 8.1).</summary>
public class ToolCallingTests
{
    private const string ToolScript = """
    { "rules": [
      { "match": "^please write$", "role": "user", "response": "On it.",
        "toolCalls": [ { "name": "workspace_write", "arguments": { "path": "notes/a.md", "content": "SECRET_ARG" } } ] },
      { "match": "\"ok\":true", "role": "tool", "response": "Done writing." }
    ] }
    """;

    private static readonly object[] Tools =
    {
        new
        {
            type = "function",
            function = new
            {
                name = "workspace_write",
                description = "Write a file",
                parameters = new { type = "object", properties = new { path = new { type = "string" } } },
            },
        },
    };

    private static async Task<(GatewayFixture fx, string token, Guid thread, Guid corr)> SetupAsync(string userText)
    {
        var script = ChatTestHelpers.WriteScript(ToolScript);
        var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, userText);
        var corr = (await fx.ClaimAsync(token))!.Id;
        return (fx, token, thread, corr);
    }

    [Fact]
    public async Task Tool_calls_stream_as_complete_chunks_with_tool_calls_finish_reason()
    {
        var (fx, token, thread, corr) = await SetupAsync("please write");
        await using var _ = fx;

        var resp = await fx.ChatRawAsync(token, corr, thread, new
        {
            stream = true,
            tools = Tools,
            tool_choice = "auto",
            messages = new[] { new { role = "user", content = "please write" } },
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();

        var (calls, finish) = ChatTestHelpers.ParseToolCalls(body);
        var call = Assert.Single(calls);
        Assert.Equal(0, call.Index);
        Assert.StartsWith("call_", call.Id);
        Assert.Equal("workspace_write", call.Name);
        Assert.Contains("notes/a.md", call.Arguments);
        Assert.Equal("tool_calls", finish);
        Assert.Equal("On it.", ChatTestHelpers.ParseChat(body).Content);
    }

    [Fact]
    public async Task Tool_result_round_trip_is_accepted()
    {
        var (fx, token, thread, corr) = await SetupAsync("please write");
        await using var _ = fx;

        var resp = await fx.ChatRawAsync(token, corr, thread, new
        {
            stream = true,
            tools = Tools,
            messages = new object[]
            {
                new { role = "user", content = "please write" },
                new
                {
                    role = "assistant",
                    content = (string?)null,
                    tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "workspace_write", arguments = "{\"path\":\"notes/a.md\"}" } } },
                },
                new { role = "tool", tool_call_id = "call_1", content = "{\"ok\":true}" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Equal("Done writing.", ChatTestHelpers.ParseChat(body).Content);
        Assert.Equal("stop", ChatTestHelpers.ParseToolCalls(body).FinishReason);
    }

    [Fact]
    public async Task Tool_requested_event_is_published_without_arguments()
    {
        var (fx, token, thread, corr) = await SetupAsync("please write");
        await using var _ = fx;

        using var streamClient = fx.Admin();
        var streamResp = await streamClient.GetAsync($"/api/threads/{thread}/stream", HttpCompletionOption.ResponseHeadersRead);
        var readTask = TestHelpers.ReadSseEventsAsync(await streamResp.Content.ReadAsStreamAsync(), 2, TimeSpan.FromSeconds(5));

        await fx.ChatRawAsync(token, corr, thread, new
        {
            stream = true,
            tools = Tools,
            messages = new[] { new { role = "user", content = "please write" } },
        });

        var events = await readTask;
        var requested = Assert.Single(events, e => e.Event == SseEventNames.ToolRequested);
        Assert.Contains("workspace_write", requested.Data);
        Assert.DoesNotContain("SECRET_ARG", requested.Data);
        Assert.DoesNotContain("notes/a.md", requested.Data);
    }

    [Fact]
    public async Task Risk_assessment_purpose_suppresses_thread_events_and_is_recorded()
    {
        var (fx, token, thread, corr) = await SetupAsync("hello");
        await using var _ = fx;

        using var streamClient = fx.Admin();
        var streamResp = await streamClient.GetAsync($"/api/threads/{thread}/stream", HttpCompletionOption.ResponseHeadersRead);
        var readTask = TestHelpers.ReadSseEventsAsync(await streamResp.Content.ReadAsStreamAsync(), 1, TimeSpan.FromSeconds(2));

        var resp = await fx.ChatRawAsync(token, corr, thread,
            new { stream = true, messages = new[] { new { role = "user", content = "classify this" } } },
            purpose: LlmCallPurpose.RiskAssessment);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Fake reply to", ChatTestHelpers.ParseChat(await resp.Content.ReadAsStringAsync()).Content);

        Assert.Empty(await readTask);
        using var db = fx.NewDb();
        Assert.Equal(LlmCallPurpose.RiskAssessment, (await db.LlmCalls.AsNoTracking().SingleAsync()).Purpose);
    }

    [Fact]
    public async Task Chat_purpose_is_the_default()
    {
        var (fx, token, thread, corr) = await SetupAsync("hello");
        await using var _ = fx;

        await (await fx.ChatAsync(token, corr, thread)).Content.ReadAsStringAsync();

        using var db = fx.NewDb();
        Assert.Equal(LlmCallPurpose.Chat, (await db.LlmCalls.AsNoTracking().SingleAsync()).Purpose);
    }

    [Theory]
    [InlineData("""{ "stream": true, "tool_choice": "required", "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "tools": [ { "type": "retrieval" } ], "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "tools": [ { "type": "function", "function": { "name": "bad name!" } } ], "messages": [ { "role": "user", "content": "x" } ] }""")]
    [InlineData("""{ "stream": true, "messages": [ { "role": "tool", "content": "x" } ] }""")]
    public async Task Invalid_tool_requests_return_400(string json)
    {
        var (fx, token, thread, corr) = await SetupAsync("hello");
        await using var _ = fx;

        using var c = fx.AgentClient(token);
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", corr.ToString());
        req.Headers.TryAddWithoutValidation("X-Thread-Id", thread.ToString());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Unknown_purpose_returns_400()
    {
        var (fx, token, thread, corr) = await SetupAsync("hello");
        await using var _ = fx;

        var resp = await fx.ChatRawAsync(token, corr, thread,
            new { stream = true, messages = new[] { new { role = "user", content = "x" } } }, purpose: "sneaky");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
