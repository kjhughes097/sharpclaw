using Microsoft.Extensions.Options;

namespace SharpClaw.Api.Services;

/// <summary>
/// Background service that periodically checks for stuck sessions — those with
/// active in-memory runners or streams but no recent activity — and logs diagnostics.
/// </summary>
public sealed class HeartbeatService(
    SessionRuntimeService runtime,
    IOptions<HeartbeatOptions> options,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        if (!cfg.Enabled)
        {
            logger.LogInformation("Heartbeat monitor is disabled.");
            return;
        }

        if (cfg.IntervalSeconds <= 0)
        {
            logger.LogWarning("Heartbeat IntervalSeconds must be positive. Heartbeat monitor will not start.");
            return;
        }

        if (cfg.StuckThresholdSeconds <= 0)
        {
            logger.LogWarning("Heartbeat StuckThresholdSeconds must be positive. Heartbeat monitor will not start.");
            return;
        }

        var interval = TimeSpan.FromSeconds(cfg.IntervalSeconds);
        var threshold = TimeSpan.FromSeconds(cfg.StuckThresholdSeconds);

        logger.LogInformation(
            "Heartbeat monitor started. Interval: {Interval}s, stuck threshold: {Threshold}s.",
            cfg.IntervalSeconds,
            cfg.StuckThresholdSeconds);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                RunCheck(threshold);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat check failed.");
            }
        }
    }

    private void RunCheck(TimeSpan threshold)
    {
        var report = runtime.GetDiagnostics(threshold);

        logger.LogInformation(
            "Heartbeat: {ActiveRunners} active runner(s), {ActiveStreams} active stream(s), {StuckCount} stuck session(s).",
            report.ActiveRunnerCount,
            report.ActiveStreamCount,
            report.StuckSessions.Count);

        foreach (var stuck in report.StuckSessions)
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
