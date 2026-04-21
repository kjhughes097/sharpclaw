using SharpClaw.Core;

namespace SharpClaw.Core;

/// <summary>
/// Aggregates multiple tool providers and dispatches calls by tool name.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IToolProvider> _byToolName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IToolProvider> _providers = [];

    public void Register(IToolProvider provider)
    {
        _providers.Add(provider);
        foreach (var schema in provider.GetSchemas())
        {
            _byToolName[schema.Name] = provider;
        }
    }

    /// <summary>Gets all tool schemas for the specified tool set names.</summary>
    public IReadOnlyList<ToolSchema> GetSchemas(IReadOnlyList<string> toolNames)
    {
        if (toolNames.Count == 0)
            return [];

        return _providers
            .Where(p => toolNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .SelectMany(p => p.GetSchemas())
            .ToList();
    }

    /// <summary>Dispatches a tool call to the appropriate provider.</summary>
    public async Task<ToolCallResult> DispatchAsync(ToolCall call, CancellationToken ct = default)
    {
        if (_byToolName.TryGetValue(call.Name, out var provider))
            return await provider.ExecuteAsync(call, ct);

        return new ToolCallResult($"Unknown tool: {call.Name}", IsError: true);
    }
}
