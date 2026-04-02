using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/integrations/telegram")]
public sealed class TelegramSettingsController(SessionStore store) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TelegramSettingsDto>(StatusCodes.Status200OK)]
    public IActionResult GetTelegramSettings()
    {
        var settings = store.GetTelegramIntegrationSettings();
        return Ok(ApiMapper.ToTelegramSettingsDto(settings));
    }

    [HttpGet("runtime")]
    [ProducesResponseType<TelegramRuntimeSettingsDto>(StatusCodes.Status200OK)]
    public IActionResult GetTelegramRuntimeSettings()
    {
        var settings = store.GetTelegramIntegrationSettings();
        return Ok(new TelegramRuntimeSettingsDto(
            IsEnabled: settings.IsEnabled,
            BotToken: settings.BotToken,
            AllowedUserIds: settings.AllowedUserIds,
            AllowedUsernames: settings.AllowedUsernames));
    }

    [HttpPut]
    [ProducesResponseType<TelegramSettingsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    public IActionResult UpdateTelegramSettings([FromBody] UpdateTelegramSettingsRequest req)
    {
        var error = ApiValidator.ValidateTelegramSettingsRequest(req);
        if (error is not null)
            return BadRequest(new ErrorResponse(error));

        var existing = store.GetTelegramIntegrationSettings();

        var requestedToken = string.IsNullOrWhiteSpace(req.BotToken) ? null : req.BotToken.Trim();
        var token = req.ClearBotToken == true
            ? null
            : requestedToken ?? existing.BotToken;

        var allowedUserIds = req.AllowedUserIds?.Distinct().Order().ToList()
            ?? existing.AllowedUserIds.Order().ToList();

        var allowedUsernames = req.AllowedUsernames is null
            ? existing.AllowedUsernames.ToList()
            : ApiMapper.NormalizeTelegramUsernames(req.AllowedUsernames);

        var updated = new TelegramIntegrationSettings(
            IsEnabled: req.IsEnabled,
            BotToken: token,
            AllowedUserIds: allowedUserIds,
            AllowedUsernames: allowedUsernames);

        store.UpsertTelegramIntegrationSettings(updated);

        return Ok(ApiMapper.ToTelegramSettingsDto(updated));
    }
}
