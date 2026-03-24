using ModelContextProtocol.Client;

namespace SharpClaw.Core;

/// <summary>
/// Extension methods for converting MCP tools to backend-neutral schemas.
/// </summary>
public static class McpToolExtensions
{
    /// <summary>
    /// Converts an <see cref="McpClientTool"/> to a backend-neutral <see cref="ToolSchema"/>.
    /// </summary>
    public static ToolSchema ToToolSchema(this McpClientTool tool, string name)
        => new(name, tool.Description, tool.JsonSchema);
}
