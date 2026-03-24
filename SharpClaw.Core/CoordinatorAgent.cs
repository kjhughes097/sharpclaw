using System.Text.Json;

namespace SharpClaw.Core;

/// <summary>
/// Result of the coordinator's routing decision.
/// </summary>
public sealed record RoutingDecision(string? Agent, string? RewrittenPrompt);

/// <summary>
/// Runs a single-turn LLM call to classify user intent and select a specialist agent.
/// No tool loop — the coordinator only returns a JSON routing decision.
/// </summary>
public sealed class CoordinatorAgent
{
    private readonly IAgentBackend _backend;
    private readonly AgentPersona _persona;

    public CoordinatorAgent(IAgentBackend backend, AgentPersona persona)
    {
        _backend = backend;
        _persona = persona;
    }

    /// <summary>
    /// Asks the coordinator to pick an agent for the given user prompt.
    /// </summary>
    /// <param name="userPrompt">The raw user message to classify.</param>
    /// <param name="availableAgents">
    /// Map of agent slug → short description (loaded from each agent's Name field).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="RoutingDecision"/> with the chosen agent slug and rewritten prompt,
    /// or nulls if no match was found.</returns>
    public async Task<RoutingDecision> RouteAsync(
        string userPrompt,
        IReadOnlyDictionary<string, string> availableAgents,
        CancellationToken cancellationToken = default)
    {
        // Build the agent catalog into the system prompt so the model knows what's available.
        var agentList = string.Join("\n",
            availableAgents.Select(kvp => $"- {kvp.Key}: {kvp.Value}"));

        var systemPrompt = _persona.SystemPrompt + "\n\nAvailable agents:\n" + agentList;

        var history = new List<ChatMessage> { new(ChatRole.User, userPrompt) };

        // Single-turn call — no tools advertised.
        var response = await _backend.CompleteAsync(
            systemPrompt: systemPrompt,
            tools: [],
            history: history,
            toolDispatcher: (_, _) => Task.FromResult(new ToolCallResult("", IsError: true)),
            cancellationToken: cancellationToken);

        return ParseDecision(response);
    }

    private static RoutingDecision ParseDecision(string response)
    {
        // Strip markdown fences if the model wraps the JSON.
        var json = response.Trim();
        if (json.StartsWith("```"))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0)
                json = json[(firstNewline + 1)..];
            if (json.EndsWith("```"))
                json = json[..^3];
            json = json.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var agent = root.TryGetProperty("agent", out var agentProp) && agentProp.ValueKind == JsonValueKind.String
                ? agentProp.GetString()
                : null;

            var rewritten = root.TryGetProperty("rewritten_prompt", out var promptProp) && promptProp.ValueKind == JsonValueKind.String
                ? promptProp.GetString()
                : null;

            return new RoutingDecision(agent, rewritten);
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"Warning: Coordinator returned unparseable response: {response}");
            return new RoutingDecision(null, null);
        }
    }
}
