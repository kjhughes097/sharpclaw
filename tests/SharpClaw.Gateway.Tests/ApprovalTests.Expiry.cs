using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Approvals expire: on schedule, on read, and when their turn ends.</summary>
public partial class ApprovalTests
{
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
}
