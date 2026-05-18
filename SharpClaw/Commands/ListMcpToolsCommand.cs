using System.Text.Json;
using SharpClaw.Abstractions;
using SharpClaw.Execution;

namespace SharpClaw.Commands;

public sealed class ListMcpToolsCommand(
    IAgentRegistry agentRegistry,
    IMcpRegistry mcpRegistry,
    ILoggerFactory loggerFactory) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".lsmt", StringComparison.OrdinalIgnoreCase);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.CurrentAgentId))
            return new CommandResult(true, "No active agent. Switch to an agent first, then run .lsmt.");

        var agent = agentRegistry.Get(context.CurrentAgentId);
        if (agent is null)
            return new CommandResult(true, $"No agent registered with name '{context.CurrentAgentId}'.");

        var allServers = mcpRegistry.GetAll();
        var selectedServers = agent.McpNames.Count == 0
            ? allServers
            : allServers
                .Where(kvp => agent.McpNames.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        if (selectedServers.Count == 0)
            return new CommandResult(true, $"Agent '{agent.Name}' has no resolvable MCP servers.");

        await using var bridge = await McpToolBridge.CreateAsync(selectedServers, loggerFactory, ct);
        if (bridge.Tools.Count == 0)
            return new CommandResult(true, $"No MCP tools discovered for agent '{agent.Name}'.");

        var lines = new List<string>
        {
            $"MCP tools for {agent.Name} ({bridge.Tools.Count}):"
        };

        foreach (var tool in bridge.Tools.OrderBy(GetToolName, StringComparer.OrdinalIgnoreCase))
        {
            var name = GetToolName(tool);
            var description = GetToolDescription(tool);
            var inputSchema = GetToolInputSchema(tool);

            lines.Add($"• {name}{(string.IsNullOrWhiteSpace(description) ? string.Empty : $" — {description}")}");
            if (!string.IsNullOrWhiteSpace(inputSchema))
                lines.Add($"  schema: {inputSchema}");
        }

        return new CommandResult(true, string.Join('\n', lines));
    }

    private static string GetToolName(object tool) =>
        ReadProperty(tool, "Name") ?? "<unknown>";

    private static string? GetToolDescription(object tool) =>
        ReadProperty(tool, "Description");

    private static string? GetToolInputSchema(object tool)
    {
        var schemaValue = ReadPropertyValue(tool, "InputSchema")
            ?? ReadPropertyValue(tool, "JsonSchema")
            ?? ReadPropertyValue(tool, "Schema");

        if (schemaValue is null) return null;

        var raw = schemaValue switch
        {
            JsonElement element => element.GetRawText(),
            _ => schemaValue.ToString()
        };

        if (string.IsNullOrWhiteSpace(raw)) return null;

        const int maxLength = 220;
        return raw.Length <= maxLength ? raw : $"{raw[..maxLength]}...";
    }

    private static string? ReadProperty(object source, string propertyName) =>
        ReadPropertyValue(source, propertyName)?.ToString();

    private static object? ReadPropertyValue(object source, string propertyName)
    {
        var prop = source.GetType().GetProperty(propertyName);
        return prop?.GetValue(source);
    }
}
