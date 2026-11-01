using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Broker;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

/// <summary><c>ask_agent</c> on the agent side (3.5 spec section 6): recipients, risk, summaries and taint.</summary>
public class AskAgentToolTests
{
    private static readonly BrokerPolicy Policy = new(new HashSet<string>(), Array.Empty<string>())
    {
        AskAgents = [new AskAgentTargetDto("cody", "Coding agent."), new AskAgentTargetDto("bea", null)],
    };

    private static JsonElement A(object args) => JsonSerializer.SerializeToElement(args);

    private static AskAgentTool Tool(RecordingGateway? gw = null, BrokerPolicy? policy = null) =>
        new(gw ?? new RecordingGateway(), policy ?? Policy);

    [Fact]
    public void Description_and_schema_list_the_recipients_from_the_gateway()
    {
        var tool = Tool();

        Assert.Contains("- cody: Coding agent.", tool.Description);
        Assert.Contains("- bea", tool.Description);
        var schema = JsonDocument.Parse(tool.ParametersJson).RootElement;
        Assert.Equal(["cody", "bea"], schema.GetProperty("properties").GetProperty("agent").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()));
        Assert.True(tool.ReturnsUntrustedContent);
    }

    [Theory]
    [InlineData("zed", "hi")]
    [InlineData("", "hi")]
    [InlineData("cody", " ")]
    public void Unknown_recipients_and_empty_messages_are_blocked(string agent, string message) =>
        Assert.IsType<ToolAssessment.Blocked>(Tool().Assess(A(new { agent, message })));

    [Fact]
    public void A_valid_ask_is_low_risk_and_summaries_carry_no_content()
    {
        var tool = Tool();
        var args = A(new { agent = "CODY", message = "SECRET question" });

        Assert.Equal(RiskLevel.Low, Assert.IsType<ToolAssessment.Scored>(tool.Assess(args)).Level);
        Assert.Equal("ask cody (15 chars)", tool.Summarize(args));
        Assert.Equal(new Dictionary<string, string> { ["tool"] = "ask_agent", ["agent"] = "cody", ["chars"] = "15" },
            tool.ClassifierFields(args));
    }

    [Fact]
    public async Task The_turns_taint_is_passed_on_and_raises_the_risk()
    {
        var gw = new RecordingGateway();
        gw.ApprovalReplies.Enqueue(RecordingGateway.Approval(ApprovalStatuses.Approved, ApprovalDecidedBy.User));
        var tool = Tool(gw);
        var config = new AgentToolConfig(RiskLevel.Low, false, 10, [BrokerTools.AskAgent]);
        var dispatcher = new ToolDispatcher(new ToolRegistry([tool], config.Tools), config, gw, new ListLogger<ToolDispatcher>());

        var clean = new TurnContext(Guid.NewGuid(), Guid.NewGuid());
        await dispatcher.DispatchAsync(new ToolCallRequest("c1", BrokerTools.AskAgent, """{"agent":"cody","message":"hi"}"""), clean, default);
        Assert.False(gw.ToolInvocations[0].Request.Tainted);
        Assert.Empty(gw.ApprovalRequests);
        Assert.True(clean.Tainted); // the reply is untrusted

        var tainted = new TurnContext(Guid.NewGuid(), Guid.NewGuid()) { Tainted = true };
        await dispatcher.DispatchAsync(new ToolCallRequest("c2", BrokerTools.AskAgent, """{"agent":"cody","message":"hi"}"""), tainted, default);
        Assert.True(gw.ToolInvocations[1].Request.Tainted);
        Assert.Equal(RiskLevels.Medium, Assert.Single(gw.ApprovalRequests).Risk);
    }

    [Fact]
    public void The_tool_is_not_offered_when_the_gateway_allows_no_recipients()
    {
        var logger = new ListLogger<object>();
        var enabled = new[] { "workspace_read", BrokerTools.AskAgent };

        Assert.Equal(["workspace_read"], AgentBrokerTools.DropIfNoRecipients(enabled, BrokerPolicy.Conservative, logger));
        Assert.Equal(enabled, AgentBrokerTools.DropIfNoRecipients(enabled, Policy, logger));
        Assert.Single(logger.Lines);
    }

    [Fact]
    public async Task Policy_loader_reads_the_recipients()
    {
        var gw = new RecordingGateway
        {
            Policy = new AgentToolPolicyResponse([BrokerTools.AskAgent], [], [], AskAgents: [new AskAgentTargetDto("cody", null)]),
        };
        var loaded = await BrokerPolicyLoader.LoadAsync(gw, new ListLogger<object>(), default);
        Assert.Equal("cody", Assert.Single(loaded.AskAgents).Name);
    }
}
