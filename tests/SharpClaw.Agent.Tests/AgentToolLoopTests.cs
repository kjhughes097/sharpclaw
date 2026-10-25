using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary>The agent's tool loop (3.2 spec section 7) against a scripted gateway.</summary>
public class AgentToolLoopTests
{
    private static readonly InboxMessageDto Message = new(Guid.NewGuid(), Guid.NewGuid(), "do the task", DateTime.UtcNow);

    /// <summary>Returns scripted chat outcomes in order and records every request.</summary>
    private sealed class ScriptedGateway : IGatewayClient
    {
        public Queue<ChatOutcome> Outcomes { get; } = new();
        public List<(List<ChatMessage> Messages, List<string> Tools)> Requests { get; } = new();
        public List<string> Replies { get; } = new();
        public List<string> Failures { get; } = new();
        public List<ToolCallReportRequest> Reports { get; } = new();

        public Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct) => Task.FromResult(InboxResult.Of(Message));
        public Task<IReadOnlyList<ChatMessage>> GetThreadHistoryAsync(Guid threadId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChatMessage>>(new[] { new ChatMessage(Roles.User, "do the task") });

        public Task<ChatOutcome> StreamChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<IAgentTool> tools,
            Guid correlationId, Guid threadId, CancellationToken ct)
        {
            Requests.Add((messages.ToList(), tools.Select(t => t.Name).ToList()));
            return Task.FromResult(Outcomes.Dequeue());
        }

        public Task PostReplyAsync(Guid threadId, string content, Guid correlationId, CancellationToken ct)
        {
            Replies.Add(content);
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid messageId, string reason, CancellationToken ct)
        {
            Failures.Add(reason);
            return Task.CompletedTask;
        }

        public Task ReportToolCallAsync(ToolCallReportRequest report, CancellationToken ct)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<ApprovalDto> RequestApprovalAsync(CreateApprovalRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApprovalDto> PollApprovalAsync(Guid approvalId, int waitSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ClassifyRiskAsync(IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private static ChatOutcome ToolCall(string id, string name, string args = "{}") =>
        ChatOutcome.Ok("", new[] { new ChatToolCall(id, name, args) });

    private static (AgentLoop Loop, ScriptedGateway Gateway, FakeTool Tool) Create(int maxIterations = 10, bool withTools = true)
    {
        var gateway = new ScriptedGateway();
        var tool = new FakeTool("t", new ToolAssessment.Scored(RiskLevel.Low, "safe"))
        {
            Execute = _ => ToolResult.Success(new { value = 42 }),
        };
        var config = new AgentToolConfig(RiskLevel.Low, false, maxIterations, new[] { "t" });
        var registry = new ToolRegistry(new IAgentTool[] { tool }, config.Tools);
        var dispatcher = new ToolDispatcher(registry, config, gateway, NullLogger<ToolDispatcher>.Instance);
        var agentConfig = new AgentConfig { GatewayUrl = "http://gw", AgentToken = "sc_x", SoulPath = "/nope" };
        var loop = new AgentLoop(gateway, new FixedSoul(), agentConfig, NullLogger<AgentLoop>.Instance, new FakeTimeProvider(),
            withTools ? new AgentTools(registry, dispatcher, maxIterations) : null);
        return (loop, gateway, tool);
    }

    private sealed class FixedSoul : ISoulProvider
    {
        public string Read() => "SOUL";
    }

    [Fact]
    public async Task Multi_step_turn_runs_tools_and_posts_only_the_final_reply()
    {
        var (loop, gw, tool) = Create();
        gw.Outcomes.Enqueue(ToolCall("call_1", "t"));
        gw.Outcomes.Enqueue(ToolCall("call_2", "t"));
        gw.Outcomes.Enqueue(ChatOutcome.Ok("All done."));

        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "All done." }, gw.Replies);
        Assert.Empty(gw.Failures);
        Assert.Equal(2, tool.Executions);
        Assert.Equal(3, gw.Requests.Count);
        Assert.All(gw.Requests, r => Assert.Equal(new[] { "t" }, r.Tools));

        // The last request carries: system, user, then (assistant tool call + tool result) twice.
        var last = gw.Requests[^1].Messages;
        Assert.Equal(new[] { Roles.System, Roles.User, Roles.Assistant, Roles.Tool, Roles.Assistant, Roles.Tool },
            last.Select(m => m.Role));
        Assert.Null(last[2].Content);
        Assert.Equal("call_1", Assert.Single(last[2].ToolCalls!).Id);
        Assert.Equal("call_1", last[3].ToolCallId);
        Assert.Contains("\"value\":42", last[3].Content);
        Assert.Equal(new[] { "call_1", "call_2" }, gw.Reports.Select(r => r.ToolCallId));
    }

    [Fact]
    public async Task Too_many_tool_rounds_fail_the_message()
    {
        var (loop, gw, tool) = Create(maxIterations: 2);
        for (var i = 0; i < 5; i++)
            gw.Outcomes.Enqueue(ToolCall($"call_{i}", "t"));

        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "tool_loop_limit" }, gw.Failures);
        Assert.Empty(gw.Replies);
        Assert.Equal(2, tool.Executions);
        Assert.Equal(3, gw.Requests.Count);
    }

    [Fact]
    public async Task Without_tools_nothing_is_offered_and_tool_calls_fail_the_message()
    {
        var (loop, gw, tool) = Create(withTools: false);
        gw.Outcomes.Enqueue(ToolCall("call_1", "t"));

        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(Assert.Single(gw.Requests).Tools);
        Assert.Equal(new[] { "tool_loop_limit" }, gw.Failures);
        Assert.Equal(0, tool.Executions);
    }

    [Fact]
    public async Task Chat_failure_mid_turn_fails_with_its_code()
    {
        var (loop, gw, _) = Create();
        gw.Outcomes.Enqueue(ToolCall("call_1", "t"));
        gw.Outcomes.Enqueue(ChatOutcome.Fail("loop_limit_exceeded"));

        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "loop_limit_exceeded" }, gw.Failures);
        Assert.Empty(gw.Replies);
    }
}
