using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Tests;

/// <summary>Tool-call audit: rows from the LLM stream, agent outcome reports, audit API (3.2 spec 8.5, 8.6, 9).</summary>
public class ToolCallAuditTests
{
    private static readonly string LongArg = new('x', 3000);

    private static string Script => """
    { "rules": [
      { "match": "^two tools$", "role": "user",
        "toolCalls": [ { "name": "workspace_list", "arguments": {} },
                       { "name": "workspace_write", "arguments": { "path": "a.md", "content": "LONG" } } ] }
    ] }
    """.Replace("LONG", LongArg);

    private static async Task<(GatewayFixture fx, AgentDto agent, string token, Guid thread, Guid corr)> SetupAsync()
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?>
        {
            ["Fake:ScriptPath"] = ChatTestHelpers.WriteScript(Script),
        });
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "two tools");
        var corr = (await fx.ClaimAsync(token))!.Id;
        return (fx, agent, token, thread, corr);
    }

    /// <summary>Runs one chat round that returns two tool calls; returns their ids in order.</summary>
    private static async Task<List<string>> RequestToolsAsync(GatewayFixture fx, string token, Guid thread, Guid corr)
    {
        var resp = await fx.ChatRawAsync(token, corr, thread, new
        {
            stream = true,
            messages = new[] { new { role = "user", content = "two tools" } },
        });
        var (calls, _) = ChatTestHelpers.ParseToolCalls(await resp.Content.ReadAsStringAsync());
        return calls.Select(c => c.Id).ToList();
    }

    private static Task<HttpResponseMessage> ReportAsync(GatewayFixture fx, string token, ToolCallReportRequest report)
    {
        var c = fx.AgentClient(token);
        return c.PostAsJsonAsync("/agent/tool-calls", report, SharpClawJson.Options);
    }

    private static ToolCallReportRequest Report(Guid corr, string id, string status = ToolCallStatuses.Succeeded) =>
        new(corr, id, status, ToolDecisions.Allowed, RiskLevels.Low, Tainted: false, ApprovalId: null, ErrorCode: null, DurationMs: 12);

    [Fact]
    public async Task Gateway_records_requested_tool_calls_from_the_stream()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;

        var ids = await RequestToolsAsync(fx, token, thread, corr);

        using var db = fx.NewDb();
        var llmCall = await db.LlmCalls.AsNoTracking().SingleAsync();
        var rows = await db.ToolCalls.AsNoTracking().OrderBy(t => t.ToolName).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(ToolCallStatuses.Requested, r.Status);
            Assert.Equal(llmCall.Id, r.LlmCallId);
            Assert.Equal(corr, r.CorrelationId);
            Assert.Equal(thread, r.ThreadId);
            Assert.Equal(agent.Id, r.AgentId);
            Assert.Null(r.ReportedAt);
        });
        Assert.Equal(ids.OrderBy(i => i), rows.Select(r => r.ToolCallId).OrderBy(i => i));
        var write = rows.Single(r => r.ToolName == "workspace_write");
        Assert.Equal(1000, write.Arguments.Length);
    }

    [Fact]
    public async Task Agent_reports_outcome_once()
    {
        var (fx, _, token, thread, corr) = await SetupAsync();
        await using var __ = fx;
        var ids = await RequestToolsAsync(fx, token, thread, corr);

        var first = await ReportAsync(fx, token, Report(corr, ids[0]) with { Tainted = true, Risk = RiskLevels.Medium });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var second = await ReportAsync(fx, token, Report(corr, ids[0]));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        using var db = fx.NewDb();
        var row = await db.ToolCalls.AsNoTracking().SingleAsync(t => t.ToolCallId == ids[0]);
        Assert.Equal(ToolCallStatuses.Succeeded, row.Status);
        Assert.Equal(ToolDecisions.Allowed, row.Decision);
        Assert.Equal(RiskLevels.Medium, row.Risk);
        Assert.True(row.Tainted);
        Assert.Equal(12, row.DurationMs);
        Assert.NotNull(row.ReportedAt);
    }

    [Fact]
    public async Task Reports_for_unknown_or_foreign_calls_return_404()
    {
        var (fx, _, token, thread, corr) = await SetupAsync();
        await using var __ = fx;
        var ids = await RequestToolsAsync(fx, token, thread, corr);
        var (_, otherToken) = await fx.CreateAgentAsync("other");

        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(fx, token, Report(corr, "call_never_requested"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ReportAsync(fx, otherToken, Report(corr, ids[0]))).StatusCode);
    }

    [Theory]
    [InlineData("requested", null, null)]
    [InlineData("succeeded", "maybe", null)]
    [InlineData("succeeded", "allowed", "extreme")]
    public async Task Invalid_reports_return_400(string status, string? decision, string? risk)
    {
        var (fx, _, token, thread, corr) = await SetupAsync();
        await using var __ = fx;
        var ids = await RequestToolsAsync(fx, token, thread, corr);

        var resp = await ReportAsync(fx, token, Report(corr, ids[0], status) with { Decision = decision, Risk = risk });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Audit_endpoint_lists_tool_calls_with_filters()
    {
        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;
        var ids = await RequestToolsAsync(fx, token, thread, corr);
        await ReportAsync(fx, token, Report(corr, ids[0]));

        using var admin = fx.Admin();
        var all = await admin.GetFromJsonAsync<ToolCallAuditResponse>(
            $"/api/audit/tool-calls?agent={agent.Name}", SharpClawJson.Options);
        Assert.Equal(2, all!.Calls.Count);
        Assert.Contains(all.Calls, c => c.Status == ToolCallStatuses.Succeeded && c.ReportedAt is not null);
        Assert.Contains(all.Calls, c => c.Status == ToolCallStatuses.Requested && c.ReportedAt is null);

        var limited = await admin.GetFromJsonAsync<ToolCallAuditResponse>(
            $"/api/audit/tool-calls?threadId={thread}&limit=1", SharpClawJson.Options);
        Assert.Single(limited!.Calls);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/audit/tool-calls?agent=nobody")).StatusCode);
    }

    [Fact]
    public async Task Report_emits_tool_call_metric_with_tool_and_status()
    {
        var measured = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == GatewayMetrics.MeterName && inst.Name == "sharpclaw.tool.calls")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, v, tags, _) =>
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            lock (measured) measured.Add((v, d));
        });
        listener.Start();

        var (fx, agent, token, thread, corr) = await SetupAsync();
        await using var _ = fx;
        var ids = await RequestToolsAsync(fx, token, thread, corr);
        await ReportAsync(fx, token, Report(corr, ids[0], ToolCallStatuses.Denied) with { Decision = ToolDecisions.Denied });

        lock (measured)
            Assert.Contains(measured, m =>
                m.Value == 1 && (string?)m.Tags["agent"] == agent.Name &&
                (string?)m.Tags["status"] == ToolCallStatuses.Denied && m.Tags.ContainsKey("tool"));
    }
}
