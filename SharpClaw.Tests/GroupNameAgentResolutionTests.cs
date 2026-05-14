using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Registry;
using SharpClaw.Telegram;

namespace SharpClaw.Tests;

public class GroupNameAgentResolutionTests
{
    private readonly AgentRegistry _agentRegistry = new();
    private readonly TelegramAgentRouter _router = new();

    public GroupNameAgentResolutionTests()
    {
        _agentRegistry.Register(new AgentDefinition("myles", null, null, null, [], [], [], [], null));
        _agentRegistry.Register(new AgentDefinition("ade", null, null, null, [], [], [], [], null));
    }

    [Fact]
    public void Resolve_returns_agent_matching_group_title()
    {
        var result = _agentRegistry.Get("myles");

        Assert.NotNull(result);
        Assert.Equal("myles", result.Name);
    }

    [Fact]
    public void Resolve_is_case_insensitive_for_group_title()
    {
        Assert.NotNull(_agentRegistry.Get("Myles"));
        Assert.NotNull(_agentRegistry.Get("MYLES"));
    }

    [Fact]
    public void Resolve_returns_null_for_non_matching_title()
    {
        Assert.Null(_agentRegistry.Get("random-group-name"));
    }

    [Fact]
    public void Explicit_route_takes_precedence_over_group_title()
    {
        const long chatId = 12345;
        _router.Map(chatId, "ade");

        // Simulate resolution order: explicit route > group title > default
        var agentId = _router.Resolve(chatId) ?? _agentRegistry.Get("myles")?.Name ?? "default";

        Assert.Equal("ade", agentId);
    }

    [Fact]
    public void Group_title_takes_precedence_over_default_agent()
    {
        const long chatId = 99999;
        const string defaultAgent = "ade";

        // No explicit route — simulates a group titled "myles"
        var agentId = _router.Resolve(chatId) ?? _agentRegistry.Get("myles")?.Name ?? defaultAgent;

        Assert.Equal("myles", agentId);
    }

    [Fact]
    public void Falls_through_to_default_when_no_match()
    {
        const long chatId = 99999;
        const string defaultAgent = "ade";

        var agentId = _router.Resolve(chatId) ?? _agentRegistry.Get("no-such-agent")?.Name ?? defaultAgent;

        Assert.Equal("ade", agentId);
    }
}
