using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using Microsoft.Extensions.Options;

namespace SharpClaw.Tools;

public sealed class WorkspaceFileTool(
    IOptions<SharpClawOptions> options,
    SchedulingContextAccessor schedulingContextAccessor) : ITool
{
    public string Name => "workspace_read";
    public string Description => "Read a file from the current agent's workspace folder.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("path", "string", "File path relative to the current agent workspace folder, or an absolute path inside it.", Required: true),
    ];

    public Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var currentAgent = schedulingContextAccessor.Current?.AgentId;
        if (string.IsNullOrWhiteSpace(currentAgent))
            return Task.FromResult<object?>("Error: no active agent workspace context is available.");

        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<object?>("Error: path is required.");

        var resolvedPath = ResolvePath(currentAgent, path);
        if (!File.Exists(resolvedPath))
            return Task.FromResult<object?>($"Error: file not found at '{path}' (resolved to '{resolvedPath}').");

        return Task.FromResult<object?>(File.ReadAllText(resolvedPath));
    }

    private string ResolvePath(string agentName, string path)
    {
        var workspaceRoot = options.Value.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new InvalidOperationException("WorkspacePath is not configured.");

        var agentRoot = Path.GetFullPath(Path.Combine(workspaceRoot, agentName));
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(agentRoot, path));

        if (!fullPath.StartsWith(agentRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path traversal detected: '{path}'");

        return fullPath;
    }
}
