using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>The agent side: long polls wake on decisions, and waiting keeps the message claimed.</summary>
public partial class ApprovalTests
{
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
