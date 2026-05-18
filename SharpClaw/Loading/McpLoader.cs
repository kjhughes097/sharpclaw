using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Loading;

public sealed class McpLoader(
    IHostEnvironment env,
    IConfiguration configuration,
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
            try
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var json = File.ReadAllText(file);
                var config = JsonSerializer.Deserialize<McpServerDefinition>(json, JsonOptions);

                if (config is null)
                {
                    logger.LogWarning("Failed to parse MCP config: {File}", file);
                    continue;
                }

                config = ResolveConfigPlaceholders(config);
                servers.Add((name, config));
                logger.LogDebug("Loaded MCP server: {Name}", name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load MCP config file {File}; skipping", file);
            }
        }

        return servers;
    }

    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);

    private McpServerDefinition ResolveConfigPlaceholders(McpServerDefinition config)
    {
        var resolvedArgs = config.Args?.Select(ResolvePlaceholders).ToArray();
        var resolvedEnv = ResolveDictionary(config.Env);
        var resolvedHeaders = ResolveDictionary(config.Headers);

        return config with
        {
            Command = ResolvePlaceholders(config.Command),
            Args = resolvedArgs,
            Env = resolvedEnv,
            Url = ResolvePlaceholders(config.Url),
            Headers = resolvedHeaders,
        };
    }

    private IReadOnlyDictionary<string, string>? ResolveDictionary(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null) return null;

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
            resolved[key] = ResolvePlaceholders(value) ?? string.Empty;

        return resolved;
    }

    private string? ResolvePlaceholders(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        var current = value;
        var startIndex = current.IndexOf("${", StringComparison.Ordinal);

        while (startIndex >= 0)
        {
            var endIndex = current.IndexOf('}', startIndex + 2);
            if (endIndex < 0) break;

            var token = current[(startIndex + 2)..endIndex];
            var replacement = ResolveToken(token);
            current = string.Concat(current.AsSpan(0, startIndex), replacement, current.AsSpan(endIndex + 1));
            startIndex = current.IndexOf("${", StringComparison.Ordinal);
        }

        return current;
    }

    private string ResolveToken(string token)
    {
        var key = token.Trim();
        if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var envKey = key[4..];
            var envValue = Environment.GetEnvironmentVariable(envKey);
            if (envValue is not null)
                return envValue;

            logger.LogWarning("MCP placeholder {Token} could not be resolved from environment", token);
            return string.Empty;
        }

        var configValue = configuration[key];
        if (configValue is not null)
            return configValue;

        logger.LogWarning("MCP placeholder {Token} could not be resolved from configuration", token);
        return string.Empty;
    }
}
