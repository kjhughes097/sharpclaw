using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

public sealed class ServiceLoader(
    IHostEnvironment env,
    IOptions<SharpClawOptions> options,
    ILogger<ServiceLoader> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public IReadOnlyList<ServiceDefinition> Load()
    {
        var dir = ResolvePath(options.Value.ServicesDirectory);

        if (!Directory.Exists(dir))
        {
            logger.LogDebug("Services directory not found at {Dir} — no services loaded", dir);
            return [];
        }

        var services = new List<ServiceDefinition>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var definition = JsonSerializer.Deserialize<ServiceDefinition>(json, JsonOptions);

                if (definition is null)
                {
                    logger.LogWarning("Failed to parse service config: {File}", file);
                    continue;
                }

                services.Add(definition);
                logger.LogDebug("Loaded service definition: {Name}", definition.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load service config file {File}; skipping", file);
            }
        }

        return services;
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}
