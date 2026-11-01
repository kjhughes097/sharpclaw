using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// With every agent free to ask every other (the default), what stops asks going on forever
/// (3.5 spec section 5.3): the chain budget and the deadlock check across separate chains.
/// </summary>
public partial class AgentAskTests
{
    /// <summary><c>ask_agent</c> enabled for each agent, and no pair lists: anyone may ask anyone.</summary>
    private static Dictionary<string, string?> OpenConfig(params string[] agents)
    {
        var config = new Dictionary<string, string?>();
        foreach (var name in agents)
            config[$"ToolBroker:Agents:{name}:Tools:0"] = BrokerTools.AskAgent;
        return config;
    }

    [Fact]
    public async Task By_default_any_agent_may_ask_any_other()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: OpenConfig("ade", "cody"));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        await AnswerAsync(fx, p.CodyToken, await ClaimAskAsync(fx, p.CodyToken), "4");

        Assert.Equal("4", (await ResultAsync(ask)).GetProperty("reply").GetString());
    }

    [Fact]
    public async Task Asks_from_one_human_message_stop_at_the_chain_budget_at_every_depth()
    {
        var config = OpenConfig("ade", "cody", "bea");
        config["AgentMessaging:MaxAsksPerChain"] = "2";
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var p = await PairAsync(fx);
        var (_, beaToken) = await fx.CreateAgentAsync("bea");

        // Ask 1: ade → cody; while answering, cody asks bea (ask 2, one level down, same chain).
        var first = AskAsync(p.Ade, "cody", toolCallId: "call_1");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        var codyTurn = new Turn(fx, p.Cody, p.CodyToken, claimed.ThreadId, claimed.Id);
        var nested = AskAsync(codyTurn, "bea");
        await AnswerAsync(fx, beaToken, await ClaimAskAsync(fx, beaToken), "from bea");
        await ResultAsync(nested);

        // Ask 3, from either level, is over the budget.
        var deep = await ResultAsync(AskAsync(codyTurn, "bea", toolCallId: "call_2"));
        Assert.Equal(AgentAsks.Refusals.ChainLimit, deep.GetProperty("error").GetString());
        await AnswerAsync(fx, p.CodyToken, claimed, "ok");
        await ResultAsync(first);
        var again = await ResultAsync(AskAsync(p.Ade, "cody", toolCallId: "call_3"));
        Assert.Equal(AgentAsks.Refusals.ChainLimit, again.GetProperty("error").GetString());

        // A new human message is a new chain with its own budget.
        await fx.ReplyToClaimedAsync(p.Ade);
        await fx.PostUserMessageAsync(p.Ade.Thread, "next");
        var next = p.Ade with { Corr = (await fx.ClaimAsync(p.Ade.Token))!.Id };
        var fresh = AskAsync(next, "cody");
        await AnswerAsync(fx, p.CodyToken, await ClaimAskAsync(fx, p.CodyToken), "fine");
        Assert.True((await ResultAsync(fresh)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_deadlock_through_three_separate_chains_is_refused()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: OpenConfig("ade", "bea", "cody"));
        // bea and cody are each busy with a turn of their own (separate human messages).
        var bea = await OwnTurnAsync(fx, "bea");
        var cody = await OwnTurnAsync(fx, "cody");
        var ade = await OwnTurnAsync(fx, "ade");

        // ade waits on bea, and bea waits on cody (each queued behind the recipient's own turn).
        var adeAsk = AskAsync(ade, "bea");
        await TestHelpers.EventuallyAsync(() => Waiters(fx) >= 1, because: "ade waiting on bea");
        var beaAsk = AskAsync(bea, "cody");
        await TestHelpers.EventuallyAsync(() => Waiters(fx) >= 2, because: "bea waiting on cody");

        // cody asking ade would close the circle: nobody could ever answer.
        var refused = await ResultAsync(AskAsync(cody, "ade"));
        Assert.Equal(AgentAsks.Refusals.AgentCycle, refused.GetProperty("error").GetString());

        // Unwind: cody finishes its own turn and answers bea; bea finishes and answers ade.
        await fx.ReplyToClaimedAsync(cody);
        await AnswerAsync(fx, cody.Token, await ClaimAskAsync(fx, cody.Token), "from cody");
        await ResultAsync(beaAsk);
        await fx.ReplyToClaimedAsync(bea);
        await AnswerAsync(fx, bea.Token, await ClaimAskAsync(fx, bea.Token), "from bea");
        Assert.Equal("from bea", (await ResultAsync(adeAsk)).GetProperty("reply").GetString());
    }

    /// <summary>An agent holding a claimed turn of its own, started by a human message.</summary>
    private static async Task<Turn> OwnTurnAsync(GatewayFixture fx, string name)
    {
        var (agent, token) = await fx.CreateAgentAsync(name);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, $"{name}'s own work");
        return new Turn(fx, agent, token, thread, (await fx.ClaimAsync(token))!.Id);
    }
}
