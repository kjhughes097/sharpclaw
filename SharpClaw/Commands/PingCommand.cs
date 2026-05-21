using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class PingCommand(IAgentRegistry agentRegistry) : ICommand
{
    private static readonly HashSet<string> Triggers = new(StringComparer.OrdinalIgnoreCase) { "hi", "ping" };

    public bool CanHandle(string text) => Triggers.Contains(text.Trim());

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.CurrentAgentId is null)
            return Task.FromResult(new CommandResult(true, "No agent set yet."));

        var agent = agentRegistry.Get(context.CurrentAgentId);
        if (agent is null)
            return Task.FromResult(new CommandResult(true, "No agent set yet."));

        // Capitalize first letter of agent name
        var agentNameFormatted = char.ToUpperInvariant(agent.Name[0]) + agent.Name[1..];
        var desc = agent.Description ?? "No description available.";
        
        var toolsList = agent.ToolNames.Count > 0 
            ? string.Join(", ", agent.ToolNames) 
            : "None";
        var mcpList = agent.McpNames.Count > 0 
            ? string.Join(", ", agent.McpNames) 
            : "None";
        var subAgentList = agent.SubAgentNames.Count > 0 
            ? string.Join(", ", agent.SubAgentNames) 
            : "None";

        var response = $"**{agentNameFormatted}**\n{desc}\n\n| | |\n|---|---|\n| **LLM** | {agent.Llm ?? "copilot"} |\n| **Model** | {agent.Model ?? "default"} |\n| **Tools** | {toolsList} |\n| **MCPs** | {mcpList} |\n| **Sub Agents** | {subAgentList} |";

        return Task.FromResult(new CommandResult(true, response));
    }
}
