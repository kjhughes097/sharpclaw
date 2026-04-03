using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/settings/app")]
public sealed class AppSettingsController(SessionStore store) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AppSettingsDto>(StatusCodes.Status200OK)]
    public IActionResult GetAppSettings()
    {
        return Ok(new AppSettingsDto(store.GetWorkspacePath()));
    }

    [HttpPut]
    [ProducesResponseType<AppSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    public IActionResult UpdateAppSettings([FromBody] UpdateAppSettingsRequest req)
    {
        var error = ApiValidator.ValidateAppSettingsRequest(req);
        if (error is not null)
            return BadRequest(new ErrorResponse(error));

        var workspacePath = req.ClearWorkspacePath == true
            ? SessionStore.DefaultWorkspacePath()
            : req.WorkspacePath?.Trim() ?? store.GetWorkspacePath();

        store.UpsertWorkspacePath(workspacePath);
        return Ok(new AppSettingsDto(store.GetWorkspacePath()));
    }
}
