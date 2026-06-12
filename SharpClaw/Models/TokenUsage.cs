namespace SharpClaw.Models;

public sealed record TokenUsage
{
    public long Id { get; init; }
    public DateTimeOffset TimestampUtc { get; init; }
    public required string AgentName { get; init; }
    public required string Provider { get; init; }
    public string? Model { get; init; }
    public string? SessionId { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? TotalTokens => (InputTokens ?? 0) + (OutputTokens ?? 0);
    public double? DurationMs { get; init; }
    public int ToolCount { get; init; }
    public int McpCount { get; init; }
    public string? Skills { get; init; }
    public bool Success { get; init; }
}
