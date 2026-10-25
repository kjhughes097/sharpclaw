using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

public class InboxAndMessagingTests
{
    [Fact]
    public async Task Inbox_claims_message_once_across_concurrent_pollers()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var threadId = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(threadId, "hello");

        // Many concurrent pollers; exactly one should get the message.
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var c = fx.AgentClient(token);
            var resp = await c.GetAsync("/agent/inbox?wait=0");
            return resp.StatusCode == HttpStatusCode.OK
                ? (await resp.Content.ReadFromJsonAsync<InboxMessageResponse>(SharpClawJson.Options))!.Message.Id
                : (Guid?)null;
        });
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public async Task Inbox_returns_204_on_timeout_and_200_when_message_arrives()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var threadId = await fx.CreateThreadAsync(agent.Id);

        using var c = fx.AgentClient(token);
        var empty = await c.GetAsync("/agent/inbox?wait=0");
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);

        // Long poll wakes promptly when a message is posted.
        var pollTask = c.GetAsync("/agent/inbox?wait=10");
        await Task.Delay(200);
        await fx.PostUserMessageAsync(threadId, "wake up");
        var resp = await pollTask;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Stale_claim_requeues_after_timeout()
    {
        await using var fx = await GatewayFixture.CreateAsync(
            extraConfig: new Dictionary<string, string?> { ["Inbox:ClaimTimeoutSeconds"] = "60" });
        var (agent, token) = await fx.CreateAgentAsync();
        var threadId = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(threadId, "hello");

        var claimed = await fx.ClaimAsync(token);
        Assert.NotNull(claimed);

        // Re-claim immediately: nothing pending.
        Assert.Null(await fx.ClaimAsync(token));

        // Advance past the claim timeout; the stale claim is requeued and can be claimed again.
        fx.Time.Advance(TimeSpan.FromSeconds(120));
        var reclaimed = await fx.ClaimAsync(token);
        Assert.NotNull(reclaimed);
        Assert.Equal(claimed!.Id, reclaimed!.Id);
    }

    [Fact]
    public async Task Reply_rejects_second_reply_and_wrong_agent()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync("assistant");
        var (other, otherToken) = await fx.CreateAgentAsync("other");
        var threadId = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(threadId, "hello");
        var claimed = await fx.ClaimAsync(token);

        using var agentClient = fx.AgentClient(token);
        var first = await agentClient.PostAsJsonAsync($"/agent/threads/{threadId}/messages",
            new AgentReplyRequest("hi there", claimed!.Id), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await agentClient.PostAsJsonAsync($"/agent/threads/{threadId}/messages",
            new AgentReplyRequest("again", claimed.Id), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // A different agent cannot reply to this thread/message.
        using var otherClient = fx.AgentClient(otherToken);
        var foreign = await otherClient.PostAsJsonAsync($"/agent/threads/{threadId}/messages",
            new AgentReplyRequest("nope", claimed.Id), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Fail_marks_message_and_emits_error_event()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var threadId = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(threadId, "hello");
        var claimed = await fx.ClaimAsync(token);

        // Subscribe to the thread stream before failing (no replay).
        using var streamClient = fx.ClientRaw();
        streamClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {GatewayFixture.AdminKey}");
        var streamResp = await streamClient.GetAsync($"/api/threads/{threadId}/stream",
            HttpCompletionOption.ResponseHeadersRead);
        var stream = await streamResp.Content.ReadAsStreamAsync();
        var readTask = TestHelpers.ReadSseEventsAsync(stream, 1, TimeSpan.FromSeconds(5));

        await Task.Delay(200);
        using var agentClient = fx.AgentClient(token);
        var fail = await agentClient.PostAsJsonAsync($"/agent/messages/{claimed!.Id}/fail",
            new FailMessageRequest("provider_error"), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, fail.StatusCode);

        var events = await readTask;
        Assert.Contains(events, e => e.Event == SseEventNames.Error && e.Data.Contains("provider_error"));

        using var db = fx.NewDb();
        var msg = await db.Messages.AsNoTracking().FirstAsync(m => m.Id == claimed.Id);
        Assert.Equal(MessageStatus.Failed, msg.Status);
        Assert.Equal("provider_error", msg.FailReason);
    }
}
