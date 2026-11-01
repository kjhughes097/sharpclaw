using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Agent;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Broker;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Two real agents (HTTP client, loop, dispatcher, <c>ask_agent</c>) against the real gateway with a
/// scripted FakeProvider: the 3.5 exit criterion end to end. <c>ade</c> asks <c>cody</c> and finishes its
/// turn with cody's answer.
/// </summary>
public class AgentAskEndToEndTests
{
    private const string Script = """
    { "rules": [
      { "match": "^ask cody$", "role": "user",
        "toolCalls": [ { "name": "ask_agent", "arguments": { "agent": "cody", "message": "What is the answer?" } } ] },
      { "match": "^\\[Message from agent ade\\]\\n\\nWhat is the answer\\?$", "role": "user", "response": "42" },
      { "match": "\"reply\":\"42\"", "role": "tool", "response": "Cody says 42." }
    ] }
    """;

    [Fact]
    public async Task One_agent_asks_another_and_finishes_with_its_answer()
    {
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?>
        {
            ["Fake:ScriptPath"] = ChatTestHelpers.WriteScript(Script),
            ["ToolBroker:Agents:ade:Tools:0"] = BrokerTools.AskAgent,
            ["AgentMessaging:Allowed:ade:0"] = "cody",
            ["AgentMessaging:Descriptions:cody"] = "Knows the answer.",
        });
        var (ade, adeToken) = await fx.CreateAgentAsync("ade", budget: 10);
        var (cody, codyToken) = await fx.CreateAgentAsync("cody", budget: 10);
        var adeThread = await fx.CreateThreadAsync(ade.Id);

        // One HttpClient per agent: the gateway client sets its token on the client's default headers.
        using var adeHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var codyHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var adeLoop = await LoopAsync(fx, adeHttp, adeToken, [BrokerTools.AskAgent]);
        var codyLoop = await LoopAsync(fx, codyHttp, codyToken, []);

        await fx.PostUserMessageAsync(adeThread, "ask cody");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var adeTurn = adeLoop.ProcessOnceAsync(cts.Token);
        while (await codyLoop.ProcessOnceAsync(cts.Token) != LoopOutcome.Processed)
        {
        }
        Assert.Equal(LoopOutcome.Processed, await adeTurn);

        await using var db = fx.NewDb();
        var replies = await db.Messages.AsNoTracking()
            .Where(m => m.Role == Roles.Assistant && m.Kind == MessageKinds.Message)
            .ToDictionaryAsync(m => m.AgentId, m => m.Content);
        Assert.Equal("Cody says 42.", replies[ade.Id]);
        Assert.Equal("42", replies[cody.Id]);

        var linked = await db.Threads.AsNoTracking().SingleAsync(t => t.AgentId == cody.Id);
        Assert.Equal((adeThread, ade.Id), (linked.OriginThreadId!.Value, linked.OriginAgentId!.Value));

        // The asker's tool result is untrusted, and the call is audited on both sides.
        var result = await db.Messages.AsNoTracking().SingleAsync(m => m.ThreadId == adeThread && m.Kind == MessageKinds.ToolResult);
        Assert.True(result.Untrusted);
        Assert.Contains("\"reply\":\"42\"", result.Content);
        Assert.Equal(BrokerTools.AskAgent, (await db.ToolCalls.AsNoTracking().SingleAsync()).ToolName);
        Assert.Equal("cody", (await db.BrokerCalls.AsNoTracking().SingleAsync()).Target);
        Assert.Equal(2, await db.LlmCalls.CountAsync(c => c.AgentId == ade.Id));
        Assert.Equal(1, await db.LlmCalls.CountAsync(c => c.AgentId == cody.Id));
    }

    private static async Task<AgentLoop> LoopAsync(GatewayFixture fx, HttpClient http, string token, string[] tools)
    {
        var config = new AgentConfig
        {
            GatewayUrl = fx.AgentBaseUrl,
            AgentToken = token,
            SoulPath = "/nonexistent",
            WorkspacePath = Path.GetTempPath(),
            LongPollSeconds = 1,
        };
        var gateway = new GatewayHttpClient(http, config);
        var policy = await BrokerPolicyLoader.LoadAsync(gateway, NullLogger.Instance, CancellationToken.None);
        var toolConfig = new AgentToolConfig(RiskLevel.Low, SmartMode: false, MaxToolIterations: 5, tools);
        var registry = new ToolRegistry(AgentBrokerTools.All(gateway, policy, config.WorkspacePath),
            AgentBrokerTools.DropIfNoRecipients(toolConfig.Tools, policy, NullLogger.Instance));
        var dispatcher = new ToolDispatcher(registry, toolConfig, gateway, NullLogger<ToolDispatcher>.Instance);
        return new AgentLoop(gateway, new Soul(), config, NullLogger<AgentLoop>.Instance, TimeProvider.System,
            new AgentTools(registry, dispatcher, toolConfig.MaxToolIterations));
    }

    private sealed class Soul : ISoulProvider
    {
        public string Read() => "You are a test agent.";
    }
}
