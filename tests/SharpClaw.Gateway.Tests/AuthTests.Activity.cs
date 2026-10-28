using System.Net.Http.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>Who sent a message (3.3.1 spec section 3.2): the session user or <c>admin_key</c>.</summary>
public partial class AuthTests
{
    [Fact]
    public async Task A_message_records_its_sender_from_the_session_or_the_admin_key()
    {
        await using var fx = await StartAsync();
        await CreateUserAsync(fx);
        var (agent, _) = await fx.CreateAgentAsync("ade");
        var thread = await fx.CreateThreadAsync(agent.Id);

        using var browser = new Browser(fx);
        (await browser.LoginAsync()).EnsureSuccessStatusCode();
        (await browser.PostAsync($"/api/threads/{thread}/messages", new PostMessageRequest("from kevin"))).EnsureSuccessStatusCode();
        await fx.PostUserMessageAsync(thread, "from the cli");

        using var admin = fx.Admin();
        var page = (await admin.GetFromJsonAsync<MessageListResponse>($"/api/threads/{thread}/messages", SharpClawJson.Options))!;
        Assert.Equal(["kevin", "admin_key"], page.Messages.Select(m => m.SentByUser));

        var activity = (await admin.GetFromJsonAsync<ActivityResponse>("/api/activity", SharpClawJson.Options))!;
        Assert.Equal([("admin_key", "ade"), ("kevin", "ade")], activity.Items.Select(i => (i.From, i.To)));
    }
}
