using SharpClaw.Abstractions;
using SharpClaw.Loading;

namespace SharpClaw.Workers;

public sealed class RegistryWorker(
    IAgentRegistry agentRegistry,
    IMcpRegistry mcpRegistry,
    ISkillRegistry skillRegistry,
    AgentLoader agentLoader,
    McpLoader mcpLoader,
    SkillLoader skillLoader,
    ILogger<RegistryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Reload();
        logger.LogInformation("SharpClaw registries loaded");
        return Task.CompletedTask;
    }

    public void Reload()
    {
        agentRegistry.Clear();
        mcpRegistry.Clear();
        skillRegistry.Clear();

        foreach (var agent in agentLoader.Load())
            agentRegistry.Register(agent);

        foreach (var (name, config) in mcpLoader.Load())
        {
            mcpRegistry.Register(name, config);
            logger.LogInformation("Registered MCP server {Name} (transport={Transport})", name, config.Transport);
        }

        foreach (var skill in skillLoader.Load())
            skillRegistry.Register(skill);

        logger.LogInformation(
            "Registries reloaded: {Agents} agents, {Mcps} MCPs, {Skills} skills",
            agentRegistry.GetAll().Count,
            mcpRegistry.GetAll().Count,
            skillRegistry.GetAll().Count);
    }
}
