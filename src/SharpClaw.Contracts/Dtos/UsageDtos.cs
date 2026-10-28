namespace SharpClaw.Contracts;

// ----- Usage (3.3.1 spec section 3.1). Aggregated from llm_calls; buckets are UTC and zero-filled. -----

/// <summary><c>Range</c> is <c>today</c>, <c>7d</c> or <c>30d</c>; <c>Buckets</c> are ascending from <c>From</c>.</summary>
public sealed record UsageResponse(
    string Range,
    DateTime From,
    DateTime To,
    int BucketSeconds,
    IReadOnlyList<UsageBucketDto> Buckets,
    IReadOnlyList<UsageByModelDto> ByModel,
    IReadOnlyList<UsageByAgentDto> ByAgent,
    UsageTotalsDto Totals);

public sealed record UsageBucketDto(DateTime Start, int Calls, long PromptTokens, long CompletionTokens, double CostUsd);

/// <summary>Sorted by cost, highest first.</summary>
public sealed record UsageByModelDto(string Model, int Calls, long PromptTokens, long CompletionTokens, double CostUsd);

/// <summary>Every agent (or only the filtered one), zero usage included, sorted by name.</summary>
public sealed record UsageByAgentDto(
    Guid AgentId, string AgentName, int Calls, long PromptTokens, long CompletionTokens, double CostUsd);

/// <summary><c>UnknownUsageCalls</c>: calls the provider reported no token counts for (they add 0 tokens).</summary>
public sealed record UsageTotalsDto(int Calls, long PromptTokens, long CompletionTokens, double CostUsd, int UnknownUsageCalls);

public static class UsageRanges
{
    public const string Today = "today";
    public const string Week = "7d";
    public const string Month = "30d";
    public static readonly string[] All = [Today, Week, Month];
}
