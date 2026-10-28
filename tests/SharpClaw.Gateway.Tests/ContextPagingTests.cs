using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// The context endpoint reads only the newest turns it needs. Whatever it loads, the result must
/// equal building from the whole thread.
/// </summary>
public class ContextPagingTests
{
    private static readonly ContextOptions Options = new()
    {
        DefaultWindowTokens = 1000, ReplyReserveTokens = 100, CharsPerToken = 4, MessageOverheadTokens = 4,
    };

    [Theory]
    [InlineData(400)]        // a handful of turns fit: the first page is enough
    [InlineData(2_000)]      // about 70 turns fit: needs a second, larger page
    [InlineData(1_000_000)]  // everything fits: every page is read
    public async Task Paged_build_equals_building_from_the_whole_thread(int window)
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var threadId = await fx.CreateThreadAsync(agent.Id);
        const int turns = 130;
        using (var db = fx.NewDb())
        {
            var now = fx.Time.GetUtcNow().UtcDateTime;
            var seq = 0;
            for (var i = 0; i < turns; i++)
            {
                var user = new Message
                {
                    Id = Guid.CreateVersion7(), ThreadId = threadId, AgentId = agent.Id, Role = Roles.User,
                    Kind = MessageKinds.Message, Content = new string('u', 40 + i % 7), Status = MessageStatus.Done,
                    CreatedAt = now, Seq = ++seq,
                };
                db.Messages.Add(user);
                db.Messages.Add(new Message
                {
                    Id = Guid.CreateVersion7(), ThreadId = threadId, AgentId = agent.Id, Role = Roles.Assistant,
                    Kind = MessageKinds.Message, Content = "reply text", Status = MessageStatus.Done,
                    CorrelationId = user.Id, CreatedAt = now, Seq = ++seq,
                });
            }
            await db.SaveChangesAsync();
        }

        using var read = fx.NewDb();
        var all = await read.Messages.AsNoTracking().Where(m => m.ThreadId == threadId).OrderBy(m => m.Seq).ToListAsync();
        var current = all.Last(m => m.Role == Roles.User);
        var builder = new ContextBuilder(Options, new NoSummariser());

        var paged = await builder.BuildForThreadAsync(read, threadId, current, window, reserveTokens: 50, default);
        var whole = await builder.BuildAsync(threadId, all, current.Id, window, reserveTokens: 50, default);

        Assert.Equal(whole.DroppedTurns, paged.DroppedTurns);
        Assert.Equal(whole.EstimatedTokens, paged.EstimatedTokens);
        Assert.Equal(whole.TooLarge, paged.TooLarge);
        Assert.Equal(whole.Messages.Select(m => (m.Role, m.Content)), paged.Messages.Select(m => (m.Role, m.Content)));
        var included = turns - paged.DroppedTurns;
        switch (window)
        {
            case 1_000_000:
                Assert.Equal(turns, included);
                break;
            case 2_000:
                // More turns fit than the first page holds, so the second page was used.
                Assert.InRange(included, ContextBuilder.InitialTurnsToLoad + 1, turns - 1);
                break;
            default:
                Assert.InRange(included, 1, ContextBuilder.InitialTurnsToLoad - 1);
                break;
        }
    }
}
