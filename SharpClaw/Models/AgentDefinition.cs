namespace SharpClaw.Models;

public sealed record AgentDefinition(
    string Name,
    string? Description,
    string? Llm,
    string? Model,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> McpNames,
    IReadOnlyList<string> SkillNames,
    IReadOnlyList<string> SubAgentNames,
    string? SystemPrompt
) : Abstractions.IAgent;
