using Microsoft.Extensions.AI;

namespace SharpClaw.Models;

public sealed class ToolCallContext(string toolName, AIFunctionArguments arguments)
{
    public string ToolName { get; } = toolName;
    public AIFunctionArguments Arguments { get; } = arguments;

    public string GetString(string key) =>
        Arguments.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
}
