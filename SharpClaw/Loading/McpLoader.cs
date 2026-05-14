using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

public sealed class McpLoader(
    IHostEnvironment env,
    IOptions<SharpClawOptions> options,
    ILogger<McpLoader> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<(string Name, McpServerDefinition Config)> Load()
    {
        var dir = ResolvePath(options.Value.McpsDirectory);

        if (!Directory.Exists(dir))
        {
            logger.LogDebug("MCPs directory not found at {Dir} — no MCP servers loaded", dir);
            return [];
        }

        var servers = new List<(string, McpServerDefinition)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var json = File.ReadAllText(file);
            var config = JsonSerializer.Deserialize<McpServerDefinition>(json, JsonOptions);

            if (config is null)
            {
                logger.LogWarning("Failed to parse MCP config: {File}", file);
                continue;
            }

            servers.Add((name, config));
            logger.LogDebug("Loaded MCP server: {Name}", name);
        }

        return servers;
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}
