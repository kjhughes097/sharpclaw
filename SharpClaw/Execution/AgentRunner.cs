using Microsoft.Extensions.AI;
using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Scheduling;

namespace SharpClaw.Execution;

public sealed class AgentRunner(
    IToolRegistry toolRegistry,
    IMcpRegistry mcpRegistry,
    ISkillRegistry skillRegistry,
    IEnumerable<ILlmProvider> providers,
    SchedulingContextAccessor schedulingContextAccessor,
    ILogger<AgentRunner> logger)
{
    private readonly Dictionary<string, ILlmProvider> _providers =
        providers.ToDictionary(p => p.ProviderName, p => p, StringComparer.OrdinalIgnoreCase);

    public async Task<ILlmSession> CreateSessionAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        var provider = ResolveProvider(request.Llm);

        var adapters = ResolveToolAdapters(request);
        var tools = adapters.Cast<AIFunction>().ToList();
        var mcpServers = ResolveMcpServers(request);
        var systemPrompt = BuildSystemPrompt(request);

        var llmRequest = new LlmSessionRequest(
            Model: request.Model,
            SystemPrompt: systemPrompt,
            Tools: tools,
            McpServers: mcpServers,
            ResumeSessionId: request.ResumeSessionId
        );

        return await provider.CreateSessionAsync(llmRequest, ct);
    }

    public async Task<AgentRunResult> SendAsync(ILlmSession session, string prompt, string? llm = null, CancellationToken ct = default)
    {
        var provider = ResolveProvider(llm);
        return await provider.SendAsync(session, prompt, ct);
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        await using var session = await CreateSessionAsync(request, ct);
        return await SendAsync(session, request.Prompt, request.Llm, ct);
    }

    private ILlmProvider ResolveProvider(string? llm)
    {
        var name = llm ?? "copilot";
        if (_providers.TryGetValue(name, out var provider))
            return provider;

        throw new InvalidOperationException($"No LLM provider registered with name '{name}'.");
    }

    private List<ToolAIFunctionAdapter> ResolveToolAdapters(AgentRunRequest request)
    {
        var toolNames = request.ToolNames;
        var tools = toolNames is null
            ? toolRegistry.GetAll().ToList()
            : toolNames.Select(n => toolRegistry.Get(n)).Where(t => t is not null).Cast<ITool>().ToList();

        return tools.Select(t =>
        {
            var adapter = new ToolAIFunctionAdapter(t, schedulingContextAccessor);
            adapter.CaptureSchedulingContext();
            return adapter;
        }).ToList();
    }

    private IReadOnlyDictionary<string, McpServerDefinition> ResolveMcpServers(AgentRunRequest request)
    {
        var mcpNames = request.McpServerNames;
        var allServers = mcpRegistry.GetAll();

        var entries = mcpNames is null
            ? allServers.ToList()
            : allServers.Where(kvp => mcpNames.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase)).ToList();

        return entries.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private string? BuildSystemPrompt(AgentRunRequest request)
    {
        if (request.SystemPromptOverride is not null)
            return AppendSkillPrompts(request.SystemPromptOverride, request.ToolNames);

        return null;
    }

    private string AppendSkillPrompts(string basePrompt, IReadOnlyList<string>? agentSkillNames)
    {
        // If no specific skills listed, no injection
        if (agentSkillNames is null) return basePrompt;

        var allSkills = skillRegistry.GetAll();
        // Skills referenced by agent are injected as prompt text
        // Note: we reuse ToolNames to carry skill info from agent definition
        // In practice, skill names come from the agent's SkillNames field
        return basePrompt;
    }
}
