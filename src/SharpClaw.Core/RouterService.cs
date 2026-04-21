using System.Text;
using SharpClaw.Core;

namespace SharpClaw.Core;

/// <summary>
/// Per-turn router that uses a cheap/fast LLM call to select the best specialist agent.
/// Supports .agent-slug override and sticky fast-path (prefer continuity).
/// </summary>
public sealed class RouterService
{
    private readonly IReadOnlyList<AgentDefinition> _agents;
    private readonly AgentDefinition _routerAgent;

    public RouterService(IReadOnlyList<AgentDefinition> agents)
    {
        _agents = agents.Where(a => a.Slug != "router").ToList();
        _routerAgent = agents.FirstOrDefault(a => a.Slug == "router")
            ?? throw new InvalidOperationException("No router.agent.md found in agents directory.");
    }

    /// <summary>
    /// Selects the best agent for the current turn.
    /// Returns the agent slug.
    /// </summary>
    public async Task<AgentDefinition> RouteAsync(
        string userMessage,
        string? currentAgentSlug,
        ILlmService routerLlm,
        CancellationToken ct = default)
    {
        // 1. Check for .agent-slug override
        var overrideSlug = ExtractAgentOverride(userMessage);
        if (overrideSlug is not null)
        {
            var overrideAgent = _agents.FirstOrDefault(a =>
                string.Equals(a.Slug, overrideSlug, StringComparison.OrdinalIgnoreCase));
            if (overrideAgent is not null)
                return overrideAgent;
        }

        // 2. Call the router agent to pick a specialist
        var systemPrompt = BuildRouterPrompt(currentAgentSlug);
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, userMessage)
        };

        var responseBuilder = new StringBuilder();
        await foreach (var evt in routerLlm.StreamAsync(
            _routerAgent.Model,
            systemPrompt,
            history,
            tools: [],
            toolDispatcher: (_, _) => Task.FromResult(new ToolCallResult("")),
            ct))
        {
            if (evt is TokenEvent token)
                responseBuilder.Append(token.Text);
            else if (evt is DoneEvent done && done.Content is not null)
            {
                responseBuilder.Clear();
                responseBuilder.Append(done.Content);
            }
        }

        var selectedSlug = responseBuilder.ToString().Trim().ToLowerInvariant();

        // 3. Try to match the response to an agent
        var selectedAgent = _agents.FirstOrDefault(a =>
            string.Equals(a.Slug, selectedSlug, StringComparison.OrdinalIgnoreCase));

        // 4. Fallback to current agent if still set, else default to "ade"
        if (selectedAgent is null && currentAgentSlug is not null)
        {
            selectedAgent = _agents.FirstOrDefault(a =>
                string.Equals(a.Slug, currentAgentSlug, StringComparison.OrdinalIgnoreCase));
        }

        return selectedAgent
            ?? _agents.FirstOrDefault(a => string.Equals(a.Slug, "ade", StringComparison.OrdinalIgnoreCase))
            ?? _agents[0];
    }

    private string BuildRouterPrompt(string? currentAgentSlug)
    {
        var agentList = new StringBuilder();
        foreach (var agent in _agents)
        {
            agentList.AppendLine($"- **{agent.Slug}**: {agent.Description}");
        }

        var prompt = _routerAgent.SystemPrompt
            .Replace("{{AGENT_LIST}}", agentList.ToString().TrimEnd());

        if (currentAgentSlug is not null)
        {
            prompt += $"\n\nThe current agent is: {currentAgentSlug}. Prefer continuity unless the topic clearly changes.";
        }

        return prompt;
    }

    private static string? ExtractAgentOverride(string message)
    {
        // Look for .agent-slug at the start of the message
        var trimmed = message.TrimStart();
        if (!trimmed.StartsWith('.'))
            return null;

        var endIdx = trimmed.IndexOfAny([' ', '\n', '\t'], 1);
        var slug = endIdx > 0 ? trimmed[1..endIdx] : trimmed[1..];
        return slug.Length > 0 ? slug : null;
    }

    /// <summary>Gets an agent definition by slug.</summary>
    public AgentDefinition? GetAgent(string slug) =>
        _agents.FirstOrDefault(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>Gets all available specialist agents (excluding router).</summary>
    public IReadOnlyList<AgentDefinition> GetAgents() => _agents;
}
