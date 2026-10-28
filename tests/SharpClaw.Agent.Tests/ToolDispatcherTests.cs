using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

public class ToolDispatcherTests
{

    private static (ToolDispatcher Dispatcher, RecordingGateway Gateway, ListLogger<ToolDispatcher> Logger) Create(
        IAgentTool[] tools, RiskLevel allowed = RiskLevel.Low, bool smartMode = false)
    {
        var gateway = new RecordingGateway();
        var logger = new ListLogger<ToolDispatcher>();
        var config = new AgentToolConfig(allowed, smartMode, 10, tools.Select(t => t.Name).ToList());
        var dispatcher = new ToolDispatcher(new ToolRegistry(tools, config.Tools), config, gateway, logger);
        return (dispatcher, gateway, logger);
    }

    private static ToolCallRequest Call(string name, string args = "{}") => new("call_1", name, args);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
    private static JsonElement Parse(ToolDispatchResult result) => Parse(result.Content);

    [Fact]
    public async Task Risk_at_or_below_allowed_level_runs_without_approval()
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.Low, "reads"));
        var (d, gw, _) = Create(new IAgentTool[] { tool });

        var result = Parse(await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, tool.Executions);
        Assert.Empty(gw.ApprovalRequests);
        var report = Assert.Single(gw.Reports);
        Assert.Equal(ToolCallStatuses.Succeeded, report.Status);
        Assert.Equal(ToolDecisions.Allowed, report.Decision);
        Assert.Equal(RiskLevels.Low, report.Risk);
        Assert.Equal("call_1", report.ToolCallId);
    }

    [Fact]
    public async Task Above_allowed_level_runs_only_after_approval()
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.High, "deletes"));
        var (d, gw, _) = Create(new IAgentTool[] { tool });
        var pending = RecordingGateway.Approval(ApprovalStatuses.Pending);
        gw.ApprovalReplies.Enqueue(pending);
        gw.ApprovalReplies.Enqueue(pending);
        gw.ApprovalReplies.Enqueue(RecordingGateway.Approval(ApprovalStatuses.Approved, ApprovalDecidedBy.User) with { Id = pending.Id });

        var result = Parse(await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, tool.Executions);
        var request = Assert.Single(gw.ApprovalRequests);
        Assert.Equal(RiskLevels.High, request.Risk);
        Assert.Equal("t call", request.Summary);
        var report = Assert.Single(gw.Reports);
        Assert.Equal(ToolDecisions.Approved, report.Decision);
        Assert.Equal(pending.Id, report.ApprovalId);
    }

    [Theory]
    [InlineData(ApprovalStatuses.Denied, ApprovalDecidedBy.User, "denied_by_user", ToolCallStatuses.Denied, ToolDecisions.Denied)]
    [InlineData(ApprovalStatuses.Denied, ApprovalDecidedBy.System, "no_approver", ToolCallStatuses.Denied, ToolDecisions.Denied)]
    [InlineData(ApprovalStatuses.Expired, ApprovalDecidedBy.System, "approval_expired", ToolCallStatuses.Expired, ToolDecisions.Expired)]
    public async Task Refused_approvals_do_not_run_the_tool(
        string status, string decidedBy, string errorCode, string reportedStatus, string reportedDecision)
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.Medium, "overwrites"));
        var (d, gw, _) = Create(new IAgentTool[] { tool });
        gw.ApprovalReplies.Enqueue(RecordingGateway.Approval(status, decidedBy));

        var result = Parse(await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default));

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(errorCode, result.GetProperty("error").GetString());
        Assert.Equal(0, tool.Executions);
        var report = Assert.Single(gw.Reports);
        Assert.Equal(reportedStatus, report.Status);
        Assert.Equal(reportedDecision, report.Decision);
        Assert.Equal(RiskLevels.Medium, report.Risk);
    }

    [Fact]
    public async Task Approval_failure_fails_closed()
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.High, "deletes"));
        var (d, gw, _) = Create(new IAgentTool[] { tool });
        gw.FailApprovals = true;

        var result = Parse(await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default));

        Assert.Equal("approval_unavailable", result.GetProperty("error").GetString());
        Assert.Equal(0, tool.Executions);
    }

    [Fact]
    public async Task Blocked_calls_never_run_and_never_ask()
    {
        var tool = new FakeTool("t", new ToolAssessment.Blocked("path escapes the workspace"));
        var (d, gw, _) = Create(new IAgentTool[] { tool }, allowed: RiskLevel.Critical);

        var result = Parse(await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default));

        Assert.Equal("blocked", result.GetProperty("error").GetString());
        Assert.Equal(0, tool.Executions);
        Assert.Empty(gw.ApprovalRequests);
        Assert.Equal(ToolDecisions.Blocked, Assert.Single(gw.Reports).Decision);
    }

    [Fact]
    public async Task Unknown_tool_and_invalid_arguments_are_rejected()
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.Low, "x"));
        var (d, gw, _) = Create(new IAgentTool[] { tool });
        var turn = new TurnContext(Guid.NewGuid(), Guid.NewGuid());

        var unknown = Parse(await d.DispatchAsync(Call("nope"), turn, default));
        var badJson = Parse(await d.DispatchAsync(Call("t", "{not json"), turn, default));
        var notObject = Parse(await d.DispatchAsync(Call("t", "[1,2]"), turn, default));

        Assert.Equal("unknown_tool", unknown.GetProperty("error").GetString());
        Assert.Equal("invalid_arguments", badJson.GetProperty("error").GetString());
        Assert.Equal("invalid_arguments", notObject.GetProperty("error").GetString());
        Assert.Equal(0, tool.Executions);
        Assert.Equal(new[] { ToolCallStatuses.UnknownTool, ToolCallStatuses.InvalidArguments, ToolCallStatuses.InvalidArguments },
            gw.Reports.Select(r => r.Status));
    }

    [Fact]
    public async Task Uncertain_without_smart_mode_is_treated_as_high()
    {
        var tool = new FakeTool("t", new ToolAssessment.Uncertain("writes a script"));
        var (d, gw, _) = Create(new IAgentTool[] { tool }, allowed: RiskLevel.Medium);
        gw.ApprovalReplies.Enqueue(RecordingGateway.Approval(ApprovalStatuses.Denied, ApprovalDecidedBy.User));

        await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);

        Assert.Empty(gw.Classifications);
        Assert.Equal(RiskLevels.High, Assert.Single(gw.ApprovalRequests).Risk);
    }

    [Theory]
    [InlineData("low", 1, 0)]
    [InlineData("Critical.", 0, 1)]
    [InlineData("I think this is medium risk", 1, 0)]
    [InlineData("no idea", 0, 1)]
    [InlineData("not low - critical", 0, 1)]
    [InlineData("low, maybe medium", 0, 1)]
    [InlineData("LOW low Low", 1, 0)]
    public async Task Smart_mode_classifies_uncertain_calls(string reply, int expectedExecutions, int expectedApprovals)
    {
        var tool = new FakeTool("t", new ToolAssessment.Uncertain("writes a script"));
        var (d, gw, _) = Create(new IAgentTool[] { tool }, allowed: RiskLevel.Medium, smartMode: true);
        gw.ClassifierReply = reply;
        gw.ApprovalReplies.Enqueue(RecordingGateway.Approval(ApprovalStatuses.Denied, ApprovalDecidedBy.User));

        await d.DispatchAsync(Call("t", """{"content":"SECRET_FILE_CONTENT"}"""), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);

        Assert.Equal(expectedExecutions, tool.Executions);
        Assert.Equal(expectedApprovals, gw.ApprovalRequests.Count);
        var sent = string.Join("\n", Assert.Single(gw.Classifications).Select(m => m.Content));
        Assert.Contains("t call", sent);
        Assert.DoesNotContain("SECRET_FILE_CONTENT", sent);
    }

    [Fact]
    public async Task Untrusted_results_raise_the_risk_of_later_calls_in_the_turn()
    {
        var reader = new FakeTool("read", new ToolAssessment.Scored(RiskLevel.Low, "reads"), untrusted: true);
        var writer = new FakeTool("write", new ToolAssessment.Scored(RiskLevel.Low, "writes"));
        var (d, gw, _) = Create(new IAgentTool[] { reader, writer }, allowed: RiskLevel.Low);
        gw.ApprovalReplies.Enqueue(RecordingGateway.Approval(ApprovalStatuses.Denied, ApprovalDecidedBy.User));
        var turn = new TurnContext(Guid.NewGuid(), Guid.NewGuid());

        await d.DispatchAsync(Call("write"), turn, default);   // low, untainted → runs
        await d.DispatchAsync(Call("read"), turn, default);    // low, taints the turn
        await d.DispatchAsync(Call("write"), turn, default);   // low + 1 = medium → needs approval

        Assert.True(turn.Tainted);
        Assert.Equal(1, writer.Executions);
        var request = Assert.Single(gw.ApprovalRequests);
        Assert.Equal(RiskLevels.Medium, request.Risk);
        Assert.Contains("untrusted", request.Reason);
        Assert.Equal(new[] { false, false, true }, gw.Reports.Select(r => r.Tainted));

        // A new turn starts clean.
        await d.DispatchAsync(Call("write"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);
        Assert.Equal(2, writer.Executions);
    }

    [Fact]
    public async Task Tool_exceptions_become_errors_without_leaking_messages()
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.Low, "x"))
        {
            Execute = _ => throw new IOException("SECRET path detail"),
        };
        var (d, gw, logger) = Create(new IAgentTool[] { tool });

        var json = (await d.DispatchAsync(Call("t"), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default)).Content;

        Assert.Equal("tool_failed", Parse(json).GetProperty("error").GetString());
        Assert.DoesNotContain("SECRET", json);
        Assert.Equal(ToolCallStatuses.Failed, Assert.Single(gw.Reports).Status);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("SECRET"));
    }

    [Fact]
    public async Task Logs_never_contain_arguments()
    {
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.Low, "x"));
        var (d, _, logger) = Create(new IAgentTool[] { tool });

        await d.DispatchAsync(Call("t", """{"content":"SECRET_ARG"}"""), new TurnContext(Guid.NewGuid(), Guid.NewGuid()), default);

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("SECRET_ARG"));
    }

    [Theory]
    [InlineData(RiskLevel.Low, RiskLevel.Medium)]
    [InlineData(RiskLevel.High, RiskLevel.Critical)]
    [InlineData(RiskLevel.Critical, RiskLevel.Critical)]
    public void Raise_is_capped_at_critical(RiskLevel from, RiskLevel to) => Assert.Equal(to, from.Raise());
}
