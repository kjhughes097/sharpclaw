using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// The turn lifecycle service and its ownership guard, called directly (the HTTP behaviour is
/// covered by the inbox, messaging and approval tests).
/// </summary>
public class MessageTurnsTests
{
    private sealed record Setup(
        GatewayFixture Fx, AuthenticatedAgent Agent, AuthenticatedAgent Other, Guid ThreadId, Guid OtherThreadId, string Token);

    private static async Task<Setup> SetupAsync()
    {
        var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync("alpha");
        var (other, _) = await fx.CreateAgentAsync("beta");
        var thread = await fx.CreateThreadAsync(agent.Id);
        var otherThread = await fx.CreateThreadAsync(other.Id);
        return new Setup(fx,
            new AuthenticatedAgent(agent.Id, agent.Name, agent.Model, agent.DailyBudgetUsd),
            new AuthenticatedAgent(other.Id, other.Name, other.Model, other.DailyBudgetUsd),
            thread, otherThread, token);
    }


    [Fact]
    public async Task Guard_finds_only_the_agents_own_claimed_message_in_the_right_thread()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var msgId = await s.Fx.PostUserMessageAsync(s.ThreadId, "hi");
        // AsNoTracking reads, so one context can watch the state change.
        using var db = s.Fx.NewDb();
        // Pending: owned, but not claimed.
        Assert.NotNull(await TurnGuard.FindOwnedAsync(db, s.Agent.Id, msgId, s.ThreadId, default));
        Assert.Null(await TurnGuard.FindClaimedAsync(db, s.Agent.Id, msgId, s.ThreadId, default));

        await s.Fx.ClaimAsync(s.Token);
        Assert.NotNull(await TurnGuard.FindClaimedAsync(db, s.Agent.Id, msgId, s.ThreadId, default));
        Assert.NotNull(await TurnGuard.FindClaimedAsync(db, s.Agent.Id, msgId, threadId: null, default));

        // Another agent, or the wrong thread, sees nothing.
        Assert.Null(await TurnGuard.FindOwnedAsync(db, s.Other.Id, msgId, null, default));
        Assert.Null(await TurnGuard.FindClaimedAsync(db, s.Agent.Id, msgId, s.OtherThreadId, default));
        Assert.True(await TurnGuard.OwnsThreadAsync(db, s.Agent.Id, s.ThreadId, default));
        Assert.False(await TurnGuard.OwnsThreadAsync(db, s.Agent.Id, s.OtherThreadId, default));
    }

    /// <summary>One service call in its own scope, as each HTTP request would be.</summary>
    private static async Task<T> WithTurns<T>(GatewayFixture fx, Func<MessageTurns, Task<T>> call)
    {
        using var scope = fx.App.Services.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<MessageTurns>());
    }

    [Fact]
    public async Task Complete_reports_each_outcome()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var msgId = await s.Fx.PostUserMessageAsync(s.ThreadId, "hi");
        var none = Array.Empty<ToolExchangeDto>();

        Assert.Equal(TurnOutcome.NotClaimed,
            (await WithTurns(s.Fx, t => t.CompleteAsync(s.Agent, s.ThreadId, msgId, "r", none, default))).Outcome);
        await s.Fx.ClaimAsync(s.Token);
        Assert.Equal(TurnOutcome.NotFound,
            (await WithTurns(s.Fx, t => t.CompleteAsync(s.Other, s.ThreadId, msgId, "r", none, default))).Outcome);

        var (outcome, reply) = await WithTurns(s.Fx, t => t.CompleteAsync(s.Agent, s.ThreadId, msgId, "the reply", none, default));
        Assert.Equal(TurnOutcome.Ok, outcome);
        Assert.Equal("the reply", reply!.Content);
        Assert.Equal(TurnOutcome.AlreadyDone,
            (await WithTurns(s.Fx, t => t.CompleteAsync(s.Agent, s.ThreadId, msgId, "again", none, default))).Outcome);

        using var check = s.Fx.NewDb();
        var user = await check.Messages.SingleAsync(m => m.Id == msgId);
        Assert.Equal(MessageStatus.Done, user.Status);
        Assert.Equal("the reply", (await check.Threads.SingleAsync(t => t.Id == s.ThreadId)).LastPreview);
    }

    [Fact]
    public async Task Fail_only_applies_to_the_agents_claimed_message()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var msgId = await s.Fx.PostUserMessageAsync(s.ThreadId, "hi");

        Assert.False(await WithTurns(s.Fx, t => t.FailAsync(s.Agent, msgId, "agent_error", default)));
        await s.Fx.ClaimAsync(s.Token);
        Assert.False(await WithTurns(s.Fx, t => t.FailAsync(s.Other, msgId, "agent_error", default)));
        Assert.True(await WithTurns(s.Fx, t => t.FailAsync(s.Agent, msgId, "agent_error", default)));

        using var check = s.Fx.NewDb();
        var user = await check.Messages.SingleAsync(m => m.Id == msgId);
        Assert.Equal(MessageStatus.Failed, user.Status);
        Assert.Equal("agent_error", user.FailReason);
    }

    [Fact]
    public async Task Post_to_an_unknown_thread_returns_null()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        Assert.Null(await WithTurns(s.Fx, t => t.PostAsync(Guid.NewGuid(), "hi", unattended: false, default)));
    }
}
