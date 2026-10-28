using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>The per-thread trace (3.3 spec section 5.3).</summary>
public class TraceTests
{
    [Fact]
    public async Task Trace_joins_calls_tools_and_approvals_per_turn_without_content()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        const string secret = "SECRET-PROMPT-CONTENT";

        var corr = await fx.PostUserMessageAsync(thread, secret);
        await fx.ClaimAsync(token);
        await (await fx.ChatAsync(token, corr, thread, secret)).Content.ReadAsStringAsync();
        await fx.RequestApprovalAsync(token, thread, corr);
        var llmId = Guid.CreateVersion7();
        var now = fx.Time.GetUtcNow().UtcDateTime;
        using (var db = fx.NewDb())
        {
            db.ToolCalls.Add(new ToolCallRecord
            {
                Id = Guid.CreateVersion7(), AgentId = agent.Id, ThreadId = thread, CorrelationId = corr, LlmCallId = llmId,
                ToolCallId = "call_1", ToolName = "workspace_delete", Arguments = "{\"path\":\"SECRET-ARGUMENT\"}",
                Status = ToolCallStatuses.Succeeded, Decision = ToolDecisions.Approved, Risk = RiskLevels.High,
                DurationMs = 12, CreatedAt = now, ReportedAt = now,
            });
            await db.SaveChangesAsync();
        }
        using (var agentClient = fx.AgentClient(token))
            (await agentClient.PostAsJsonAsync($"/agent/threads/{thread}/messages",
                new AgentReplyRequest("SECRET-REPLY", corr), SharpClawJson.Options)).EnsureSuccessStatusCode();
        var second = await fx.PostUserMessageAsync(thread, "queued");

        using var admin = fx.Admin();
        var raw = await admin.GetStringAsync($"/api/threads/{thread}/trace");
        Assert.DoesNotContain("SECRET", raw);
        var trace = ClientApiHelpers.Read<TraceResponse>(raw);

        Assert.Equal(new[] { corr, second }, trace.Turns.Select(t => t.CorrelationId));
        var turn = trace.Turns[0];
        Assert.Equal(MessageStatus.Done, turn.Status);
        Assert.NotNull(turn.ClaimedAt);
        Assert.NotNull(turn.CompletedAt);
        var call = Assert.Single(turn.LlmCalls);
        Assert.Equal(LlmCallStatus.Ok, call.Status);
        var tool = Assert.Single(turn.ToolCalls);
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal(llmId, tool.LlmCallId);
        Assert.Equal(ToolCallStatuses.Succeeded, tool.Outcome);
        Assert.Equal(ToolDecisions.Approved, tool.Decision);
        var approval = Assert.Single(turn.Approvals);
        Assert.Equal(ApprovalStatuses.Expired, approval.Status); // the reply ended the turn
        Assert.Equal(new TraceTotalsDto(call.CostUsd, call.PromptTokens, call.CompletionTokens, 1, 1), turn.Totals);

        Assert.Equal(MessageStatus.Pending, trace.Turns[1].Status);
        Assert.Empty(trace.Turns[1].LlmCalls);
    }

    [Fact]
    public async Task Trace_is_capped_at_the_latest_100_turns()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var ids = new List<Guid>();
        for (var i = 0; i < 105; i++)
            ids.Add(await fx.PostUserMessageAsync(thread, $"m{i}"));

        using var admin = fx.Admin();
        var trace = (await admin.GetFromJsonAsync<TraceResponse>($"/api/threads/{thread}/trace", SharpClawJson.Options))!;
        Assert.Equal(ids.Skip(5), trace.Turns.Select(t => t.CorrelationId));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/threads/{Guid.NewGuid()}/trace")).StatusCode);
    }
}
