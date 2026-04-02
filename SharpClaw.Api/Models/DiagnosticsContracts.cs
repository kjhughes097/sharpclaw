namespace SharpClaw.Api.Models;

/// <summary>
/// Snapshot of system diagnostics returned by the heartbeat monitor and diagnostics endpoint.
/// </summary>
public sealed record HeartbeatReport(
    int ActiveRunnerCount,
    int ActiveStreamCount,
    IReadOnlyList<StuckSessionInfo> StuckSessions,
    DateTimeOffset CheckedAt) : IApiPayload;

/// <summary>
/// Describes a single session that appears stuck — it holds in-memory resources
/// but has had no recent activity.
/// </summary>
public sealed record StuckSessionInfo(
    string SessionId,
    string AgentSlug,
    DateTimeOffset LastActivityAt,
    double IdleSeconds,
    bool HasRunner,
    bool HasStreams);
