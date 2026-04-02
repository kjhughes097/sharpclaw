using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SharpClaw.Api.Models;
using SharpClaw.Api.Services;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/diagnostics")]
public sealed class DiagnosticsController(
    SessionRuntimeService runtime,
    IOptions<HeartbeatOptions> options) : ControllerBase
{
    /// <summary>
    /// Returns the current heartbeat diagnostics, including any sessions that appear stuck.
    /// </summary>
    [HttpGet("heartbeat")]
    [ProducesResponseType<HeartbeatReport>(StatusCodes.Status200OK)]
    public IActionResult GetHeartbeat()
    {
        var threshold = TimeSpan.FromSeconds(options.Value.StuckThresholdSeconds);
        var report = runtime.GetDiagnostics(threshold);
        return Ok(report);
    }
}
