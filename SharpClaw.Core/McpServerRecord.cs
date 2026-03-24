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
    bool IsEnabled);