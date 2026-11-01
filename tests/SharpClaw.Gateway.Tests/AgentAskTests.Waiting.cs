using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>ask_agent</c>: waiting, the asker's heartbeat, timeouts and cancellation (3.5 spec sections 5.4, 5.5).</summary>
public partial class AgentAskTests
{
    [Fact]
    public async Task A_timeout_before_the_recipient_picks_it_up_fails_the_ask()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "the ask waiting");
        fx.Time.Advance(TimeSpan.FromSeconds(601));

        var result = await ResultAsync(ask);
        Assert.Equal(FailReasons.AskTimeout, result.GetProperty("error").GetString());
        await using var db = fx.NewDb();
        var msg = await db.Messages.SingleAsync(m => m.SenderAgentId != null);
        Assert.Equal((MessageStatus.Failed, FailReasons.AskTimeout), (msg.Status, msg.FailReason));
        Assert.True(await db.ThreadEvents.AnyAsync(e => e.ThreadId == msg.ThreadId && e.Type == SseEventNames.Error));
        Assert.Null(await fx.ClaimAsync(p.CodyToken));
    }

    [Fact]
    public async Task A_timeout_after_the_recipient_claimed_it_lets_the_recipient_finish()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "the ask waiting");
        fx.Time.Advance(TimeSpan.FromSeconds(601));

        var result = await ResultAsync(ask);
        Assert.Equal(FailReasons.AskTimeout, result.GetProperty("error").GetString());
        await AnswerAsync(fx, p.CodyToken, claimed, "late but kept");
        await using var db = fx.NewDb();
        Assert.Equal(MessageStatus.Done, (await db.Messages.SingleAsync(m => m.Id == claimed.Id)).Status);
    }

    [Fact]
    public async Task Waiting_keeps_the_askers_claim_fresh()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "the ask waiting");
        fx.Time.Advance(TimeSpan.FromSeconds(400)); // past Inbox:ClaimTimeoutSeconds (300)
        var now = fx.Time.GetUtcNow().UtcDateTime;
        await TestHelpers.EventuallyAsync(async () =>
        {
            await using var db = fx.NewDb();
            return (await db.Messages.SingleAsync(m => m.Id == p.Ade.Corr)).ClaimedAt == now;
        }, because: "the heartbeat");

        Assert.Null(await fx.ClaimAsync(p.Ade.Token)); // not requeued as stale
        await AnswerAsync(fx, p.CodyToken, await ClaimAskAsync(fx, p.CodyToken), "4");
        Assert.Equal("4", (await ResultAsync(ask)).GetProperty("reply").GetString());
    }

    [Fact]
    public async Task A_dropped_request_cancels_an_ask_nobody_picked_up()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        using var cts = new CancellationTokenSource();
        var ask = AskAsync(p.Ade, "cody", ct: cts.Token);
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "the ask waiting");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);

        await TestHelpers.EventuallyAsync(async () =>
        {
            await using var db = fx.NewDb();
            return (await db.Messages.SingleAsync(m => m.SenderAgentId != null)).FailReason == FailReasons.AskCancelled;
        }, because: "the ask cancelled");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_asking_turn_ending_cancels_its_pending_asks(bool reply)
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "the ask waiting");
        using (var c = fx.AgentClient(p.Ade.Token))
        {
            var resp = reply
                ? await c.PostAsJsonAsync($"/agent/threads/{p.Ade.Thread}/messages",
                    new AgentReplyRequest("gave up", p.Ade.Corr), SharpClawJson.Options)
                : await c.PostAsJsonAsync($"/agent/messages/{p.Ade.Corr}/fail",
                    new FailMessageRequest(FailReasons.AgentError), SharpClawJson.Options);
            resp.EnsureSuccessStatusCode();
        }

        var result = await ResultAsync(ask);
        Assert.Equal(AgentAsks.Refusals.AgentFailed, result.GetProperty("error").GetString());
        Assert.Equal(FailReasons.AskCancelled, result.GetProperty("reason").GetString());
        Assert.Null(await fx.ClaimAsync(p.CodyToken));
    }

    [Fact]
    public async Task A_stale_asking_turn_that_is_requeued_cancels_its_pending_asks()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "the ask waiting");
        // What the inbox does when it requeues a stale claim.
        await using (var scope = fx.App.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<MessageTurns>().ReleaseAsync(p.Ade.Corr, CancellationToken.None);

        var result = await ResultAsync(ask);
        Assert.Equal(FailReasons.AskCancelled, result.GetProperty("reason").GetString());
    }
}
