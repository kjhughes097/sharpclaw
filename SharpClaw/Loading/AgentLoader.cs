using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

public sealed class AgentLoader(
    IHostEnvironment env,
    IOptions<SharpClawOptions> options,
    ILogger<AgentLoader> logger)
{
    public IReadOnlyList<AgentDefinition> Load()
    {
        var dir = ResolvePath(options.Value.AgentsDirectory);

        if (!Directory.Exists(dir))
        {
            logger.LogDebug("Agents directory not found at {Dir} — no agents loaded", dir);
            return [];
        }

        var agents = new List<AgentDefinition>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.agent.md"))
        {
            var agent = AgentDefinitionParser.Parse(file);
            agents.Add(agent);
            logger.LogDebug("Loaded agent: {Name}", agent.Name);
        }

        return agents;
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}
