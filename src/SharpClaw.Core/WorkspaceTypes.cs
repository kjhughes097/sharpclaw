namespace SharpClaw.Core;

/// <summary>
/// Status of a workspace project.
/// </summary>
public enum WorkspaceProjectStatus
{
    Live,
    Stale,
    Archived,
}

/// <summary>
/// Metadata for a project within a workspace category (coding, running, finance, other).
/// </summary>
public sealed record WorkspaceProject(
    string Slug,
    string Name,
    string Category,
    WorkspaceProjectStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastModifiedAt,
    int TotalTokens,
    string? Icon,
    string? Image,
    IReadOnlyList<string> Collaborators,
    string? Readme);
