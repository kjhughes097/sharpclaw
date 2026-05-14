using SharpClaw.Abstractions;
using SharpClaw.Commands;
using SharpClaw.Models;
using SharpClaw.Registry;

namespace SharpClaw.Tests;

public class CommandRouterTests
{
    private readonly IAgentRegistry _agentRegistry = new AgentRegistry();

    public CommandRouterTests()
    {
        _agentRegistry.Register(new AgentDefinition("cody", "Coding assistant", null, null, [], [], [], [], null));
        _agentRegistry.Register(new AgentDefinition("myles", "Research agent", null, null, [], [], [], [], null));
    }

    [Fact]
    public async Task SwitchCommand_matches_dot_letter()
    {
        var cmd = new SwitchAgentCommand(_agentRegistry);
        Assert.True(cmd.CanHandle(".c"));
        Assert.False(cmd.CanHandle(".cc"));
        Assert.False(cmd.CanHandle("hello"));
    }

    [Fact]
    public async Task SwitchCommand_resolves_agent_by_first_letter()
    {
        var cmd = new SwitchAgentCommand(_agentRegistry);
        var result = await cmd.ExecuteAsync(new CommandContext("chan1", ".c", "myles"));

        Assert.True(result.Handled);
        Assert.Equal("cody", result.SwitchedToAgent);
        Assert.Contains("Switched to cody", result.ResponseText);
    }

    [Fact]
    public async Task PingCommand_returns_agent_info()
    {
        var cmd = new PingCommand(_agentRegistry);
        Assert.True(cmd.CanHandle("ping"));
        Assert.True(cmd.CanHandle("hi"));

        var result = await cmd.ExecuteAsync(new CommandContext("chan1", "ping", "cody"));
        Assert.True(result.Handled);
        Assert.Contains("cody", result.ResponseText);
        Assert.Contains("Coding assistant", result.ResponseText);
    }

    [Fact]
    public async Task PingCommand_no_agent_set()
    {
        var cmd = new PingCommand(_agentRegistry);
        var result = await cmd.ExecuteAsync(new CommandContext("chan1", "ping", null));
        Assert.Contains("No agent set yet", result.ResponseText);
    }

    [Fact]
    public async Task CommandRouter_returns_null_for_unmatched()
    {
        var router = new CommandRouter([new SwitchAgentCommand(_agentRegistry), new PingCommand(_agentRegistry)]);
        var result = await router.TryExecuteAsync(new CommandContext("chan1", "hello world", "cody"));
        Assert.Null(result);
    }
}
