using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Api.Services;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(
    SessionRuntimeService runtime,
    SessionStore store) : ControllerBase
{
    /// <summary>
    /// Returns the current heartbeat diagnostics, including any sessions that appear stuck.
    /// </summary>
    [HttpGet("heartbeat")]
    [ProducesResponseType<HeartbeatReport>(StatusCodes.Status200OK)]
    public IActionResult GetHeartbeat()
    {
        var cfg = store.GetHeartbeatSettings();
        var threshold = TimeSpan.FromSeconds(cfg.StuckThresholdSeconds);
        var report = runtime.GetDiagnostics(threshold);
        return Ok(report);
    }

    /// <summary>
    /// Cleans up a single stuck session by disposing its runner and completing its streams.
    /// The session's persisted data is preserved — only in-memory resources are released.
    /// </summary>
    [HttpPost("heartbeat/cleanup/{sessionId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CleanupSession(string sessionId)
    {
        await runtime.CleanupSessionAsync(sessionId);
        return Ok(new { sessionId, cleanedUp = true });
    }

    /// <summary>
    /// Cleans up all sessions that are currently detected as stuck.
    /// </summary>
    [HttpPost("heartbeat/cleanup")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CleanupAllStuck()
    {
        var cfg = store.GetHeartbeatSettings();
        var threshold = TimeSpan.FromSeconds(cfg.StuckThresholdSeconds);
        var report = runtime.GetDiagnostics(threshold);

        var cleaned = new List<string>();
        foreach (var stuck in report.StuckSessions)
        {
            await runtime.CleanupSessionAsync(stuck.SessionId);
            cleaned.Add(stuck.SessionId);
        }

        return Ok(new { cleanedUp = cleaned.Count, sessionIds = cleaned });
    }
}
