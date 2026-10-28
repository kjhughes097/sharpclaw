using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Message paging (3.3 spec section 5.2).</summary>
public class MessagePaginationTests
{
    private static async Task<(GatewayFixture Fx, Guid Thread, string Token)> SetupAsync(int turns)
    {
        var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        for (var i = 0; i < turns; i++)
        {
            var corr = await fx.PostUserMessageAsync(thread, $"q{i}");
            await fx.ReplyAsync(token, thread, corr, $"a{i}");
        }
        return (fx, thread, token);
    }

    private static async Task<MessageListResponse> GetAsync(GatewayFixture fx, Guid thread, string query)
    {
        using var admin = fx.Admin();
        return (await admin.GetFromJsonAsync<MessageListResponse>($"/api/threads/{thread}/messages?{query}", SharpClawJson.Options))!;
    }

    [Fact]
    public async Task Latest_page_then_before_and_after_seq()
    {
        var (fx, thread, _) = await SetupAsync(5); // seq 1..10
        await using var _ = fx;

        var latest = await GetAsync(fx, thread, "limit=4");
        Assert.Equal(new[] { 7, 8, 9, 10 }, latest.Messages.Select(m => m.Seq));
        Assert.True(latest.HasMoreBefore);

        var earlier = await GetAsync(fx, thread, "limit=4&beforeSeq=7");
        Assert.Equal(new[] { 3, 4, 5, 6 }, earlier.Messages.Select(m => m.Seq));
        Assert.True(earlier.HasMoreBefore);

        var first = await GetAsync(fx, thread, "limit=4&beforeSeq=3");
        Assert.Equal(new[] { 1, 2 }, first.Messages.Select(m => m.Seq));
        Assert.False(first.HasMoreBefore);

        var after = await GetAsync(fx, thread, "limit=3&afterSeq=4");
        Assert.Equal(new[] { 5, 6, 7 }, after.Messages.Select(m => m.Seq));
        Assert.True(after.HasMoreBefore);

        var all = await GetAsync(fx, thread, "");
        Assert.Equal(10, all.Messages.Count);
        Assert.False(all.HasMoreBefore);

        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/threads/{thread}/messages?beforeSeq=3&afterSeq=1")).StatusCode);
    }

    [Fact]
    public async Task Existing_after_id_still_works_and_limit_is_capped()
    {
        var (fx, thread, _) = await SetupAsync(3);
        await using var _ = fx;
        var all = await GetAsync(fx, thread, "");
        var anchor = all.Messages[1];

        var after = await GetAsync(fx, thread, $"after={anchor.Id}");
        Assert.Equal(all.Messages.Skip(2).Select(m => m.Id), after.Messages.Select(m => m.Id));

        // limit above the maximum is clamped rather than rejected.
        var capped = await GetAsync(fx, thread, "limit=100000");
        Assert.Equal(6, capped.Messages.Count);
    }

    [Fact]
    public async Task Include_tools_counts_every_kind_in_the_limit()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "read it");
        await fx.ClaimAsync(token);
        using (var agentClient = fx.AgentClient(token))
        {
            var exchange = new ToolExchangeDto(null,
                new[] { new ChatToolCall("call_1", "workspace_read", "{\"path\":\"a.md\"}") },
                new[] { new ToolResultDto("call_1", "{\"ok\":true}", true, false) });
            (await agentClient.PostAsJsonAsync($"/agent/threads/{thread}/messages",
                new AgentReplyRequest("done", corr, new[] { exchange }), SharpClawJson.Options)).EnsureSuccessStatusCode();
        }

        var withTools = await GetAsync(fx, thread, "includeTools=true&limit=3");
        Assert.Equal(new[] { MessageKinds.ToolRequest, MessageKinds.ToolResult, MessageKinds.Message },
            withTools.Messages.Select(m => m.Kind));
        Assert.True(withTools.HasMoreBefore);

        var plain = await GetAsync(fx, thread, "limit=3");
        Assert.Equal(2, plain.Messages.Count);
        Assert.All(plain.Messages, m => Assert.Equal(MessageKinds.Message, m.Kind));
        Assert.False(plain.HasMoreBefore);
    }
}
