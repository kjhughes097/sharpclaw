using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        return (client, events);
    }

    [Fact]
    public async Task Nobody_watching_still_gets_a_pending_approval()
    {
        // Supersedes 3.2 §8.2 ("no subscriber → denied"): pre-3.3 §9.1.
        var s = await SetupAsync();
        await using var _ = s.Fx;

        var (status, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal(ApprovalStatuses.Pending, approval!.Status);
        Assert.Null(approval.DecidedBy);
    }

    [Fact]
    public async Task Unattended_message_gets_an_approval_denied_by_the_system()
    {
        var fx = await GatewayFixture.CreateAsync();
        await using var _ = fx;
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello", unattended: true);
        var corr = (await fx.ClaimAsync(token))!.Id;
        var s = new Setup(fx, agent, token, thread, corr);

        var (_, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(ApprovalStatuses.Denied, approval!.Status);
        Assert.Equal(ApprovalDecidedBy.System, approval.DecidedBy);
        using var db = fx.NewDb();
        Assert.True((await db.Messages.AsNoTracking().SingleAsync(m => m.Id == corr)).Unattended);
    }

    private static async Task<List<ApprovalDetailDto>> ListAsync(Setup s, string query = "")
    {
        using var admin = s.Fx.Admin();
        return (await admin.GetFromJsonAsync<ApprovalListResponse>($"/api/approvals{query}", SharpClawJson.Options))!.Approvals.ToList();
    }

    [Fact]
    public async Task A_decided_approval_leaves_no_wake_up_signal_behind()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var signals = s.Fx.App.Services.GetRequiredService<SharpClaw.Gateway.Services.ApprovalSignal>();
        var (_, approval) = await CreateAsync(s, Request(s));

        var poll = PollAsync(s, approval!.Id, wait: 5);
        await TestHelpers.EventuallyAsync(() => signals.Count == 1);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(s, approval.Id, ApprovalStatuses.Approved)).StatusCode);

        Assert.Equal(ApprovalStatuses.Approved, (await poll).Status);
        Assert.Equal(0, signals.Count);
        // A late poll of the decided approval answers at once and doesn't re-create the key.
        Assert.Equal(ApprovalStatuses.Approved, (await PollAsync(s, approval.Id, wait: 5)).Status);
        Assert.Equal(0, signals.Count);
    }

    [Fact]
    public async Task Queue_lists_and_gets_approvals_with_filters()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));

        var pending = Assert.Single(await ListAsync(s, "?status=pending&agent=assistant"));
        Assert.Equal(approval!.Id, pending.Id);
        Assert.Equal("assistant", pending.Agent);
        Assert.Equal("workspace_delete", pending.ToolName);
        Assert.Equal("delete notes/old.md", pending.Summary);
        Assert.Equal(s.Thread, pending.ThreadId);
        Assert.Empty(await ListAsync(s, "?status=approved"));

        using var admin = s.Fx.Admin();
        var one = await admin.GetFromJsonAsync<ApprovalDetailResponse>($"/api/approvals/{approval.Id}", SharpClawJson.Options);
        Assert.Equal(ApprovalStatuses.Pending, one!.Approval.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/approvals/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/approvals?status=maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/approvals?agent=nobody")).StatusCode);
    }

    [Fact]
    public async Task Sweeper_expires_on_schedule_wakes_the_agent_and_emits_decided()
    {
        var s = await SetupAsync(new Dictionary<string, string?>
        {
            ["Approvals:TimeoutSeconds"] = "60",
            ["Approvals:SweepIntervalSeconds"] = "10",
        });
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));
        var (client, stream) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream");
        using var __ = client;
        var events = TestHelpers.ReadSseEventsAsync(stream, 1, TimeSpan.FromSeconds(10));
        var poll = PollAsync(s, approval!.Id, wait: 10);
        await TestHelpers.WaitForLongPollAsync<SharpClaw.Gateway.Services.ApprovalSignal>(s.Fx);

        // The sweeper's PeriodicTimer runs on the fake clock.
        s.Fx.Time.Advance(TimeSpan.FromSeconds(61));

        var result = await poll;
        Assert.Equal(ApprovalStatuses.Expired, result.Status);
        Assert.Equal(ApprovalDecidedBy.System, result.DecidedBy);
        var decided = Assert.Single(await events);
        Assert.Equal(SseEventNames.ApprovalDecided, decided.Event);
        Assert.Contains("\"status\":\"expired\"", decided.Data);
        Assert.Contains("\"decidedBy\":\"system\"", decided.Data);
    }

    [Fact]
    public async Task Sweep_once_only_touches_overdue_approvals()
    {
        var s = await SetupAsync(new Dictionary<string, string?> { ["Approvals:TimeoutSeconds"] = "60" });
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));
        var sweeper = s.Fx.App.Services.GetRequiredService<SharpClaw.Gateway.Services.ApprovalExpirySweeper>();

        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None));
        using (var db = s.Fx.NewDb())
            await db.Approvals.Where(a => a.Id == approval!.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(a => a.ExpiresAt, s.Fx.Time.GetUtcNow().UtcDateTime.AddSeconds(-1)));
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));
        Assert.Equal(ApprovalStatuses.Expired, (await PollAsync(s, approval!.Id)).Status);
    }

    [Fact]
    public async Task Fail_expires_pending_approvals_of_the_turn()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));

        using (var agent = s.Fx.AgentClient(s.Token))
            (await agent.PostAsJsonAsync($"/agent/messages/{s.Corr}/fail", new FailMessageRequest("agent_error"), SharpClawJson.Options))
                .EnsureSuccessStatusCode();

        var expired = await PollAsync(s, approval!.Id);
        Assert.Equal(ApprovalStatuses.Expired, expired.Status);
        Assert.Equal(ApprovalDecidedBy.System, expired.DecidedBy);
    }

    [Fact]
    public async Task Reply_expires_pending_approvals_of_the_turn()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));

        using (var agent = s.Fx.AgentClient(s.Token))
            (await agent.PostAsJsonAsync($"/agent/threads/{s.Thread}/messages", new AgentReplyRequest("done", s.Corr), SharpClawJson.Options))
                .EnsureSuccessStatusCode();

        Assert.Equal(ApprovalStatuses.Expired, (await PollAsync(s, approval!.Id)).Status);
    }

    [Fact]
    public async Task Claim_timeout_requeue_expires_pending_approvals_of_the_turn()
    {
        var s = await SetupAsync(new Dictionary<string, string?>
        {
            ["Inbox:ClaimTimeoutSeconds"] = "60",
            ["Approvals:TimeoutSeconds"] = "600",
        });
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));

        s.Fx.Time.Advance(TimeSpan.FromSeconds(61));
        var reclaimed = await s.Fx.ClaimAsync(s.Token);

        Assert.Equal(s.Corr, reclaimed!.Id);
        using var db = s.Fx.NewDb();
        var row = await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == approval!.Id);
        Assert.Equal(ApprovalStatuses.Expired, row.Status);
        Assert.Equal(ApprovalDecidedBy.System, row.DecidedBy);
    }

    [Fact]
    public async Task Concurrent_decisions_have_exactly_one_winner()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            DecideAsync(s, approval!.Id, i % 2 == 0 ? ApprovalStatuses.Approved : ApprovalStatuses.Denied)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    private sealed class RecordingNotifier : SharpClaw.Gateway.Services.IApprovalNotifier
    {
        public List<SharpClaw.Gateway.Services.ApprovalNotification> Calls { get; } = new();
        public bool Throw { get; set; }

        public Task NotifyRequestedAsync(SharpClaw.Gateway.Services.ApprovalNotification approval, CancellationToken ct)
        {
            Calls.Add(approval);
            if (Throw)
                throw new InvalidOperationException("SECRET notifier failure");
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notifier_is_called_for_pending_approvals_only_and_cannot_break_the_request(bool throws)
    {
        var notifier = new RecordingNotifier { Throw = throws };
        var fx = await GatewayFixture.CreateAsync(configure: b =>
            b.Services.AddSingleton<SharpClaw.Gateway.Services.IApprovalNotifier>(notifier));
        await using var _ = fx;
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "a");
        var s = new Setup(fx, agent, token, thread, (await fx.ClaimAsync(token))!.Id);

        var (status, approval) = await CreateAsync(s, Request(s));

        Assert.Equal(HttpStatusCode.Created, status);
        var call = Assert.Single(notifier.Calls);
        Assert.Equal(approval!.Id, call.ApprovalId);
        Assert.Equal("assistant", call.Agent);
        Assert.Equal("workspace_delete", call.ToolName);

        // Unattended approvals are never pending, so nobody is notified.
        var thread2 = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread2, "b", unattended: true);
        var s2 = new Setup(fx, agent, token, thread2, (await fx.ClaimAsync(token))!.Id);
        await CreateAsync(s2, Request(s2));
        Assert.Single(notifier.Calls);
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
        await TestHelpers.WaitForLongPollAsync<SharpClaw.Gateway.Services.ApprovalSignal>(s.Fx);
        var decided = await DecideAsync(s, approval.Id, decision);
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await poll;
        Assert.Equal(decision, result.Status);
        Assert.Equal(ApprovalDecidedBy.User, result.DecidedBy);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), "long poll should return promptly");
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
