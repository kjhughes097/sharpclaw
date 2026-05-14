using SharpClaw.Models;

namespace SharpClaw.Abstractions;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ToolParameterDefinition> Parameters { get; }
    Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default);
}
