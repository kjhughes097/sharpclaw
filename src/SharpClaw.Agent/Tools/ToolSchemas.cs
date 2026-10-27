using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools;

/// <summary>The OpenAI <c>tools</c> array offered to the model.</summary>
public static class ToolSchemas
{
    public static List<Dictionary<string, object?>> ToWire(IEnumerable<IAgentTool> tools) =>
        tools.Select(t => new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonDocument.Parse(t.ParametersJson).RootElement.Clone(),
            },
        }).ToList();

    /// <summary>Length of the serialised array, as the gateway counts it for its prompt estimate.</summary>
    public static int JsonLength(IReadOnlyCollection<IAgentTool> tools) =>
        tools.Count == 0 ? 0 : JsonSerializer.Serialize(ToWire(tools), SharpClawJson.Options).Length;
}
