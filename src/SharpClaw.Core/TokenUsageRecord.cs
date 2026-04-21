namespace SharpClaw.Core;

/// <summary>
/// Records token usage for a single turn (one user message → one assistant response).
/// </summary>
public sealed record TokenUsageRecord(
    string Provider,
    int InputTokens,
    int OutputTokens,
    string? AgentSlug,
    DateTimeOffset Timestamp);
