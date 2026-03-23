namespace SharpClaw.Core;

/// <summary>
/// Describes an agent's identity and capabilities, loaded from an .agent.md file.
/// </summary>
public sealed record AgentPersona(
    string Name,
    string SystemPrompt,
    IReadOnlyList<string> McpServers);
