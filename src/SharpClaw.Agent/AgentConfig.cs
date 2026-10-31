namespace SharpClaw.Agent;

public sealed class AgentConfig
{
    public required string GatewayUrl { get; init; }
    public required string AgentToken { get; init; }
    public string SoulPath { get; init; } = "/config/soul.md";
    public string WorkspacePath { get; init; } = "/workspace";

    /// <summary>Read-only tool policy (3.2). Missing file = chat only.</summary>
    public string ToolConfigPath { get; init; } = "/config/agent.json";

    /// <summary>The operator's shared, read-only skill library (skills spec section 4).</summary>
    public string SkillsPath { get; init; } = "/skills";
    public int LongPollSeconds { get; init; } = 30;
    public string LogLevel { get; init; } = "Information";

    public static AgentConfig FromEnvironment()
    {
        var gatewayUrl = Require("GATEWAY_URL");
        var token = Require("AGENT_TOKEN");
        return new AgentConfig
        {
            GatewayUrl = gatewayUrl,
            AgentToken = token,
            SoulPath = Environment.GetEnvironmentVariable("SOUL_PATH") ?? "/config/soul.md",
            WorkspacePath = Environment.GetEnvironmentVariable("WORKSPACE_PATH") ?? "/workspace",
            ToolConfigPath = Environment.GetEnvironmentVariable("AGENT_CONFIG_PATH") ?? "/config/agent.json",
            SkillsPath = Environment.GetEnvironmentVariable("SKILLS_PATH") ?? "/skills",
            LongPollSeconds = int.TryParse(Environment.GetEnvironmentVariable("LONG_POLL_SECONDS"), out var p) ? p : 30,
            LogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information",
        };
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Environment variable {name} is required.");
        return value;
    }
}
