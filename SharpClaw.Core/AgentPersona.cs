namespace SharpClaw.Core;

/// <summary>
/// Describes an agent's identity and capabilities, loaded from an .agent.md file.
/// </summary>
public sealed record AgentPersona(
    string Name,
    string Description,
    string SystemPrompt,
    IReadOnlyList<string> McpServers,
    IReadOnlyDictionary<string, ToolPermission> PermissionPolicy,
    string Backend,
    string Model,
    bool IsEnabled);
