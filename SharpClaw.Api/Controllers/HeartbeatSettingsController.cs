using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/settings/heartbeat")]
public sealed class HeartbeatSettingsController(SessionStore store) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<HeartbeatSettingsDto>(StatusCodes.Status200OK)]
    public IActionResult GetHeartbeatSettings()
    {
        var settings = store.GetHeartbeatSettings();
        return Ok(new HeartbeatSettingsDto(settings.Enabled, settings.IntervalSeconds, settings.StuckThresholdSeconds, settings.AutoCleanupEnabled, settings.AutoCleanupThresholdSeconds));
    }

    [HttpPut]
    [ProducesResponseType<HeartbeatSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    public IActionResult UpdateHeartbeatSettings([FromBody] UpdateHeartbeatSettingsRequest req)
    {
        var current = store.GetHeartbeatSettings();

        var enabled = req.Enabled ?? current.Enabled;
        var interval = req.IntervalSeconds ?? current.IntervalSeconds;
        var threshold = req.StuckThresholdSeconds ?? current.StuckThresholdSeconds;
        var autoCleanupEnabled = req.AutoCleanupEnabled ?? current.AutoCleanupEnabled;
        var autoCleanupThreshold = req.AutoCleanupThresholdSeconds ?? current.AutoCleanupThresholdSeconds;

        if (interval <= 0)
            return BadRequest(new ErrorResponse("IntervalSeconds must be a positive number."));
        if (threshold <= 0)
            return BadRequest(new ErrorResponse("StuckThresholdSeconds must be a positive number."));
        if (autoCleanupThreshold <= 0)
            return BadRequest(new ErrorResponse("AutoCleanupThresholdSeconds must be a positive number."));

        var updated = new HeartbeatSettings(enabled, interval, threshold, autoCleanupEnabled, autoCleanupThreshold);
        store.UpsertHeartbeatSettings(updated);

        return Ok(new HeartbeatSettingsDto(updated.Enabled, updated.IntervalSeconds, updated.StuckThresholdSeconds, updated.AutoCleanupEnabled, updated.AutoCleanupThresholdSeconds));
    }
}
