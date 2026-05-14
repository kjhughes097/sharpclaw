using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Registry;

namespace SharpClaw.Tests;

public class AgentRegistryTests
{
    private readonly IAgentRegistry _registry = new AgentRegistry();

    [Fact]
    public void Register_and_Get_returns_agent()
    {
        var agent = new AgentDefinition("test", "A test agent", null, "gpt-4o", [], [], [], [], "You are a test.");
        _registry.Register(agent);

        var result = _registry.Get("test");
        Assert.NotNull(result);
        Assert.Equal("test", result.Name);
    }

    [Fact]
    public void Get_is_case_insensitive()
    {
        var agent = new AgentDefinition("Cody", null, null, null, [], [], [], [], null);
        _registry.Register(agent);

        Assert.NotNull(_registry.Get("cody"));
        Assert.NotNull(_registry.Get("CODY"));
    }

    [Fact]
    public void Clear_removes_all_agents()
    {
        _registry.Register(new AgentDefinition("a", null, null, null, [], [], [], [], null));
        _registry.Register(new AgentDefinition("b", null, null, null, [], [], [], [], null));

        _registry.Clear();

        Assert.Empty(_registry.GetAll());
    }

    [Fact]
    public void Get_unknown_returns_null()
    {
        Assert.Null(_registry.Get("nonexistent"));
    }
}
