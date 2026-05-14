namespace SharpClaw.Models;

public sealed record SkillDefinition(
    string Name,
    string? Description,
    string PromptText,
    string? Command,
    IReadOnlyList<string>? Args
);
