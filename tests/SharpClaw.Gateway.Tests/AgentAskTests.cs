using System.Net.Http.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Agent-to-agent asks (3.5 spec): the policy agents see (<c>GET /agent/tools</c>). The ask itself
/// is in the other <c>AgentAskTests.*.cs</c> files.
/// </summary>
public partial class AgentAskTests
{
    [Fact]
    public async Task Policy_lists_the_allowed_recipients_that_exist_in_config_order()
    {
        var config = AskConfig(("ade", "cody"), ("ade", "nobody"), ("ade", "bea"));
        config["AgentMessaging:Descriptions:cody"] = "  Coding agent.  ";
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var (_, adeToken) = await fx.CreateAgentAsync("ade");
        await fx.CreateAgentAsync("bea");
        var (cody, _) = await fx.CreateAgentAsync("cody");
        await DisableAsync(fx, cody.Id);

        var policy = await PolicyAsync(fx, adeToken);

        Assert.Contains(BrokerTools.AskAgent, policy.Tools);
        Assert.Equal(
            [new AskAgentTargetDto("cody", "Coding agent."), new AskAgentTargetDto("bea", null)],
            policy.AskAgents!);
    }

    [Fact]
    public async Task By_default_every_other_agent_is_a_recipient()
    {
        var config = BrokerTestHelpers.Config("ade", BrokerTools.AskAgent);
        config["AgentMessaging:Descriptions:cody"] = "Coding agent.";
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var (_, adeToken) = await fx.CreateAgentAsync("ade");
        await fx.CreateAgentAsync("cody");
        await fx.CreateAgentAsync("bea");

        Assert.Equal(
            [new AskAgentTargetDto("bea", null), new AskAgentTargetDto("cody", "Coding agent.")],
            (await PolicyAsync(fx, adeToken)).AskAgents!);
    }

    [Fact]
    public async Task Policy_has_no_recipients_without_the_tool_or_when_pairs_are_required()
    {
        var config = BrokerTestHelpers.Config("ade", BrokerTools.WebSearch);
        config["AgentMessaging:AllowAll"] = "false";
        config["AgentMessaging:Allowed:ade:0"] = "cody";
        config[$"ToolBroker:Agents:cody:Tools:0"] = BrokerTools.AskAgent;
        await using var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var (_, adeToken) = await fx.CreateAgentAsync("ade");
        var (_, codyToken) = await fx.CreateAgentAsync("cody");

        Assert.Empty((await PolicyAsync(fx, adeToken)).AskAgents!);  // ask_agent not enabled for ade
        Assert.Empty((await PolicyAsync(fx, codyToken)).AskAgents!); // cody has no pairs and AllowAll is off
    }

    [Fact]
    public void Options_match_names_ignoring_case_and_skip_blanks()
    {
        var o = new AgentMessagingOptions
        {
            Allowed = new() { ["Ade"] = ["cody", " ", "CODY", "bea", "nobody"] },
            Descriptions = new() { ["Cody"] = "codes", ["bea"] = " " },
        };
        string[] agents = ["ade", "Bea", "cody", "dan"];

        Assert.Equal(["cody", "Bea"], o.RecipientsOf("ade", agents));
        Assert.True(o.MayAsk("ADE", "Bea"));
        Assert.False(o.MayAsk("ade", "dan"));     // ade has its own list
        Assert.True(o.MayAsk("bea", "ade"));      // everyone else: AllowAll
        Assert.Equal(["ade", "cody", "dan"], o.RecipientsOf("bea", agents));
        Assert.False(o.MayAsk("bea", "BEA"));     // never itself
        Assert.Equal("codes", o.DescriptionOf("cody"));
        Assert.Null(o.DescriptionOf("bea"));

        o.AllowAll = false;
        Assert.False(o.MayAsk("bea", "ade"));
        Assert.Empty(o.RecipientsOf("bea", agents));
        Assert.True(o.MayAsk("ade", "cody"));
    }

    [Fact]
    public void A_long_description_is_refused()
    {
        var o = new AgentMessagingOptions { Descriptions = new() { ["cody"] = new string('x', 301) } };
        Assert.Contains("AgentMessaging:Descriptions:cody must be at most 300", o.Problem());
    }

    private static async Task<AgentToolPolicyResponse> PolicyAsync(GatewayFixture fx, string token)
    {
        using var c = fx.AgentClient(token);
        return (await c.GetFromJsonAsync<AgentToolPolicyResponse>("/agent/tools", SharpClawJson.Options))!;
    }

    private static async Task DisableAsync(GatewayFixture fx, Guid agentId)
    {
        using var admin = fx.Admin();
        (await admin.PatchAsJsonAsync($"/api/agents/{agentId}", new UpdateAgentRequest(null, null, false), SharpClawJson.Options))
            .EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gateway config letting each sender ask its recipients: <c>ask_agent</c> enabled for every agent
    /// named (so recipients can ask back where a test needs it) and the pairs in <c>AgentMessaging:Allowed</c>.
    /// </summary>
    private static Dictionary<string, string?> AskConfig(params (string Sender, string Recipient)[] pairs)
    {
        var config = new Dictionary<string, string?>();
        foreach (var name in pairs.SelectMany(p => new[] { p.Sender, p.Recipient }).Distinct())
            config[$"ToolBroker:Agents:{name}:Tools:0"] = BrokerTools.AskAgent;
        foreach (var group in pairs.GroupBy(p => p.Sender))
        {
            var i = 0;
            foreach (var (_, recipient) in group)
                config[$"AgentMessaging:Allowed:{group.Key}:{i++}"] = recipient;
        }
        return config;
    }
}
