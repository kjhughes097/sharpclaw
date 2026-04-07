using SharpClaw.Core;

namespace SharpClaw.Api.Services;

/// <summary>
/// Background service that periodically checks for stuck sessions — those with
/// active in-memory runners or streams but no recent activity — and logs diagnostics.
/// When auto-cleanup is enabled, sessions idle beyond the auto-cleanup threshold are
/// automatically cleaned up by disposing their runners and completing their streams.
/// Settings are loaded from the database on each tick so changes take effect without a restart.
/// </summary>
public sealed class HeartbeatService(
    SessionRuntimeService runtime,
    SessionStore store,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    private static readonly TimeSpan MinTickInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Heartbeat monitor started.");

        // Use a short base tick so config changes are picked up reasonably quickly.
        using var timer = new PeriodicTimer(MinTickInterval);
        var nextRunAt = DateTimeOffset.UtcNow;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var cfg = store.GetHeartbeatSettings();
                if (!cfg.Enabled)
                    continue;

                if (DateTimeOffset.UtcNow < nextRunAt)
                    continue;

                var threshold = TimeSpan.FromSeconds(cfg.StuckThresholdSeconds);
                await RunCheckAsync(threshold, cfg);

                nextRunAt = DateTimeOffset.UtcNow.AddSeconds(cfg.IntervalSeconds);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat check failed.");
            }
        }
    }

    private async Task RunCheckAsync(TimeSpan threshold, HeartbeatSettings cfg)
    {
        var report = runtime.GetDiagnostics(threshold);

        logger.LogInformation(
            "Heartbeat: {ActiveRunners} active runner(s), {ActiveStreams} active stream(s), {StuckCount} stuck session(s).",
            report.ActiveRunnerCount,
            report.ActiveStreamCount,
            report.StuckSessions.Count);

        var autoCleanupThreshold = cfg.AutoCleanupEnabled
            ? TimeSpan.FromSeconds(cfg.AutoCleanupThresholdSeconds)
            : (TimeSpan?)null;

        foreach (var stuck in report.StuckSessions)
        {
            var shouldAutoCleanup = autoCleanupThreshold.HasValue && stuck.IdleSeconds >= autoCleanupThreshold.Value.TotalSeconds;

            if (shouldAutoCleanup)
            {
                logger.LogWarning(
                    "Auto-cleaning stuck session: {SessionId} (agent: {AgentSlug}). " +
                    "Last activity: {LastActivity}, idle for {IdleMinutes:F1} minute(s). " +
                    "Has runner: {HasRunner}, has streams: {HasStreams}.",
                    stuck.SessionId,
                    stuck.AgentSlug,
                    stuck.LastActivityAt,
                    stuck.IdleSeconds / 60.0,
                    stuck.HasRunner,
                    stuck.HasStreams);

                await runtime.CleanupSessionAsync(stuck.SessionId);
            }
            else
            {
                logger.LogWarning(
                    "Stuck session detected: {SessionId} (agent: {AgentSlug}). " +
                    "Last activity: {LastActivity}, idle for {IdleMinutes:F1} minute(s). " +
                    "Has runner: {HasRunner}, has streams: {HasStreams}.",
                    stuck.SessionId,
                    stuck.AgentSlug,
                    stuck.LastActivityAt,
                    stuck.IdleSeconds / 60.0,
                    stuck.HasRunner,
                    stuck.HasStreams);
            }
        }
    }
}
