using Microsoft.AspNetCore.Mvc;
using SharpClaw.Api.Models;
using SharpClaw.Core;

namespace SharpClaw.Api.Controllers;

[ApiController]
[Route("api/workspace")]
public sealed class WorkspaceController(SessionStore store) : ControllerBase
{
    [HttpGet("browse")]
    [ProducesResponseType<WorkspaceBrowseResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorResponse>(StatusCodes.Status404NotFound)]
    public IActionResult Browse([FromQuery] string? path)
    {
        var workspaceRoot = store.GetWorkspacePath();

        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return BadRequest(new ErrorResponse("Workspace path is not configured."));

        var rootFull = Path.GetFullPath(workspaceRoot);

        string targetFull;
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path == ".")
        {
            targetFull = rootFull;
        }
        else
        {
            // Combine and resolve to prevent path traversal
            var combined = Path.Combine(rootFull, path);
            targetFull = Path.GetFullPath(combined);
        }

        // Ensure the resolved path is within the workspace root
        if (!targetFull.StartsWith(rootFull, StringComparison.Ordinal)
            || (targetFull.Length > rootFull.Length && targetFull[rootFull.Length] != Path.DirectorySeparatorChar && rootFull[^1] != Path.DirectorySeparatorChar))
        {
            return BadRequest(new ErrorResponse("Path is outside the workspace directory."));
        }

        if (!Directory.Exists(targetFull))
            return NotFound(new ErrorResponse("Directory not found."));

        var relativePath = targetFull.Length > rootFull.Length
            ? targetFull[rootFull.Length..].TrimStart(Path.DirectorySeparatorChar)
            : "";

        var entries = new List<WorkspaceEntryDto>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(targetFull).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var info = new DirectoryInfo(dir);
                entries.Add(new WorkspaceEntryDto(info.Name, "directory", null, info.LastWriteTimeUtc));
            }

            foreach (var file in Directory.EnumerateFiles(targetFull).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                entries.Add(new WorkspaceEntryDto(info.Name, "file", info.Length, info.LastWriteTimeUtc));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(new ErrorResponse("Access denied to the requested directory."));
        }

        return Ok(new WorkspaceBrowseResponse(relativePath, entries));
    }
}
