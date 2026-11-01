using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>ask_agent</c> never logs the question or the reply (3.5 spec section 2).</summary>
public partial class AgentAskTests
{
    [Fact]
    public async Task An_ask_keeps_the_question_and_reply_out_of_logs_and_the_question_out_of_events()
    {
        var logs = new ConcurrentQueue<string>();
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: AskConfig(("ade", "cody")), configure: b =>
            b.Logging.AddProvider(new TelemetryAndAbortTests.CapturingLoggerProvider(logs)));
        var p = await PairAsync(fx);

        var ask = AskAsync(p.Ade, "cody", "SECRET_ASK_QUESTION");
        var claimed = await ClaimAskAsync(fx, p.CodyToken);
        await AnswerAsync(fx, p.CodyToken, claimed, "SECRET_ASK_REPLY");
        await ResultAsync(ask);

        var all = string.Join("\n", logs);
        Assert.Contains("agent ask queued", all);
        Assert.Contains("agent ask finished", all);
        Assert.DoesNotContain("SECRET_ASK_QUESTION", all);
        Assert.DoesNotContain("SECRET_ASK_REPLY", all);

        await using var db = fx.NewDb();
        var events = string.Join("\n", await db.ThreadEvents.Select(e => e.Data).ToListAsync());
        Assert.DoesNotContain("SECRET_ASK_QUESTION", events);
    }
}
