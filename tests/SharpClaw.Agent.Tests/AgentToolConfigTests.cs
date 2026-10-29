using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Utility;
using SharpClaw.Agent.Tools.Workspace;

namespace SharpClaw.Agent.Tests;

public class AgentToolConfigTests : IDisposable
{
    // The same union Program.cs passes in.
    private static readonly string[] Known = WorkspaceTools.Names.Concat(UtilityTools.Names).ToArray();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"agent-config-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    private AgentToolConfig? Load(string json)
    {
        File.WriteAllText(_path, json);
        return AgentToolConfig.Load(_path, Known);
    }

    [Fact]
    public void Missing_file_returns_null_so_the_agent_runs_without_tools() =>
        Assert.Null(AgentToolConfig.Load(_path, Known));

    [Fact]
    public void Valid_config_is_loaded()
    {
        var config = Load("""
        { "allowedRiskLevel": "medium", "smartMode": true, "maxToolIterations": 5,
          "tools": [ "workspace_read", "workspace_write", "workspace_read" ] }
        """)!;

        Assert.Equal(RiskLevel.Medium, config.AllowedRiskLevel);
        Assert.True(config.SmartMode);
        Assert.Equal(5, config.MaxToolIterations);
        Assert.Equal(new[] { "workspace_read", "workspace_write" }, config.Tools);
    }

    [Fact]
    public void Defaults_are_conservative()
    {
        var config = Load("{}")!;
        Assert.Equal(RiskLevel.Low, config.AllowedRiskLevel);
        Assert.False(config.SmartMode);
        Assert.Equal(10, config.MaxToolIterations);
        Assert.Empty(config.Tools);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "allowedRiskLevel": "yolo" }""")]
    [InlineData("""{ "maxToolIterations": 0 }""")]
    [InlineData("""{ "maxToolIterations": "ten" }""")]
    [InlineData("""{ "tools": "workspace_read" }""")]
    [InlineData("""{ "tools": [ "run_shell" ] }""")]
    public void Invalid_config_throws(string json) =>
        Assert.Throws<AgentToolConfigException>(() => Load(json));

    [Fact]
    public void Every_known_tool_name_is_accepted()
    {
        var config = Load(JsonSerializer.Serialize(new { tools = Known }))!;
        Assert.Equal(Known, config.Tools);
        Assert.Contains("current_time", config.Tools);
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("coder")]
    public void Shipped_agent_configs_load(string agent)
    {
        var path = Path.Combine(RepoRoot(), "docker", "template", "agents", agent, "config", "agent.json");
        var config = AgentToolConfig.Load(path, Known);
        Assert.NotNull(config);
        Assert.NotEmpty(config.Tools);
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
