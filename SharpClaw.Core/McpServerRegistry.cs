using ModelContextProtocol.Client;

namespace SharpClaw.Core;

/// <summary>
/// Maps well-known MCP server names to their transport configurations.
/// Add entries here for every server that agents can reference by name.
/// </summary>
public static class McpServerRegistry
{
    private static string[] AllowedDirs =>
        Environment.GetEnvironmentVariable("MCP_ALLOWED_DIRS")?.Split(':', StringSplitOptions.RemoveEmptyEntries)
        ?? [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];

    /// <summary>
    /// Returns an <see cref="IClientTransport"/> for a well-known server name.
    /// </summary>
    public static IClientTransport Resolve(string serverName) => serverName switch
    {
        "filesystem" => new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-filesystem", .. AllowedDirs],
            Name = "filesystem",
        }),
        "sqlite" => new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "npx",
            Arguments = ["-y", "@anthropic/mcp-server-sqlite"],
            Name = "sqlite",
        }),
        _ => throw new ArgumentException($"Unknown MCP server: '{serverName}'. Register it in McpServerRegistry."),
    };
}
