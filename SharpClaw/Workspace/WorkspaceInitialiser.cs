using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;

namespace SharpClaw.Workspace;

public sealed class WorkspaceInitialiser(
    IOptions<SharpClawOptions> options,
    IAgentRegistry agentRegistry,
    ILogger<WorkspaceInitialiser> logger)
{
    public void Initialise()
    {
        var root = options.Value.WorkspacePath;
        if (string.IsNullOrWhiteSpace(root))
        {
            logger.LogWarning("WorkspacePath not configured — workspace initialisation skipped");
            return;
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "projects"));
        Directory.CreateDirectory(Path.Combine(root, "knowledge"));
        Directory.CreateDirectory(Path.Combine(root, "schedules"));

        foreach (var agent in agentRegistry.GetAll())
        {
            var agentDir = Path.Combine(root, agent.Name);
            Directory.CreateDirectory(agentDir);
        }

        logger.LogInformation("Workspace initialised at {Root}", root);
    }
}
