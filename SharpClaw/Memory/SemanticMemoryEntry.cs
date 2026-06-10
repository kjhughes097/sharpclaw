namespace SharpClaw.Memory;

public enum MemoryType
{
    Fact,
    Decision,
    Preference,
    Learning
}

public sealed record SemanticMemoryEntry
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required string AgentName { get; init; }
    public MemoryType Type { get; init; } = MemoryType.Fact;
    public float TrustScore { get; init; } = 1.0f;
    public int AccessCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RecalledMemory(
    string Id,
    string Content,
    MemoryType Type,
    float Score,
    float TrustScore);
