using SharpClaw.Core;

namespace SharpClaw.Core;

/// <summary>
/// Provides tool schemas and dispatches tool calls.
/// </summary>
public interface IToolProvider
{
    /// <summary>Name of this tool set (e.g. "filesystem", "web-search").</summary>
    string Name { get; }

    /// <summary>Returns the tool schemas this provider exposes to the LLM.</summary>
    IReadOnlyList<ToolSchema> GetSchemas();

    /// <summary>Executes a tool call.</summary>
    Task<ToolCallResult> ExecuteAsync(ToolCall call, CancellationToken ct = default);
}
