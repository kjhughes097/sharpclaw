using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>GET /api/activity</c> (3.3.1 spec section 3.2).</summary>
public class ActivityApiTests
{
    private static async Task<ActivityResponse> GetAsync(GatewayFixture fx, string query = "")
    {
        using var admin = fx.Admin();
        return (await admin.GetFromJsonAsync<ActivityResponse>($"/api/activity{query}", SharpClawJson.Options))!;
    }

    [Fact]
    public async Task Prompts_and_replies_are_listed_newest_first_with_sender_and_recipient()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync("ade");
        var thread = await fx.CreateThreadAsync(agent.Id);
        var prompt = await fx.PostUserMessageAsync(thread, "  hello\n\n   there  ");
        fx.Time.Advance(TimeSpan.FromSeconds(1));
        await fx.ReplyAsync(token, thread, prompt, "hi back");

        var items = (await GetAsync(fx)).Items;
        Assert.Equal(2, items.Count);
        var (reply, user) = (items[0], items[1]);

        Assert.Equal((Roles.Assistant, "ade", "admin_key", "hi back"), (reply.Role, reply.From, reply.To, reply.Preview));
        Assert.Equal((Roles.User, "admin_key", "ade", "hello there"), (user.Role, user.From, user.To, user.Preview));
        Assert.Equal(MessageStatus.Done, user.Status);
        Assert.Equal((thread, "t", "ade"), (user.ThreadId, user.ThreadTitle, user.AgentName));
    }

    [Fact]
    public async Task Tool_rows_are_never_included()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync("ade");
        var thread = await fx.CreateThreadAsync(agent.Id);
        var prompt = await fx.PostUserMessageAsync(thread, "go");
        Assert.NotNull(await fx.ClaimAsync(token));
        using var agentClient = fx.AgentClient(token);
        var exchange = new ToolExchangeDto(null,
            [new ChatToolCall("call_1", "workspace_list", "{}")],
            [new ToolResultDto("call_1", "secret-tool-output", true, false)]);
        (await agentClient.PostAsJsonAsync($"/agent/threads/{thread}/messages",
            new AgentReplyRequest("done", prompt, [exchange]), SharpClawJson.Options)).EnsureSuccessStatusCode();

        var items = (await GetAsync(fx)).Items;
        Assert.Equal([Roles.Assistant, Roles.User], items.Select(i => i.Role));
        Assert.DoesNotContain(items, i => i.Preview.Contains("secret-tool-output"));
    }

    [Fact]
    public async Task Preview_is_cut_to_120_characters_and_a_missing_sender_falls_back_to_user()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync("ade");
        var thread = await fx.CreateThreadAsync(agent.Id);
        var prompt = await fx.PostUserMessageAsync(thread, new string('x', 500));
        await fx.ReplyAsync(token, thread, prompt, "ok");
        // Rows from before 3.3.1 have no sender.
        await using (var db = fx.NewDb())
            await db.Messages.Where(m => m.Id == prompt).ExecuteUpdateAsync(s => s.SetProperty(m => m.SentByUser, (string?)null));

        var items = (await GetAsync(fx)).Items;
        var user = Assert.Single(items, i => i.Role == Roles.User);
        Assert.Equal(120, user.Preview.Length);
        Assert.Equal("user", user.From);
        Assert.Equal("user", Assert.Single(items, i => i.Role == Roles.Assistant).To);
    }

    [Fact]
    public async Task Pages_with_a_cursor_and_filters_by_agent()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (ade, _) = await fx.CreateAgentAsync("ade");
        var (cody, _) = await fx.CreateAgentAsync("cody");
        var adeThread = await fx.CreateThreadAsync(ade.Id);
        var codyThread = await fx.CreateThreadAsync(cody.Id);
        for (var i = 0; i < 3; i++)
        {
            await fx.PostUserMessageAsync(adeThread, $"ade {i}");
            await fx.PostUserMessageAsync(codyThread, $"cody {i}");
            fx.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await GetAsync(fx, "?agent=ade&limit=2");
        Assert.Equal(["ade 2", "ade 1"], first.Items.Select(i => i.Preview));
        Assert.NotNull(first.NextCursor);
        var second = await GetAsync(fx, $"?agent=ade&limit=2&before={first.NextCursor}");
        Assert.Equal(["ade 0"], second.Items.Select(i => i.Preview));
        Assert.Null(second.NextCursor);

        Assert.Equal(6, (await GetAsync(fx)).Items.Count);
    }

    [Fact]
    public async Task Bad_cursor_is_400_unknown_agent_is_404_and_agent_listener_is_404()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/activity?before=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/activity?agent=nobody")).StatusCode);
        using var agent = fx.AgentClient(token);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/activity")).StatusCode);
    }
}
