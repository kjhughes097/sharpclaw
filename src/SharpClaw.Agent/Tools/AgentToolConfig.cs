using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpClaw.Agent.Tools;

/// <summary>A configuration problem that must stop the agent (it must never silently grant tools).</summary>
public sealed class AgentToolConfigException(string message) : Exception(message);

/// <summary>
/// Tool policy read once at startup from a read-only file (3.2 spec section 4). Never read from the
/// workspace and never changed at runtime.
/// </summary>
public sealed record AgentToolConfig(
    RiskLevel AllowedRiskLevel,
    bool SmartMode,
    int MaxToolIterations,
    IReadOnlyList<string> Tools,
    bool TaintWorkspaceReads = false)
{
    /// <summary>Used when the config file is missing: chat only, no tools.</summary>
    public static AgentToolConfig NoTools { get; } = new(RiskLevel.Low, false, 10, Array.Empty<string>());

    /// <summary>
    /// Loads the config. Returns <c>null</c> when the file doesn't exist (caller runs with no tools).
    /// Throws <see cref="AgentToolConfigException"/> for anything invalid.
    /// </summary>
    public static AgentToolConfig? Load(string path, IReadOnlyCollection<string> knownTools)
    {
        if (!File.Exists(path))
            return null;

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new AgentToolConfigException("agent config must be a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new AgentToolConfigException("agent config is not valid JSON.");
        }

        var allowedText = (string?)root["allowedRiskLevel"] ?? "low";
        if (!RiskLevelExtensions.TryParse(allowedText, out var allowed))
            throw new AgentToolConfigException($"allowedRiskLevel '{allowedText}' is not one of low, medium, high, critical.");

        var smartMode = root["smartMode"] is { } sm && sm.GetValueKind() == JsonValueKind.True;

        // The agent's own workspace is not untrusted by default (3.6 spec section 3).
        var taintWorkspaceReads = root["taintWorkspaceReads"] is { } tw && tw.GetValueKind() == JsonValueKind.True;

        var maxIterations = 10;
        if (root["maxToolIterations"] is { } mi)
        {
            if (mi.GetValueKind() != JsonValueKind.Number || !int.TryParse(mi.ToJsonString(), out maxIterations) ||
                maxIterations < 1 || maxIterations > 100)
                throw new AgentToolConfigException("maxToolIterations must be an integer between 1 and 100.");
        }

        var tools = new List<string>();
        if (root["tools"] is { } toolsNode)
        {
            if (toolsNode is not JsonArray array)
                throw new AgentToolConfigException("tools must be an array of tool names.");
            foreach (var item in array)
            {
                var name = item?.GetValueKind() == JsonValueKind.String ? (string?)item : null;
                if (name is null || !knownTools.Contains(name))
                    throw new AgentToolConfigException($"unknown tool '{item?.ToJsonString()}'.");
                if (!tools.Contains(name))
                    tools.Add(name);
            }
        }

        return new AgentToolConfig(allowed, smartMode, maxIterations, tools, taintWorkspaceReads);
    }
}

/// <summary>The tools this agent may offer to the model: the configured subset of the available ones.</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolRegistry(IEnumerable<IAgentTool> available, IReadOnlyList<string> enabled)
    {
        var byName = available.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _tools = enabled.ToDictionary(n => n, n => byName[n], StringComparer.Ordinal);
    }

    public IReadOnlyCollection<IAgentTool> Tools => _tools.Values;

    public bool TryGet(string name, out IAgentTool tool) => _tools.TryGetValue(name, out tool!);
}
