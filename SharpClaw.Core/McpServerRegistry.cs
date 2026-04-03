using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace SharpClaw.Core;

/// <summary>
/// Builds MCP client transports from the stored MCP registry.
/// </summary>
public static class McpServerRegistry
{
    private const string AllowedDirsPlaceholder = "${SHARPCLAW_ALLOWED_DIRS}";
    private static readonly Regex EnvVarPattern = new(@"\$\{(?<name>[A-Z0-9_]+)\}", RegexOptions.Compiled);

    private static string[] ResolveAllowedDirs(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
            return [workspacePath.Trim()];

        return [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
    }

    /// <summary>
    /// Returns an <see cref="IClientTransport"/> for a stored MCP server definition.
    /// </summary>
    public static IClientTransport Resolve(McpServerRecord server, string? workspacePath = null)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
            throw new ArgumentException($"MCP '{server.Slug}' has no command configured.");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = server.Command,
            Arguments = ExpandArguments(server.Args, workspacePath),
            Name = server.Slug,
        });
    }

    private static List<string> ExpandArguments(IReadOnlyList<string> rawArgs, string? workspacePath)
    {
        var expanded = new List<string>();

        foreach (var arg in rawArgs)
        {
            if (string.Equals(arg, AllowedDirsPlaceholder, StringComparison.Ordinal))
            {
                expanded.AddRange(ResolveAllowedDirs(workspacePath));
                continue;
            }

            expanded.Add(ExpandEnvironmentVariables(arg));
        }

        return expanded;
    }

    private static string ExpandEnvironmentVariables(string value)
    {
        return EnvVarPattern.Replace(value, match =>
        {
            var name = match.Groups["name"].Value;
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        });
    }
}
