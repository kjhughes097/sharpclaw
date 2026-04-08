using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Api.Services;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class SessionsController(SessionStore store, SessionRuntimeService runtimeService) : ControllerBase
{
    [HttpGet("sessions")]
    [ProducesResponseType<List<SessionDto>>(StatusCodes.Status200OK)]
    public IActionResult GetSessions()
    {
        var agentsBySlug = store.ListAgents()
            .ToDictionary(agent => agent.Slug, StringComparer.OrdinalIgnoreCase);

        var sessions = store.ListSessions()
            .Select(session => ApiMapper.ToSessionDto(store, session, agentsBySlug))
            .ToList();

        return Ok(sessions);
    }

    [HttpGet("sessions/{id}")]
    [ProducesResponseType<SessionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult GetSession(string id)
    {
        var session = store.ListSessions().FirstOrDefault(item => item.SessionId == id);
        if (session is null)
            return NotFound(new ErrorResponse($"Session '{id}' not found."));

        var agentsBySlug = store.ListAgents()
            .ToDictionary(agent => agent.Slug, StringComparer.OrdinalIgnoreCase);

        return Ok(ApiMapper.ToSessionDto(store, session, agentsBySlug));
    }

    [HttpDelete("sessions/{id}")]
    [ProducesResponseType<SessionDeletedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSession(string id)
    {
        var response = await runtimeService.DeleteSessionAsync(id);
        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpPost("sessions")]
    [ProducesResponseType<SessionCreatedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req, CancellationToken cancellationToken)
    {
        var response = await runtimeService.CreateSessionAsync(req.AgentId, cancellationToken);
        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpPost("sessions/{id}/messages")]
    [ProducesResponseType<MessageQueuedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendMessage(string id, [FromBody] SendMessageRequest req, CancellationToken cancellationToken)
    {
        var response = await runtimeService.QueueMessageAsync(id, req.Message, cancellationToken);
        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpGet("sessions/{id}/messages/{msgId}/stream")]
    public Task StreamMessage(string id, string msgId)
        => runtimeService.StreamEventsAsync(id, msgId, HttpContext);

    [HttpPost("sessions/{id}/archive")]
    [ProducesResponseType<SessionArchivedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ArchiveSession(string id)
    {
        var response = await runtimeService.ArchiveSessionAsync(id);
        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpPost("sessions/{id}/permissions/{requestId}")]
    [ProducesResponseType<PermissionResolvedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status500InternalServerError)]
    public IActionResult ResolvePermission(string id, string requestId, [FromBody] PermissionDecision decision)
    {
        var response = runtimeService.ResolvePermission(id, requestId, decision.Allow);
        return StatusCode(response.StatusCode, response.Payload);
    }
}