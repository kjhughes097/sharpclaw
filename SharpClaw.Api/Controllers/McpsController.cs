using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/mcps")]
public sealed class McpsController(SessionStore store) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<List<McpDto>>(StatusCodes.Status200OK)]
    public IActionResult GetMcps()
    {
        var agentCounts = store.GetAgentCountsByMcp();
        var mcps = store.ListMcps()
            .Select(mcp => ApiMapper.ToMcpDto(mcp, agentCounts.GetValueOrDefault(mcp.Slug, 0)))
            .ToList();

        return Ok(mcps);
    }

    [HttpPost]
    [ProducesResponseType<McpDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status409Conflict)]
    public IActionResult CreateMcp([FromBody] McpDefinitionRequest req)
    {
        var error = ApiValidator.ValidateMcpRequest(req, creating: true);
        if (error is not null)
            return BadRequest(new ErrorResponse(error));

        var slug = req.Slug!.Trim();
        if (store.GetMcp(slug) is not null)
            return Conflict(new ErrorResponse($"MCP '{slug}' already exists."));

        var mcp = ApiMapper.ToMcpRecord(req);
        store.CreateMcp(mcp);

        return StatusCode(StatusCodes.Status201Created, ApiMapper.ToMcpDto(mcp, 0));
    }

    [HttpPut("{slug}")]
    [ProducesResponseType<McpDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult UpdateMcp(string slug, [FromBody] McpDefinitionRequest req)
    {
        var error = ApiValidator.ValidateMcpRequest(req, creating: false);
        if (error is not null)
            return BadRequest(new ErrorResponse(error));

        if (!string.IsNullOrWhiteSpace(req.Slug) &&
            !string.Equals(req.Slug.Trim(), slug, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new ErrorResponse("Renaming an existing MCP slug is not supported."));
        }

        var updated = ApiMapper.ToMcpRecord(req, slug);
        return store.UpdateMcp(slug, updated)
            ? Ok(ApiMapper.ToMcpDto(updated, store.CountAgentsForMcp(slug)))
                : NotFound(new ErrorResponse($"MCP '{slug}' not found."));
    }

    [HttpPatch("{slug}/enabled")]
    [ProducesResponseType<McpEnabledStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult SetMcpEnabled(string slug, [FromBody] McpEnabledRequest req)
    {
        return store.SetMcpEnabled(slug, req.IsEnabled)
            ? Ok(new McpEnabledStateResponse(slug, req.IsEnabled))
            : NotFound(new ErrorResponse($"MCP '{slug}' not found."));
    }

    [HttpDelete("{slug}")]
    [ProducesResponseType<McpDeletedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<McpDeleteConflictResponse>(StatusCodes.Status409Conflict)]
    public IActionResult DeleteMcp(string slug, [FromQuery] bool? detachAgents)
    {
        var mcp = store.GetMcp(slug);
        if (mcp is null)
            return NotFound(new ErrorResponse($"MCP '{slug}' not found."));

        var linkedAgentCount = store.CountAgentsForMcp(slug);
        if (linkedAgentCount > 0 && detachAgents != true)
        {
            return Conflict(new McpDeleteConflictResponse(
                $"MCP '{slug}' is linked to {linkedAgentCount} agent(s). Re-run delete with detachAgents=true to remove those references first.",
                linkedAgentCount,
                true));
        }

        var detachedAgents = linkedAgentCount > 0 ? store.DetachMcpFromAgents(slug) : 0;

        return store.DeleteMcp(slug)
            ? Ok(new McpDeletedResponse(slug, detachedAgents))
            : NotFound(new ErrorResponse($"MCP '{slug}' not found."));
    }
}