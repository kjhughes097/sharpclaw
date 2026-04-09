using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Api.Services;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController(KnowledgeService knowledgeService) : ControllerBase
{
    /// <summary>
    /// Lists all knowledge entries from the workspace knowledge folder.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<KnowledgeListResponse>(StatusCodes.Status200OK)]
    public IActionResult ListKnowledge()
    {
        var entries = knowledgeService.ListKnowledge()
            .Select(e => new KnowledgeEntryDto(
                e.Filename, e.Title, e.SessionId, e.Agent, e.Persona,
                e.ArchivedAt, e.Tags, e.Summary))
            .ToList();

        return Ok(new KnowledgeListResponse(entries));
    }

    /// <summary>
    /// Finds knowledge entries that share any of the provided tags.
    /// </summary>
    [HttpGet("related")]
    [ProducesResponseType<KnowledgeListResponse>(StatusCodes.Status200OK)]
    public IActionResult FindRelated([FromQuery] string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return Ok(new KnowledgeListResponse([]));

        var tagList = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var entries = knowledgeService.FindRelatedKnowledge(tagList)
            .Select(e => new KnowledgeEntryDto(
                e.Filename, e.Title, e.SessionId, e.Agent, e.Persona,
                e.ArchivedAt, e.Tags, e.Summary))
            .ToList();

        return Ok(new KnowledgeListResponse(entries));
    }
}
