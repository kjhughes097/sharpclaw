namespace SharpClaw.Agent;

public sealed class AgentConfig
{
    public required string GatewayUrl { get; init; }
    public required string AgentToken { get; init; }
    public string SoulPath { get; init; } = "/config/soul.md";
    public string WorkspacePath { get; init; } = "/workspace";
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
