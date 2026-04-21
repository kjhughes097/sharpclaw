namespace SharpClaw.Core;

/// <summary>
/// Metadata for a project.
/// </summary>
public sealed record ProjectInfo(
    string Slug,
    string Name,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ChatInfo> Chats,
    int TotalInputTokens = 0,
    int TotalOutputTokens = 0);

/// <summary>
/// Metadata for a chat within a project.
/// </summary>
public sealed record ChatInfo(
    string Slug,
    string Title,
    string? LastAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int TotalInputTokens = 0,
    int TotalOutputTokens = 0);
