using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using Microsoft.Extensions.Options;

namespace SharpClaw.Tools;

public sealed class WorkspaceWriteTool(
    IOptions<SharpClawOptions> options,
    SchedulingContextAccessor schedulingContextAccessor) : ITool
{
    public string Name => "workspace_write";
    public string Description => "Write or append a file in the current agent's workspace folder.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("path", "string", "File path relative to the current agent workspace folder, or an absolute path inside it.", Required: true),
        new("content", "string", "Content to write.", Required: true),
        new("mode", "string", "Write mode: 'replace' or 'append'.", Required: false),
    ];

    public Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var currentAgent = schedulingContextAccessor.Current?.AgentId;
        if (string.IsNullOrWhiteSpace(currentAgent))
            return Task.FromResult<object?>("Error: no active agent workspace context is available.");

        var path = context.GetString("path");
        var content = context.GetString("content");
        var mode = context.GetString("mode");

        if (string.IsNullOrWhiteSpace(path))
            return Task.FromResult<object?>("Error: path is required.");

        if (content.Length == 0)
            return Task.FromResult<object?>("Error: content is required.");

        var resolvedPath = ResolvePath(currentAgent, path);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);

        if (mode.Equals("append", StringComparison.OrdinalIgnoreCase))
            File.AppendAllText(resolvedPath, content);
        else
            File.WriteAllText(resolvedPath, content);

        return Task.FromResult<object?>($"Wrote {resolvedPath}.");
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
