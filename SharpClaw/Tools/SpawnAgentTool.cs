using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Tools;

public sealed class SpawnAgentTool(IAgentRegistry agentRegistry, Execution.AgentRunner runner) : ITool
{
    public string Name => "spawn_agent";
    public string Description => "Invoke another agent with a prompt and get its response.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("agent_name", "string", "The name of the agent to spawn.", Required: true),
        new("prompt", "string", "The prompt to send to the agent.", Required: true),
    ];

    public async Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var agentName = context.GetString("agent_name");
        var prompt = context.GetString("prompt");

        var agent = agentRegistry.Get(agentName);
        if (agent is null)
            return $"Error: agent '{agentName}' not found.";

        var request = new AgentRunRequest(
            Prompt: prompt,
            Llm: agent.Llm,
            Model: agent.Model,
            SystemPromptOverride: agent.SystemPrompt,
            McpServerNames: agent.McpNames.Count > 0 ? agent.McpNames : null,
            ToolNames: agent.ToolNames.Count > 0 ? agent.ToolNames : null
        );

        var result = await runner.RunAsync(request, ct);
        return result.Success ? result.Response : $"Error: {result.Error}";
    }
}
