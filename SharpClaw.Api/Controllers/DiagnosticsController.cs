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
}
