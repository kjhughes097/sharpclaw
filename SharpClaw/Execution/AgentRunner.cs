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

        logger.LogInformation(
            "Creating session with provider={Provider}, model={Model}, requestedTools={RequestedTools}, requestedMcps={RequestedMcps}",
            provider.ProviderName,
            request.Model ?? "<default>",
            FormatNames(request.ToolNames),
            FormatNames(request.McpServerNames));

        var adapters = ResolveToolAdapters(request);
        var tools = adapters.Cast<AIFunction>().ToList();
        var (eagerMcps, lazyMcps) = ResolveMcpServers(request);
        var systemPrompt = BuildSystemPrompt(request, lazyMcps.Keys.ToList());

        logger.LogInformation(
            "Resolved session configuration: tools={ToolCount}, mcps={McpCount} ({McpNames}), lazyMcps={LazyCount} ({LazyNames})",
            tools.Count,
            eagerMcps.Count,
            eagerMcps.Count == 0 ? "none" : string.Join(", ", eagerMcps.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
            lazyMcps.Count,
            lazyMcps.Count == 0 ? "none" : string.Join(", ", lazyMcps.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

        var llmRequest = new LlmSessionRequest(
            Model: request.Model,
            SystemPrompt: systemPrompt,
            Tools: tools,
            McpServers: eagerMcps,
            LazyMcpServers: lazyMcps.Count > 0 ? lazyMcps : null,
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
        List<ITool> tools;

        if (toolNames is null)
        {
            tools = toolRegistry.GetAll().ToList();
            logger.LogInformation("No tool filter specified; exposing all {Count} tools", tools.Count);
        }
        else
        {
            var resolved = toolNames.Select(n => toolRegistry.Get(n)).ToList();
            var missing = toolNames.Where((name, index) => resolved[index] is null).ToList();
            tools = resolved.Where(t => t is not null).Cast<ITool>().ToList();

            logger.LogInformation(
                "Requested tools resolved: requested={RequestedCount}, resolved={ResolvedCount}, missing={MissingCount}",
                toolNames.Count,
                tools.Count,
                missing.Count);

            if (missing.Count > 0)
            {
                logger.LogWarning("Missing tool registrations: {MissingTools}", string.Join(", ", missing));
            }
        }

        return tools.Select(t =>
        {
            var adapter = new ToolAIFunctionAdapter(t, schedulingContextAccessor);
            adapter.CaptureSchedulingContext();
            return adapter;
        }).ToList();
    }

    private (Dictionary<string, McpServerDefinition> Eager, Dictionary<string, McpServerDefinition> Lazy) ResolveMcpServers(AgentRunRequest request)
    {
        var mcpNames = request.McpServerNames;
        var allServers = mcpRegistry.GetAll();

        logger.LogInformation(
            "MCP registry contains {Count} server(s): {Names}",
            allServers.Count,
            allServers.Count == 0 ? "none" : string.Join(", ", allServers.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

        if (mcpNames is not null)
        {
            var missing = mcpNames
                .Where(name => !allServers.ContainsKey(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (missing.Count > 0)
            {
                logger.LogWarning("Requested MCP servers not registered: {MissingMcps}", string.Join(", ", missing));
            }
        }

        var entries = mcpNames is null
            ? allServers.ToList()
            : allServers.Where(kvp => mcpNames.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase)).ToList();

        logger.LogInformation(
            "Resolved MCP servers: requested={Requested}, resolved={Resolved}",
            FormatNames(mcpNames),
            entries.Count == 0 ? "none" : string.Join(", ", entries.Select(x => x.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));

        // Per-agent lazy_mcps override the global Lazy flag on the definition.
        // If LazyMcpNames is specified, those names are lazy and all others are eager.
        // If not specified, fall back to the global Lazy flag on each definition.
        var lazyMcpNames = request.LazyMcpNames;

        var eager = new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase);
        var lazy = new Dictionary<string, McpServerDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, definition) in entries)
        {
            var isLazy = lazyMcpNames is not null
                ? lazyMcpNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                : definition.Lazy;

            if (isLazy)
                lazy[name] = definition;
            else
                eager[name] = definition;
        }

        return (eager, lazy);
    }

    private static string FormatNames(IReadOnlyList<string>? names)
    {
        if (names is null) return "<all>";
        if (names.Count == 0) return "<none>";
        return string.Join(", ", names);
    }

    private string? BuildSystemPrompt(AgentRunRequest request, IReadOnlyList<string> lazyMcpNames)
    {
        var prompt = request.SystemPromptOverride is not null
            ? AppendSkillPrompts(request.SystemPromptOverride, request.ToolNames)
            : null;

        if (lazyMcpNames.Count == 0)
            return prompt;

        var hints = string.Join("\n", lazyMcpNames.Select(name =>
            $"- Call `activate_{name}` to enable {name} tools. They are not available until activated."));
        var lazyHint = $"\n\n## On-demand toolsets\n\nThe following toolsets require activation before use:\n{hints}";

        return (prompt ?? string.Empty) + lazyHint;
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
