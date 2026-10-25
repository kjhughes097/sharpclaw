using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Approval brokering and the claim heartbeat (3.2 spec sections 8.1–8.3).</summary>
public class ApprovalTests
{
    private sealed record Setup(GatewayFixture Fx, AgentDto Agent, string Token, Guid Thread, Guid Corr);

    private static async Task<Setup> SetupAsync(IDictionary<string, string?>? config = null)
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;
        return new Setup(fx, agent, token, thread, corr);
    }

    private static CreateApprovalRequest Request(Setup s) =>
        new(s.Corr, s.Thread, "call_1", "workspace_delete", "delete notes/old.md", RiskLevels.High, "deletes a file");

    private static async Task<(HttpStatusCode Status, ApprovalDto? Approval)> CreateAsync(Setup s, CreateApprovalRequest req)
    {
        using var c = s.Fx.AgentClient(s.Token);
        var resp = await c.PostAsJsonAsync("/agent/approvals", req, SharpClawJson.Options);
        var body = resp.IsSuccessStatusCode
            ? (await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval
            : null;
        return (resp.StatusCode, body);
    }

    private static async Task<ApprovalDto> PollAsync(Setup s, Guid id, int wait = 0)
    {
        using var c = s.Fx.AgentClient(s.Token);
        return (await c.GetFromJsonAsync<ApprovalResponse>($"/agent/approvals/{id}?wait={wait}", SharpClawJson.Options))!.Approval;
    }

    private static async Task<HttpResponseMessage> DecideAsync(Setup s, Guid id, string decision)
    {
        using var admin = s.Fx.Admin();
        return await admin.PostAsJsonAsync($"/api/approvals/{id}", new DecideApprovalRequest(decision), SharpClawJson.Options);
    }

    /// <summary>Opens the thread stream (a watching human) and returns a reader for its events.</summary>
    private static async Task<(HttpClient Client, Task<List<(string Event, string Data)>> Events)> WatchAsync(Setup s, int count)
    {
        var client = s.Fx.Admin();
        var resp = await client.GetAsync($"/api/threads/{s.Thread}/stream", HttpCompletionOption.ResponseHeadersRead);
        var events = TestHelpers.ReadSseEventsAsync(await resp.Content.ReadAsStreamAsync(), count, TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        return (client, events);
    }

    [Fact]
    public async Task Nobody_watching_means_denied_immediately_by_system()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;

        var (status, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(ApprovalStatuses.Denied, approval!.Status);
        Assert.Equal(ApprovalDecidedBy.System, approval.DecidedBy);
    }

    [Fact]
    public async Task Watched_thread_gets_a_pending_approval_and_event_without_content()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (client, events) = await WatchAsync(s, 1);
        using var __ = client;

        var (_, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(ApprovalStatuses.Pending, approval!.Status);
        var evt = Assert.Single(await events);
        Assert.Equal(SseEventNames.ApprovalRequested, evt.Event);
        Assert.Contains(approval.Id.ToString(), evt.Data);
        Assert.Contains("delete notes/old.md", evt.Data);
        Assert.Contains("\"risk\":\"high\"", evt.Data);
    }

    [Theory]
    [InlineData(ApprovalStatuses.Approved)]
    [InlineData(ApprovalStatuses.Denied)]
    public async Task Human_decision_wakes_the_agent_long_poll(string decision)
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (client, _) = await WatchAsync(s, 1);
        using var __ = client;
        var (_, approval) = await CreateAsync(s, Request(s));

        var poll = PollAsync(s, approval!.Id, wait: 10);
        await Task.Delay(300);
        var decided = await DecideAsync(s, approval.Id, decision);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        var started = DateTime.UtcNow;
        var result = await poll;
        Assert.Equal(decision, result.Status);
        Assert.Equal(ApprovalDecidedBy.User, result.DecidedBy);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "long poll should return promptly");
    }

    [Fact]
    public async Task Pending_approval_expires_after_the_timeout_and_cannot_be_decided()
    {
        var s = await SetupAsync(new Dictionary<string, string?> { ["Approvals:TimeoutSeconds"] = "300" });
        await using var _ = s.Fx;
        var (client, _) = await WatchAsync(s, 1);
        using var __ = client;
        var (_, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(ApprovalStatuses.Pending, (await PollAsync(s, approval!.Id)).Status);
        s.Fx.Time.Advance(TimeSpan.FromSeconds(301));

        var expired = await PollAsync(s, approval.Id);
        Assert.Equal(ApprovalStatuses.Expired, expired.Status);
        Assert.Equal(ApprovalDecidedBy.System, expired.DecidedBy);
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(s, approval.Id, ApprovalStatuses.Approved)).StatusCode);
    }

    [Fact]
    public async Task Decisions_are_final_and_validated()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (client, _) = await WatchAsync(s, 1);
        using var __ = client;
        var (_, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(s, approval!.Id, "maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(s, approval.Id, ApprovalStatuses.Denied)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(s, approval.Id, ApprovalStatuses.Approved)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DecideAsync(s, Guid.NewGuid(), ApprovalStatuses.Approved)).StatusCode);
    }

    [Fact]
    public async Task Foreign_and_invalid_requests_are_rejected()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (client, _) = await WatchAsync(s, 1);
        using var __ = client;
        var (_, approval) = await CreateAsync(s, Request(s));
        var (_, otherToken) = await s.Fx.CreateAgentAsync("other");

        using (var other = s.Fx.AgentClient(otherToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/agent/approvals/{approval!.Id}?wait=0")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await other.PostAsJsonAsync("/agent/approvals", Request(s), SharpClawJson.Options)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(s, Request(s) with { Risk = "extreme" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateAsync(s, Request(s) with { Summary = new string('x', 201) })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CreateAsync(s, Request(s) with { CorrelationId = Guid.NewGuid() })).Status);
    }

    [Fact]
    public async Task Approval_polling_keeps_the_message_claimed()
    {
        var s = await SetupAsync(new Dictionary<string, string?> { ["Inbox:ClaimTimeoutSeconds"] = "60" });
        await using var _ = s.Fx;
        var (client, _) = await WatchAsync(s, 1);
        using var __ = client;
        var (_, approval) = await CreateAsync(s, Request(s));

        // 50 s + 50 s is past the 60 s claim timeout, but each poll refreshes the claim.
        s.Fx.Time.Advance(TimeSpan.FromSeconds(50));
        await PollAsync(s, approval!.Id);
        s.Fx.Time.Advance(TimeSpan.FromSeconds(50));

        Assert.Null(await s.Fx.ClaimAsync(s.Token));
        using var db = s.Fx.NewDb();
        Assert.Equal(MessageStatus.Claimed, (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == s.Corr)).Status);
    }

    [Fact]
    public async Task Chat_calls_keep_the_message_claimed()
    {
        var s = await SetupAsync(new Dictionary<string, string?> { ["Inbox:ClaimTimeoutSeconds"] = "60" });
        await using var _ = s.Fx;

        s.Fx.Time.Advance(TimeSpan.FromSeconds(50));
        await (await s.Fx.ChatAsync(s.Token, s.Corr, s.Thread)).Content.ReadAsStringAsync();
        s.Fx.Time.Advance(TimeSpan.FromSeconds(50));

        Assert.Null(await s.Fx.ClaimAsync(s.Token));
    }
}
