using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>The global <c>/api/events</c> stream the UI uses (3.3 spec section 5.7).</summary>
public class GlobalStreamTests
{
    [Fact]
    public async Task Global_stream_carries_every_threads_events_with_thread_ids()
    {
        var script = ChatTestHelpers.WriteScript("""
            { "rules": [ { "match": "go", "response": "abcdefgh", "chunkSize": 4 } ] }
            """);
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        var (agent, token) = await fx.CreateAgentAsync();
        var a = await fx.CreateThreadAsync(agent.Id);
        var b = await fx.CreateThreadAsync(agent.Id);

        var (client, stream) = await fx.OpenStreamAsync("/api/events");
        using var _ = client;
        // Per thread: message.created, 2 deltas, message.completed.
        var read = TestHelpers.ReadSseAsync(stream, 8, TimeSpan.FromSeconds(8), skipReady: true);

        foreach (var thread in new[] { a, b })
        {
            var corr = await fx.PostUserMessageAsync(thread, "go");
            await fx.ClaimAsync(token);
            await (await fx.ChatAsync(token, corr, thread, "go")).Content.ReadAsStringAsync();
            using var agentClient = fx.AgentClient(token);
            (await agentClient.PostAsJsonAsync($"/agent/threads/{thread}/messages",
                new AgentReplyRequest("abcdefgh", corr), SharpClawJson.Options)).EnsureSuccessStatusCode();
        }

        var events = await read;
        Assert.Equal(8, events.Count);
        foreach (var thread in new[] { a, b })
        {
            var mine = events.Where(e => (Guid?)JsonNode.Parse(e.Data)!["threadId"] == thread).Select(e => e.Event);
            Assert.Equal(new[] { SseEventNames.MessageCreated, SseEventNames.Delta, SseEventNames.Delta, SseEventNames.MessageCompleted }, mine);
        }
    }

    [Fact]
    public async Task Global_connect_sends_snapshots_for_in_flight_turns_across_threads()
    {
        var script = ChatTestHelpers.WriteScript("""
            { "rules": [ { "match": "slow", "response": "abcdefghij", "chunkSize": 2, "delayMs": 300 } ] }
            """);
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "slow");
        await fx.ClaimAsync(token);
        var chat = Task.Run(async () => await (await fx.ChatAsync(token, corr, thread, "slow")).Content.ReadAsStringAsync());
        await TestHelpers.WaitForStreamedTextAsync(fx, thread);

        var (client, stream) = await fx.OpenStreamAsync("/api/events");
        using var _ = client;
        var events = await TestHelpers.ReadSseAsync(stream, 2, TimeSpan.FromSeconds(4));
        await chat;

        Assert.Equal(SseEventNames.Ready, events[0].Event);
        Assert.Null((Guid?)JsonNode.Parse(events[0].Data)!["threadId"]);
        Assert.Equal(SseEventNames.Snapshot, events[1].Event);
        var snapshot = ClientApiHelpers.Read<SnapshotEvent>(events[1].Data);
        Assert.Equal(thread, snapshot.ThreadId);
        Assert.Equal(corr, snapshot.CorrelationId);
    }

    [Fact]
    public async Task Global_stream_replays_from_last_event_id_and_is_never_compressed()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var first = await fx.CreateThreadAsync(agent.Id);
        long seen;
        using (var db = fx.NewDb())
            seen = db.ThreadEvents.Max(e => e.Id);
        var second = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(first, "hi");

        using var client = fx.Admin();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", seen.ToString());
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(resp.Content.Headers.ContentEncoding);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var events = await TestHelpers.ReadSseAsync(await resp.Content.ReadAsStreamAsync(), 3, TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { SseEventNames.ThreadCreated, SseEventNames.MessageCreated, SseEventNames.Ready },
            events.Select(e => e.Event));
        Assert.Equal(second, ClientApiHelpers.Read<ThreadCreatedEvent>(events[0].Data).ThreadId);
        Assert.Equal(first, ClientApiHelpers.Read<MessageCreatedEvent>(events[1].Data).ThreadId);
    }

    [Fact]
    public async Task Approval_and_error_events_carry_the_thread_id()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "x");
        await fx.ClaimAsync(token);

        var (client, stream) = await fx.OpenStreamAsync("/api/events");
        using var _ = client;
        var read = TestHelpers.ReadSseAsync(stream, 3, TimeSpan.FromSeconds(5), skipReady: true);
        await fx.RequestApprovalAsync(token, thread, corr);
        using (var agentClient = fx.AgentClient(token))
            (await agentClient.PostAsJsonAsync($"/agent/messages/{corr}/fail", new FailMessageRequest("agent_error"),
                SharpClawJson.Options)).EnsureSuccessStatusCode();

        var events = await read;
        Assert.Equal(new[] { SseEventNames.ApprovalRequested, SseEventNames.ApprovalDecided, SseEventNames.Error },
            events.Select(e => e.Event));
        Assert.All(events, e => Assert.Equal(thread, (Guid?)JsonNode.Parse(e.Data)!["threadId"]));

        // The failed message carries its reason, for the UI's failure notice.
        using var admin = fx.Admin();
        var list = await admin.GetFromJsonAsync<MessageListResponse>($"/api/threads/{thread}/messages", SharpClawJson.Options);
        var failed = Assert.Single(list!.Messages);
        Assert.Equal(MessageStatus.Failed, failed.Status);
        Assert.Equal("agent_error", failed.FailReason);
    }
}

/// <summary>Every 3.3 endpoint needs client credentials and the client listener.</summary>
public class ClientApiAuthTests
{
    [Fact]
    public async Task New_endpoints_require_auth_and_reject_the_agent_listener()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var paths = new[]
        {
            "/api/threads", $"/api/threads/{thread}", $"/api/threads/{thread}/trace", "/api/system",
            "/api/audit/tool-calls", "/api/audit/llm-calls", "/api/events",
        };

        using var anonymous = fx.ClientRaw();
        using var agentPort = fx.AgentClient(token);
        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await agentPort.GetAsync(path)).StatusCode);
        }

        var patch = await anonymous.PatchAsJsonAsync($"/api/threads/{thread}", new UpdateThreadRequest("x", null), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Unauthorized, patch.StatusCode);
    }

    [Fact]
    public async Task Cookie_patch_needs_the_csrf_token()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        using (var admin = fx.Admin())
            (await admin.PostAsJsonAsync("/api/users", new CreateUserRequest("kevin", "correct horse battery staple"),
                SharpClawJson.Options)).EnsureSuccessStatusCode();

        var cookies = new CookieContainer();
        using var browser = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(fx.ClientBaseUrl) };
        browser.DefaultRequestHeaders.Add("Origin", fx.ClientBaseUrl);
        var login = await browser.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("kevin", "correct horse battery staple"), SharpClawJson.Options);
        var csrf = (await login.Content.ReadFromJsonAsync<LoginResponse>(SharpClawJson.Options))!.CsrfToken;

        var without = await browser.PatchAsJsonAsync($"/api/threads/{thread}", new UpdateThreadRequest("x", null), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Equal(ErrorCodes.CsrfFailed, await ClientApiHelpers.ErrorCodeAsync(without));

        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/threads/{thread}")
        {
            Content = JsonContent.Create(new UpdateThreadRequest("x", null), options: SharpClawJson.Options),
        };
        request.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(request)).StatusCode);
    }
}
