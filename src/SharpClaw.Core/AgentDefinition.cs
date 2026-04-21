namespace SharpClaw.Core;

/// <summary>
/// Parsed representation of an *.agent.md file.
/// </summary>
public sealed record AgentDefinition(
    string Slug,
    string Name,
    string Description,
    string Service,
    string Model,
    IReadOnlyList<string> Tools,
    string SystemPrompt);
