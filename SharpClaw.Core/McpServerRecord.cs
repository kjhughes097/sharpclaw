namespace SharpClaw.Core;

/// <summary>
/// Represents a stored MCP server definition from the database.
/// </summary>
public sealed record McpServerRecord(
    string Slug,
    string Name,
    string Description,
    string Command,
    IReadOnlyList<string> Args,
    bool IsEnabled,
    string? Url = null)
{
    /// <summary>
    /// True when this MCP is configured as a remote HTTP/SSE server rather than a local stdio process.
    /// </summary>
    public bool IsRemote => !string.IsNullOrWhiteSpace(Url);
}