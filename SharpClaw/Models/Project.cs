namespace SharpClaw.Models;

public sealed record Project(
    string Id,
    string Title,
    string? Description,
    DateTimeOffset CreatedAt
);
