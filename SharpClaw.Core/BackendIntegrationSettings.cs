namespace SharpClaw.Core;

public sealed record BackendIntegrationSettings(
    string Backend,
    bool IsEnabled,
    string? ApiKey,
    DateTimeOffset? UpdatedAt,
    long DailyTokenLimit = 1_000_000);
