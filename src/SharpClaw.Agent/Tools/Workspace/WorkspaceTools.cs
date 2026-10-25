using System.Text;
using System.Text.Json;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>The four workspace tools (3.2 spec section 6). No tool here executes anything.</summary>
public static class WorkspaceTools
{
    public const int MaxListEntries = 200;
    public const int MaxReadBytes = 64 * 1024;
    public const int MaxWriteBytes = 256 * 1024;

    public static IAgentTool[] All(string workspacePath)
    {
        var root = new WorkspaceRoot(workspacePath);
        return new IAgentTool[] { new ListTool(root), new ReadTool(root), new WriteTool(root), new DeleteTool(root) };
    }

    public static IReadOnlyCollection<string> Names { get; } =
        new[] { "workspace_list", "workspace_read", "workspace_write", "workspace_delete" };

    private const string PathOnlySchema = """
        {"type":"object","properties":{"path":{"type":"string","description":"Path relative to the workspace root"}},"required":["path"]}
        """;

    /// <summary>Lists one directory (not recursive).</summary>
    private sealed class ListTool(WorkspaceRoot root) : IAgentTool
    {
        public string Name => "workspace_list";
        public string Description => $"List files and folders in a workspace directory (at most {MaxListEntries} entries).";
        public string ParametersJson => """
            {"type":"object","properties":{"path":{"type":"string","description":"Directory relative to the workspace root; omit for the root"}}}
            """;
        public bool ReturnsUntrustedContent => false;

        public ToolAssessment Assess(JsonElement args) =>
            root.Resolve(Args.String(args, "path"), allowRoot: true).Error is { } error
                ? new ToolAssessment.Blocked(error)
                : new ToolAssessment.Scored(RiskLevel.Low, "lists a directory");

        public string Summarize(JsonElement args) => $"list {Args.ShortPath(Args.String(args, "path"))}";

        public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            var (path, _) = root.Resolve(Args.String(args, "path"), allowRoot: true);
            if (!Directory.Exists(path!.FullPath))
                return Task.FromResult(ToolResult.Error("not_found", $"No directory at '{path.Relative}'."));

            var all = new DirectoryInfo(path.FullPath).EnumerateFileSystemInfos()
                .OrderBy(i => i.Name, StringComparer.Ordinal)
                .ToList();
            var entries = all.Take(MaxListEntries).Select(i => new
            {
                name = i.Name,
                type = i.LinkTarget is not null ? "link" : i is DirectoryInfo ? "directory" : "file",
                size = i is FileInfo f && i.LinkTarget is null ? f.Length : (long?)null,
            }).ToList();
            return Task.FromResult(ToolResult.Success(new { path = path.Relative, entries, truncated = all.Count > MaxListEntries }));
        }
    }

    /// <summary>Reads a text file. File content is untrusted, so reading taints the rest of the turn.</summary>
    private sealed class ReadTool(WorkspaceRoot root) : IAgentTool
    {
        public string Name => "workspace_read";
        public string Description => $"Read a text file from the workspace (at most {MaxReadBytes / 1024} KB; longer files are truncated).";
        public string ParametersJson => PathOnlySchema;
        public bool ReturnsUntrustedContent => true;

        public ToolAssessment Assess(JsonElement args) =>
            root.Resolve(Args.String(args, "path")).Error is { } error
                ? new ToolAssessment.Blocked(error)
                : new ToolAssessment.Scored(RiskLevel.Low, "reads a file");

        public string Summarize(JsonElement args) => $"read {Args.ShortPath(Args.String(args, "path"))}";

        public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            var (path, _) = root.Resolve(Args.String(args, "path"));
            if (!File.Exists(path!.FullPath))
                return ToolResult.Error("not_found", $"No file at '{path.Relative}'.");

            await using var stream = File.OpenRead(path.FullPath);
            var buffer = new byte[Math.Min(stream.Length, MaxReadBytes)];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            var content = Encoding.UTF8.GetString(buffer, 0, read);
            return ToolResult.Success(new { path = path.Relative, content, bytes = stream.Length, truncated = stream.Length > MaxReadBytes });
        }
    }

    /// <summary>Creates or overwrites a text file, atomically (temp file then rename).</summary>
    private sealed class WriteTool(WorkspaceRoot root) : IAgentTool
    {
        // Script and executable types (and dotfiles such as .bashrc): nothing here runs them, but
        // something else could later, so the rules don't score them confidently.
        private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".sh", ".bash", ".ps1", ".bat", ".cmd", ".exe", ".dll", ".so", ".py", ".js",
        };

        public string Name => "workspace_write";
        public string Description => $"Create or overwrite a text file in the workspace (at most {MaxWriteBytes / 1024} KB).";
        public string ParametersJson => """
            {"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"content":{"type":"string","description":"Full new file content"}},"required":["path","content"]}
            """;
        public bool ReturnsUntrustedContent => false;

        public ToolAssessment Assess(JsonElement args)
        {
            var (path, error) = root.Resolve(Args.String(args, "path"));
            if (error is not null)
                return new ToolAssessment.Blocked(error);
            if (Args.String(args, "content") is not { } content)
                return new ToolAssessment.Blocked("content (a string) is required");
            if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes)
                return new ToolAssessment.Blocked($"content exceeds {MaxWriteBytes / 1024} KB");
            if (Directory.Exists(path!.FullPath))
                return new ToolAssessment.Blocked("path is a directory");

            if (IsSensitive(path.Relative))
                return new ToolAssessment.Uncertain("writes a script, executable or dotfile");
            return File.Exists(path.FullPath)
                ? new ToolAssessment.Scored(RiskLevel.Medium, "overwrites an existing file")
                : new ToolAssessment.Scored(RiskLevel.Low, "creates a new file");
        }

        public string Summarize(JsonElement args)
        {
            var (path, _) = root.Resolve(Args.String(args, "path"));
            var bytes = Encoding.UTF8.GetByteCount(Args.String(args, "content") ?? "");
            var kind = path is not null && File.Exists(path.FullPath) ? "overwrite" : "new";
            return $"write {Args.ShortPath(Args.String(args, "path"))} ({Args.Size(bytes)}, {kind})";
        }

        public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            var (path, _) = root.Resolve(Args.String(args, "path"));
            var content = Args.String(args, "content")!;
            var created = !File.Exists(path!.FullPath);

            var dir = System.IO.Path.GetDirectoryName(path.FullPath)!;
            Directory.CreateDirectory(dir);
            var temp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(path.FullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), ct);
                File.Move(temp, path.FullPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }

            return ToolResult.Success(new { path = path.Relative, bytes = Encoding.UTF8.GetByteCount(content), created });
        }

        private static bool IsSensitive(string relative) =>
            relative.Split('/').Any(segment => segment.StartsWith('.')) ||
            SensitiveExtensions.Contains(System.IO.Path.GetExtension(relative));
    }

    /// <summary>Deletes a single file (never a directory). Always high risk.</summary>
    private sealed class DeleteTool(WorkspaceRoot root) : IAgentTool
    {
        public string Name => "workspace_delete";
        public string Description => "Delete a single file from the workspace.";
        public string ParametersJson => PathOnlySchema;
        public bool ReturnsUntrustedContent => false;

        public ToolAssessment Assess(JsonElement args) =>
            root.Resolve(Args.String(args, "path")).Error is { } error
                ? new ToolAssessment.Blocked(error)
                : new ToolAssessment.Scored(RiskLevel.High, "deletes a file");

        public string Summarize(JsonElement args) => $"delete {Args.ShortPath(Args.String(args, "path"))}";

        public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            var (path, _) = root.Resolve(Args.String(args, "path"));
            if (Directory.Exists(path!.FullPath))
                return Task.FromResult(ToolResult.Error("not_a_file", "Only files can be deleted, not directories."));
            if (!File.Exists(path.FullPath))
                return Task.FromResult(ToolResult.Error("not_found", $"No file at '{path.Relative}'."));

            File.Delete(path.FullPath);
            return Task.FromResult(ToolResult.Success(new { path = path.Relative, deleted = true }));
        }
    }
}
