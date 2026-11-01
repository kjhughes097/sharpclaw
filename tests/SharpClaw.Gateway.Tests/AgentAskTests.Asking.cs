using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Services.Broker;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>ask_agent</c> (3.5 spec section 5): queueing, the linked thread and the answer.</summary>
public partial class AgentAskTests
{
    [Fact]
    public async Task The_recipients_reply_is_returned_and_both_sides_are_recorded()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody", "What is 2+2?");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        Assert.Equal(("ade", false, "What is 2+2?"), (claimed.FromAgent, claimed.Tainted, claimed.Content));
        await AnswerAsync(fx, p.CodyToken, claimed, "4");

        var result = await ResultAsync(ask);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("cody", result.GetProperty("agent").GetString());
        Assert.Equal("4", result.GetProperty("reply").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(claimed.ThreadId, result.GetProperty("threadId").GetGuid());

        await using var db = fx.NewDb();
        var thread = await db.Threads.SingleAsync(t => t.Id == claimed.ThreadId);
        Assert.Equal((p.Cody.Id, p.Ade.Thread, p.Ade.Agent.Id), (thread.AgentId, thread.OriginThreadId!.Value, thread.OriginAgentId!.Value));
        Assert.Equal("From ade: What is 2+2?", thread.Title);
        var msg = await db.Messages.SingleAsync(m => m.Id == claimed.Id);
        Assert.Equal((p.Ade.Agent.Id, p.Ade.Corr, "call_1", 1, MessageStatus.Done),
            (msg.SenderAgentId!.Value, msg.OriginMessageId!.Value, msg.OriginToolCallId, msg.Hop, msg.Status));
        Assert.Null(msg.SentByUser);
        var audit = await db.BrokerCalls.SingleAsync();
        Assert.Equal((BrokerTools.AskAgent, ToolBroker.Statuses.Succeeded, "cody"), (audit.Tool, audit.Status, audit.Target));
    }

    [Fact]
    public async Task Asks_from_one_thread_share_a_linked_thread_even_when_archived_and_another_thread_gets_its_own()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var first = await AskAndAnswerAsync(p, "one", "call_1");
        using (var admin = fx.Admin())
            (await admin.PatchAsJsonAsync($"/api/threads/{first}", new UpdateThreadRequest(null, true), SharpClawJson.Options))
                .EnsureSuccessStatusCode();
        var second = await AskAndAnswerAsync(p, "two", "call_2");
        Assert.Equal(first, second);

        await fx.ReplyToClaimedAsync(p.Ade);
        var otherThread = await fx.CreateThreadAsync(p.Ade.Agent.Id);
        await fx.PostUserMessageAsync(otherThread, "again");
        var other = p.Ade with { Thread = otherThread, Corr = (await fx.ClaimAsync(p.Ade.Token))!.Id };
        var third = await AskAndAnswerAsync(p with { Ade = other }, "three", "call_1");
        Assert.NotEqual(first, third);
    }

    [Fact]
    public async Task The_same_tool_call_twice_queues_one_message_and_both_get_the_answer()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask1 = AskAsync(p.Ade, "cody");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        var ask2 = AskAsync(p.Ade, "cody");
        await TestHelpers.EventuallyAsync(async () =>
        {
            await using var db = fx.NewDb();
            return await db.BrokerCalls.CountAsync() == 0 && Waiters(fx) >= 2;
        }, because: "both requests waiting on the one ask");
        await AnswerAsync(fx, p.CodyToken, claimed, "4");

        Assert.Equal("4", (await ResultAsync(ask1)).GetProperty("reply").GetString());
        Assert.Equal("4", (await ResultAsync(ask2)).GetProperty("reply").GetString());
        await using var check = fx.NewDb();
        Assert.Equal(1, await check.Messages.CountAsync(m => m.SenderAgentId != null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attendance_and_taint_carry_over_to_the_recipient(bool unattended)
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx, unattended);

        var ask = AskAsync(p.Ade, "cody", tainted: true);
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        Assert.True(claimed.Tainted);

        // An approval in the linked thread: asked of a human when attended, denied at once otherwise.
        var approvalId = await fx.RequestApprovalAsync(p.CodyToken, claimed.ThreadId, claimed.Id);
        await using (var db = fx.NewDb())
        {
            var approval = await db.Approvals.SingleAsync(a => a.Id == approvalId);
            Assert.Equal(unattended ? ApprovalStatuses.Denied : ApprovalStatuses.Pending, approval.Status);
            Assert.Equal(unattended, (await db.Messages.SingleAsync(m => m.Id == claimed.Id)).Unattended);
            Assert.Equal(!unattended, await db.ThreadEvents.AnyAsync(e =>
                e.ThreadId == claimed.ThreadId && e.Type == SseEventNames.ApprovalRequested));
        }
        await AnswerAsync(fx, p.CodyToken, claimed, "ok");
        await ResultAsync(ask);
    }

    [Fact]
    public async Task A_long_reply_is_cut_and_marked_truncated()
    {
        var config = AskConfig(("ade", "cody"));
        config["AgentMessaging:MaxReplyChars"] = "1000";
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        await AnswerAsync(fx, p.CodyToken, await ClaimAskAsync(fx, p.CodyToken), new string('x', 1500));

        var result = await ResultAsync(ask);
        Assert.Equal(1000, result.GetProperty("reply").GetString()!.Length);
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_recipient_failure_is_returned_with_its_reason()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        using (var c = fx.AgentClient(p.CodyToken))
            (await c.PostAsJsonAsync($"/agent/messages/{claimed.Id}/fail",
                new FailMessageRequest(FailReasons.ToolLoopLimit), SharpClawJson.Options)).EnsureSuccessStatusCode();

        var result = await ResultAsync(ask);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(AgentAsks.Refusals.AgentFailed, result.GetProperty("error").GetString());
        Assert.Equal(FailReasons.ToolLoopLimit, result.GetProperty("reason").GetString());
        await using var db = fx.NewDb();
        Assert.Equal(ToolBroker.Statuses.Failed, (await db.BrokerCalls.SingleAsync()).Status);
    }

    [Fact]
    public async Task The_recipient_sees_who_asked_in_its_context()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody", "What is 2+2?");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        using (var c = fx.AgentClient(p.CodyToken))
        {
            var context = await c.GetFromJsonAsync<ContextResponse>(
                $"/agent/threads/{claimed.ThreadId}/context", SharpClawJson.Options);
            var user = Assert.Single(context!.Messages);
            Assert.Equal("[Message from agent ade]\n\nWhat is 2+2?", user.Content);
            Assert.True(context.EstimatedTokens >= user.Content!.Length / 4, "the prefix counts in the estimate");
        }
        await AnswerAsync(fx, p.CodyToken, claimed, "4");
        await ResultAsync(ask);
    }

    // ----- helpers -----

    private sealed record Pair(Turn Ade, AgentDto Cody, string CodyToken);

    /// <summary>Agents <c>cody</c> and <c>ade</c>, with ade holding a claimed turn (its own thread).</summary>
    private static async Task<Pair> PairAsync(GatewayFixture fx, bool unattended = false)
    {
        var (cody, codyToken) = await fx.CreateAgentAsync("cody");
        var (ade, adeToken) = await fx.CreateAgentAsync("ade");
        var thread = await fx.CreateThreadAsync(ade.Id);
        await fx.PostUserMessageAsync(thread, "hello", unattended);
        var corr = (await fx.ClaimAsync(adeToken))!.Id;
        return new Pair(new Turn(fx, ade, adeToken, thread, corr), cody, codyToken);
    }

    private static async Task<HttpResponseMessage> AskAsync(
        Turn t, string agent, string message = "What is 2+2?", string toolCallId = "call_1", bool tainted = false,
        CancellationToken ct = default)
    {
        using var c = t.Fx.AgentClient(t.Token);
        return await c.PostAsJsonAsync($"/agent/tools/{BrokerTools.AskAgent}",
            new InvokeToolRequest(t.Corr, t.Thread, toolCallId, Args(new { agent, message }), Tainted: tainted),
            SharpClawJson.Options, ct);
    }

    private static async Task<JsonElement> ResultAsync(Task<HttpResponseMessage> ask)
    {
        var resp = await ask;
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Claims the ask once it has been queued.</summary>
    private static async Task<InboxMessageDto> ClaimAskAsync(GatewayFixture fx, string token)
    {
        InboxMessageDto? claimed = null;
        await TestHelpers.EventuallyAsync(async () => (claimed = await fx.ClaimAsync(token)) is not null,
            because: "the ask in the recipient's inbox");
        return claimed!;
    }

    private static async Task AnswerAsync(GatewayFixture fx, string token, InboxMessageDto claimed, string reply)
    {
        using var c = fx.AgentClient(token);
        (await c.PostAsJsonAsync($"/agent/threads/{claimed.ThreadId}/messages",
            new AgentReplyRequest(reply, claimed.Id), SharpClawJson.Options)).EnsureSuccessStatusCode();
    }

    /// <summary>Asks, answers, and returns the linked thread.</summary>
    private static async Task<Guid> AskAndAnswerAsync(Pair p, string message, string toolCallId)
    {
        var ask = AskAsync(p.Ade, "cody", message, toolCallId);
        var claimed = await ClaimAskAsync(p.Ade.Fx, p.CodyToken);
        await AnswerAsync(p.Ade.Fx, p.CodyToken, claimed, "ok");
        return (await ResultAsync(ask)).GetProperty("threadId").GetGuid();
    }

    /// <summary>Requests currently waiting on an ask.</summary>
    private static int Waiters(GatewayFixture fx) =>
        ((KeyedSignal)fx.App.Services.GetService(typeof(AskFinishedSignal))!).Waiters;
}
