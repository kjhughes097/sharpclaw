namespace SharpClaw.Core;

/// <summary>
/// A single persisted token usage entry, aggregated per provider + agent + day.
/// </summary>
public sealed record TokenUsageRecord(
    string Provider,
    string AgentSlug,
    DateOnly UsageDate,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

/// <summary>
/// Aggregated daily token usage for a single LLM provider.
/// </summary>
public sealed record ProviderDailyUsage(
    string Provider,
    DateOnly UsageDate,
    long TotalTokens,
    long DailyLimit);

/// <summary>
/// Aggregated daily token usage for a single agent.
/// </summary>
public sealed record AgentDailyUsage(
    string AgentSlug,
    DateOnly UsageDate,
    long TotalTokens,
    long? DailyLimit);

/// <summary>
/// Token usage data point for charting: tokens per agent per time bucket.
/// </summary>
public sealed record TokenUsageDataPoint(
    string Bucket,
    string AgentSlug,
    long TotalTokens);
