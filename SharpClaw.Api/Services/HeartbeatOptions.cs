namespace SharpClaw.Api.Services;

/// <summary>
/// Configuration for the heartbeat monitor that periodically checks for stuck sessions.
/// </summary>
public sealed class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    /// <summary>
    /// How often the heartbeat check runs, in seconds. Default: 300 (5 minutes).
    /// </summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>
    /// A session with an active runner or stream whose last activity exceeds this
    /// threshold (in seconds) is considered stuck. Default: 600 (10 minutes).
    /// </summary>
    public int StuckThresholdSeconds { get; set; } = 600;

    /// <summary>
    /// Whether the heartbeat monitor is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
