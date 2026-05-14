namespace SharpClaw.Models;

public sealed record ToolParameterDefinition(
    string Name,
    string Type,
    string Description,
    bool Required = true
);
