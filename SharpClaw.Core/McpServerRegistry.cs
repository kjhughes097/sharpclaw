using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace SharpClaw.Core;

public sealed record McpLaunchInfo(string Command, IReadOnlyList<string> Arguments, string DisplayCommand);

/// <summary>
/// Builds MCP client transports from the stored MCP registry.
/// </summary>
public static class McpServerRegistry
{
    private const string WorkspacePathPlaceholder = "${WORKSPACE_PATH}";
    private const string LegacyAllowedDirsPlaceholder = "${SHARPCLAW_ALLOWED_DIRS}";
    private const string KnowledgeBasePlaceholder = "${SHARPCLAW_KNOWLEDGE_BASE}";
    private static readonly Regex EnvVarPattern = new(@"\$\{(?<name>[A-Z0-9_]+)\}", RegexOptions.Compiled);

    private static string[] ResolveAllowedDirs(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
            return [workspacePath.Trim()];

        return [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
    }

    private static string ResolveKnowledgeBaseDir()
    {
        var configuredPath = Environment.GetEnvironmentVariable("SHARPCLAW_KNOWLEDGE_BASE");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath.Trim();

        if (Directory.Exists("/knowledge"))
            return "/knowledge";

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir))
            return Path.Combine(homeDir, "knowledge");

        return Path.Combine(Environment.CurrentDirectory, "knowledge");
    }

    /// <summary>
    /// Returns an <see cref="IClientTransport"/> for a stored MCP server definition.
    /// Uses <see cref="HttpClientTransport"/> for remote (URL-based) servers, and
    /// <see cref="StdioClientTransport"/> for local (command-based) servers.
    /// </summary>
    public static IClientTransport Resolve(McpServerRecord server, string? workspacePath = null)
    {
        if (server.IsRemote)
        {
            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url!),
                Name = server.Slug,
            });
        }

        var launch = ResolveLaunch(server, workspacePath);

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = launch.Command,
            Arguments = launch.Arguments.ToList(),
            Name = server.Slug,
        });
    }

    public static McpLaunchInfo ResolveLaunch(McpServerRecord server, string? workspacePath = null)
    {
        if (server.IsRemote)
            return new McpLaunchInfo(string.Empty, [], server.Url!);

        if (string.IsNullOrWhiteSpace(server.Command))
            throw new ArgumentException($"MCP '{server.Slug}' has no command configured.");

        var command = ResolveCommandPath(ExpandEnvironmentVariables(server.Command.Trim()));
        var arguments = ExpandArguments(server.Args, workspacePath);
        return new McpLaunchInfo(command, arguments, FormatCommand(command, arguments));
    }

    private static List<string> ExpandArguments(IReadOnlyList<string> rawArgs, string? workspacePath)
    {
        var expanded = new List<string>();

        foreach (var arg in rawArgs)
        {
            if (string.Equals(arg, WorkspacePathPlaceholder, StringComparison.Ordinal)
                || string.Equals(arg, LegacyAllowedDirsPlaceholder, StringComparison.Ordinal))
            {
                expanded.AddRange(ResolveAllowedDirs(workspacePath));
                continue;
            }

            if (string.Equals(arg, KnowledgeBasePlaceholder, StringComparison.Ordinal))
            {
                expanded.Add(ResolveKnowledgeBaseDir());
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

    private static string ResolveCommandPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("MCP command resolves to an empty value.");

        if (Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return command;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return candidate;

            if (!OperatingSystem.IsWindows())
                continue;

            var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM";
            foreach (var extension in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var withExtension = candidate + extension;
                if (File.Exists(withExtension))
                    return withExtension;
            }
        }

        throw new InvalidOperationException(
            $"Unable to resolve MCP command '{command}' from PATH. Ensure the executable is installed and available to the SharpClaw process.");
    }

    private static string FormatCommand(string command, IReadOnlyList<string> arguments)
    {
        return string.Join(" ", new[] { QuoteForDisplay(command) }.Concat(arguments.Select(QuoteForDisplay)));
    }

    private static string QuoteForDisplay(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        return '"' + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';
    }
}
