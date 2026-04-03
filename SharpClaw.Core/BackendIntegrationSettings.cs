namespace SharpClaw.Core;

public sealed record BackendIntegrationSettings(
    string Backend,
    bool IsEnabled,
    string? ApiKey,
    DateTimeOffset? UpdatedAt);
