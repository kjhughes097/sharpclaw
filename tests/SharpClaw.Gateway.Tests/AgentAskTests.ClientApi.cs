using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>What the client API shows of an ask (3.5 spec section 8): sender, linked thread, activity, trace.</summary>
public partial class AgentAskTests
{
    [Fact]
    public async Task The_client_api_shows_the_sender_the_link_the_activity_and_the_trace()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody", "What is 2+2?");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        await AnswerAsync(fx, p.CodyToken, claimed, "4");
        await ResultAsync(ask);
        var linked = claimed.ThreadId;

        using var admin = fx.Admin();
        var messages = await admin.GetFromJsonAsync<MessageListResponse>($"/api/threads/{linked}/messages", SharpClawJson.Options);
        var question = messages!.Messages.Single(m => m.Role == Roles.User);
        Assert.Equal(("ade", null), (question.SentByAgent, question.SentByUser));
        Assert.Null(messages.Messages.Single(m => m.Role == Roles.Assistant).SentByAgent);

        var detail = await admin.GetFromJsonAsync<ThreadDetailResponse>($"/api/threads/{linked}", SharpClawJson.Options);
        Assert.Equal((p.Ade.Thread, "ade"), (detail!.Thread.OriginThreadId!.Value, detail.Thread.OriginAgentName));
        var own = await admin.GetFromJsonAsync<ThreadDetailResponse>($"/api/threads/{p.Ade.Thread}", SharpClawJson.Options);
        Assert.Null(own!.Thread.OriginThreadId);

        var list = await admin.GetFromJsonAsync<ThreadListResponse>($"/api/threads?agentId={p.Cody.Id}", SharpClawJson.Options);
        var row = Assert.Single(list!.Threads);
        Assert.Equal((linked, p.Ade.Thread, "ade"), (row.Id, row.OriginThreadId!.Value, row.OriginAgentName));

        var activity = await admin.GetFromJsonAsync<ActivityResponse>($"/api/activity?agent={p.Cody.Id}", SharpClawJson.Options);
        Assert.Contains(activity!.Items, i => i.Role == Roles.User && i.From == "ade" && i.To == "cody");
        Assert.Contains(activity.Items, i => i.Role == Roles.Assistant && i.From == "cody" && i.To == "ade");

        var trace = await admin.GetFromJsonAsync<TraceResponse>($"/api/threads/{p.Ade.Thread}/trace", SharpClawJson.Options);
        var traced = Assert.Single(Assert.Single(trace!.Turns).Asks!);
        Assert.Equal(new TraceAskDto("call_1", "cody", linked, claimed.Id, MessageStatus.Done, null), traced);

        await using var db = fx.NewDb();
        Assert.True(await db.ThreadEvents.AnyAsync(e => e.ThreadId == linked && e.Type == SseEventNames.ThreadCreated));
    }
}
