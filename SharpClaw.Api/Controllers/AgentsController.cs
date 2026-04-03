using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Api.Services;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class AgentsController(
    SessionStore store,
    BackendRegistry backendRegistry,
    BackendModelService backendModelService,
    SessionRuntimeService runtimeService) : ControllerBase
{
    [HttpGet("personas")]
    [ProducesResponseType<List<PersonaDto>>(StatusCodes.Status200OK)]
    public IActionResult GetPersonas()
    {
        var personas = store.ListAgents(includeDisabled: false)
            .Select(ApiMapper.ToPersonaDto)
            .ToList();

        return Ok(personas);
    }

    [HttpGet("agents")]
    [ProducesResponseType<List<AgentDto>>(StatusCodes.Status200OK)]
    public IActionResult GetAgents()
    {
        var sessionCounts = store.GetSessionCountsByAgent();
        var agents = store.ListAgents()
            .Select(agent => ApiMapper.ToAgentDto(agent, sessionCounts.GetValueOrDefault(agent.Slug, 0)))
            .ToList();

        return Ok(agents);
    }

    [HttpGet("backends/{backend}/models")]
    [ProducesResponseType<BackendModelsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemResponse>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetBackendModels(string backend, CancellationToken cancellationToken)
    {
        var response = await backendModelService.GetModelsAsync(backend, cancellationToken);
        return StatusCode(response.StatusCode, response.Payload);
    }

    [HttpPost("agents")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public IActionResult CreateAgent([FromBody] AgentDefinitionRequest req)
    {
        var error = ApiValidator.ValidateAgentRequest(store, backendRegistry.BackendNames, req);
        if (error is not null)
            return BadRequest(new ErrorResponse(error));

        var agentId = ApiMapper.CreateAgentId(req.Name!.Trim());
        if (store.GetAgent(agentId) is not null)
            return Conflict(new ErrorResponse($"Agent '{req.Name!.Trim()}' already exists."));

        var agent = ApiMapper.ToAgentRecord(req, agentId);
        store.CreateAgent(agent);

        return StatusCode(StatusCodes.Status201Created, ApiMapper.ToAgentDto(agent, 0));
    }

    [HttpPut("agents/{slug}")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult UpdateAgent(string slug, [FromBody] AgentDefinitionRequest req)
    {
        var error = ApiValidator.ValidateAgentRequest(store, backendRegistry.BackendNames, req);
        if (error is not null)
            return BadRequest(new ErrorResponse(error));

        var updated = ApiMapper.ToAgentRecord(req, slug);
        return store.UpdateAgent(slug, updated)
            ? Ok(ApiMapper.ToAgentDto(updated, store.CountSessionsForAgent(slug)))
            : NotFound(new ErrorResponse($"Agent '{slug}' not found."));
    }

    [HttpPatch("agents/{slug}/enabled")]
    [ProducesResponseType<EnabledStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult SetAgentEnabled(string slug, [FromBody] AgentEnabledRequest req)
    {
        return store.SetAgentEnabled(slug, req.IsEnabled)
            ? Ok(new EnabledStateResponse(slug, req.IsEnabled))
            : NotFound(new ErrorResponse($"Agent '{slug}' not found."));
    }

    [HttpDelete("agents/{slug}")]
    [ProducesResponseType<AgentDeletedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AgentDeleteConflictResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteAgent(string slug, [FromQuery] bool? purgeSessions)
    {
        var agent = store.GetAgent(slug);
        if (agent is null)
            return NotFound(new ErrorResponse($"Agent '{slug}' not found."));

        var linkedSessionCount = store.CountSessionsForAgent(slug);
        if (linkedSessionCount > 0 && purgeSessions != true)
        {
            return Conflict(new AgentDeleteConflictResponse(
                $"Agent '{slug}' has {linkedSessionCount} linked session(s). Re-run delete with purgeSessions=true to delete those sessions first.",
                linkedSessionCount,
                true));
        }

        if (linkedSessionCount > 0)
        {
            var sessionIds = store.ListSessionIdsForAgent(slug);
            store.PurgeSessionsForAgent(slug);
            foreach (var sessionId in sessionIds)
                await runtimeService.CleanupSessionAsync(sessionId);
        }

        return store.DeleteAgent(slug)
            ? Ok(new AgentDeletedResponse(slug, linkedSessionCount))
            : NotFound(new ErrorResponse($"Agent '{slug}' not found."));
    }
}