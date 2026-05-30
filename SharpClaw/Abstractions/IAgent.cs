using SharpClaw.Models;

namespace SharpClaw.Abstractions;

public interface IAgent
{
    string Name { get; }
    string? Description { get; }
    string? Llm { get; }
    string? Model { get; }
    string? SystemPrompt { get; }
    IReadOnlyList<string> ToolNames { get; }
    IReadOnlyList<string> McpNames { get; }
    IReadOnlyList<string> LazyMcpNames { get; }
    IReadOnlyList<string> SkillNames { get; }
    IReadOnlyList<string> SubAgentNames { get; }
}
