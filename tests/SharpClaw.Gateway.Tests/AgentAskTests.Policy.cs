using System.Net;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>ask_agent</c>: the gateway's policy and the chain checks (3.5 spec sections 5.1 and 5.3).</summary>
public partial class AgentAskTests
{
    [Fact]
    public async Task A_recipient_not_allowed_and_an_unknown_one_look_the_same()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);
        await fx.CreateAgentAsync("bea");

        var notAllowed = await AskAsync(p.Ade, "bea");
        var unknown = await AskAsync(p.Ade, "zed", toolCallId: "call_2");

        Assert.Equal(HttpStatusCode.NotFound, notAllowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await notAllowed.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        await AssertNothingQueuedAsync(fx);
    }

    [Fact]
    public async Task A_disabled_recipient_is_reported_to_the_model()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);
        await DisableAsync(fx, p.Cody.Id);

        var result = await ResultAsync(AskAsync(p.Ade, "cody"));

        Assert.Equal(AgentAsks.Refusals.AgentUnavailable, result.GetProperty("error").GetString());
        await AssertNothingQueuedAsync(fx);
    }

    [Fact]
    public async Task Without_ask_agent_enabled_for_the_sender_the_tool_does_not_exist()
    {
        var config = AskConfig(("ade", "cody"));
        config.Remove("ToolBroker:Agents:ade:Tools:0");
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var p = await PairAsync(fx);

        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(p.Ade, "cody")).StatusCode);
        await AssertNothingQueuedAsync(fx);
    }

    [Theory]
    [InlineData("", "hi", "agent is required")]
    [InlineData("cody", " ", "message is required")]
    public async Task Missing_arguments_are_refused(string agent, string message, string expected)
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var resp = await AskAsync(p.Ade, agent, message);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(expected, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_chain_longer_than_MaxHops_is_refused()
    {
        var config = AskConfig(("ade", "cody"), ("cody", "bea"));
        config["AgentMessaging:MaxHops"] = "1";
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var p = await PairAsync(fx);
        var (_, beaToken) = await fx.CreateAgentAsync("bea");

        var ask = AskAsync(p.Ade, "cody");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        var codyTurn = new Turn(fx, p.Cody, p.CodyToken, claimed.ThreadId, claimed.Id);

        var result = await ResultAsync(AskAsync(codyTurn, "bea"));
        Assert.Equal(AgentAsks.Refusals.HopLimit, result.GetProperty("error").GetString());
        Assert.Null(await fx.ClaimAsync(beaToken));

        await AnswerAsync(fx, p.CodyToken, claimed, "ok");
        await ResultAsync(ask);
    }

    [Fact]
    public async Task Asking_back_up_the_chain_is_refused()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody"), ("cody", "ade")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        var codyTurn = new Turn(fx, p.Cody, p.CodyToken, claimed.ThreadId, claimed.Id);

        var result = await ResultAsync(AskAsync(codyTurn, "ade"));
        Assert.Equal(AgentAsks.Refusals.AgentCycle, result.GetProperty("error").GetString());
        Assert.Null(await fx.ClaimAsync(p.Ade.Token));

        await AnswerAsync(fx, p.CodyToken, claimed, "ok");
        await ResultAsync(ask);
    }

    [Fact]
    public async Task Two_agents_asking_each_other_from_separate_chains_is_refused()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody"), ("cody", "ade")));
        var (cody, codyToken) = await fx.CreateAgentAsync("cody");
        var codyThread = await fx.CreateThreadAsync(cody.Id);
        await fx.PostUserMessageAsync(codyThread, "cody's own work");
        var codyTurn = new Turn(fx, cody, codyToken, codyThread, (await fx.ClaimAsync(codyToken))!.Id);
        var p = await PairWithExistingCodyAsync(fx, cody, codyToken);

        var adeAsk = AskAsync(p.Ade, "cody"); // queued for cody, who is busy with its own turn
        await TestHelpers.EventuallyAsync(() => Waiters(fx) > 0, because: "ade waiting on cody");

        var result = await ResultAsync(AskAsync(codyTurn, "ade"));
        Assert.Equal(AgentAsks.Refusals.AgentCycle, result.GetProperty("error").GetString());

        await fx.ReplyToClaimedAsync(codyTurn);
        await AnswerAsync(fx, codyToken, await ClaimAskAsync(fx, codyToken), "ok");
        await ResultAsync(adeAsk);
    }

    private static async Task<Pair> PairWithExistingCodyAsync(GatewayFixture fx, AgentDto cody, string codyToken)
    {
        var (ade, adeToken) = await fx.CreateAgentAsync("ade");
        var thread = await fx.CreateThreadAsync(ade.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(adeToken))!.Id;
        return new Pair(new Turn(fx, ade, adeToken, thread, corr), cody, codyToken);
    }

    private static async Task AssertNothingQueuedAsync(GatewayFixture fx)
    {
        await using var db = fx.NewDb();
        Assert.False(await db.Messages.AnyAsync(m => m.SenderAgentId != null));
        Assert.False(await db.Threads.AnyAsync(t => t.OriginThreadId != null));
    }
}
