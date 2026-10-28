namespace SharpClaw.Contracts;

// ----- Provider credits (3.3.1 spec section 3.4) -----

/// <summary>
/// The cached OpenRouter balance. <c>Status</c> is one of <see cref="CreditsStatuses"/>; amounts and
/// <c>AsOf</c> are from the last successful fetch, null if there has never been one.
/// </summary>
public sealed record CreditsResponse(
    string Status,
    double? RemainingUsd,
    double? TotalCreditsUsd,
    double? TotalUsageUsd,
    DateTime? AsOf,
    DateTime? LastAttemptAt,
    double LowThresholdUsd);

public static class CreditsStatuses
{
    /// <summary>The last fetch succeeded and is recent.</summary>
    public const string Ok = "ok";
    /// <summary>No fetch has succeeded recently (and the last one didn't fail, e.g. right after startup).</summary>
    public const string Stale = "stale";
    /// <summary>The last fetch failed; amounts are from the last success, if any.</summary>
    public const string Failing = "failing";
    /// <summary>No <c>OpenRouter:ManagementKey</c> is set.</summary>
    public const string NotConfigured = "not_configured";
    /// <summary>The gateway runs the fake provider.</summary>
    public const string Unsupported = "unsupported";
}
