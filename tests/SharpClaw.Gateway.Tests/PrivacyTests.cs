using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Cross-cutting (pre-3.3 spec section 12): the new code paths never log message content or tool
/// arguments/results, and durable events never carry tool arguments or results.
/// </summary>
public class PrivacyTests
{
    [Fact]
    public async Task Tool_history_context_events_and_approvals_keep_content_out_of_logs_and_events()
    {
        var logs = new ConcurrentQueue<string>();
        await using var fx = await GatewayFixture.CreateAsync(configure: b =>
            b.Logging.AddProvider(new TelemetryAndAbortTests.CapturingLoggerProvider(logs)));
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "SECRET_PROMPT_TEXT");
        await fx.ClaimAsync(token);
        using var client = fx.AgentClient(token);

        await client.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(corr, thread, "call_1", "workspace_write",
            "write notes/a.md", RiskLevels.Medium, "overwrites a file"), SharpClawJson.Options);
        await client.GetAsync($"/agent/threads/{thread}/context?reserveTokens=10");
        var exchange = new ToolExchangeDto("SECRET_ASSISTANT_TEXT",
            new[] { new ChatToolCall("call_1", "workspace_write", """{"path":"notes/a.md","content":"SECRET_TOOL_ARGUMENT"}""") },
            new[] { new ToolResultDto("call_1", """{"ok":true,"content":"SECRET_TOOL_RESULT"}""", true, true) });
        (await client.PostAsJsonAsync($"/agent/threads/{thread}/messages",
            new AgentReplyRequest("SECRET_REPLY_TEXT", corr, new[] { exchange }), SharpClawJson.Options)).EnsureSuccessStatusCode();
        using (var admin = fx.Admin())
            await admin.GetStringAsync("/api/approvals");

        var all = string.Join("\n", logs);
        foreach (var secret in new[] { "SECRET_PROMPT_TEXT", "SECRET_ASSISTANT_TEXT", "SECRET_TOOL_ARGUMENT", "SECRET_TOOL_RESULT", "SECRET_REPLY_TEXT" })
            Assert.DoesNotContain(secret, all);

        using var db = fx.NewDb();
        var events = string.Join("\n", await db.ThreadEvents.Select(e => e.Data).ToListAsync());
        Assert.DoesNotContain("SECRET_TOOL_ARGUMENT", events);
        Assert.DoesNotContain("SECRET_TOOL_RESULT", events);
        Assert.DoesNotContain("SECRET_ASSISTANT_TEXT", events);
    }
}
