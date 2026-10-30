using System.Text;
using System.Text.Json;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>
/// The workspace tools (3.2 spec section 6, agent tools spec section 4). No tool here executes anything.
/// </summary>
public static class WorkspaceTools
{
    public const int MaxListEntries = 200;
    public const int MaxReadBytes = 64 * 1024;
    public const int MaxWriteBytes = 256 * 1024;

    public static IAgentTool[] All(string workspacePath)
    {
        var root = new WorkspaceRoot(workspacePath);
        return new IAgentTool[]
        {
            new ListTool(root), new ReadTool(root), new WriteTool(root), new DeleteTool(root),
            new SearchTool(root), new FindTool(root), new EditTool(root), new AppendTool(root),
            new MoveTool(root), new CopyTool(root),
        };
    }

    public static IReadOnlyCollection<string> Names { get; } = new[]
    {
        "workspace_list", "workspace_read", "workspace_write", "workspace_delete",
        "workspace_search", "workspace_find", "workspace_edit", "workspace_append",
        "workspace_move", "workspace_copy",
    };

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
            var path = root.Require(Args.String(args, "path"), allowRoot: true);
            if (!Directory.Exists(path.FullPath))
                return Task.FromResult(ToolResult.Error("not_found", $"No directory at '{path.Relative}'."));

            // Enumerated through a no-follow directory handle, so a swapped-in link can't redirect it.
            var (handle, dirPath) = NoFollow.OpenDirectory(root.Path, path.Relative);
            List<FileSystemInfo> all;
            using (handle)
                all = new DirectoryInfo(dirPath).EnumerateFileSystemInfos()
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

    /// <summary>
    /// Reads a text file, a page of at most <see cref="MaxReadBytes"/> at a time (agent tools spec
    /// section 4.6). File content is untrusted, so reading taints the rest of the turn.
    /// </summary>
    private sealed class ReadTool(WorkspaceRoot root) : IAgentTool
    {
        public string Name => "workspace_read";
        public string Description =>
            $"Read a text file from the workspace, at most {MaxReadBytes / 1024} KB per call. " +
            "When the result is truncated, call again with offset = nextOffset to read the next part.";
        public string ParametersJson => """
            {"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"offset":{"type":"integer","minimum":0,"description":"Byte offset to start reading from (default 0); use nextOffset from a truncated result"}},"required":["path"]}
            """;
        public bool ReturnsUntrustedContent => true;

        public ToolAssessment Assess(JsonElement args)
        {
            if (root.Resolve(Args.String(args, "path")).Error is { } error)
                return new ToolAssessment.Blocked(error);
            if (!TryOffset(args, out _))
                return new ToolAssessment.Blocked("offset must be a non-negative integer");
            return new ToolAssessment.Scored(RiskLevel.Low, "reads a file");
        }

        public string Summarize(JsonElement args) =>
            TryOffset(args, out var offset) && offset > 0
                ? $"read {Args.ShortPath(Args.String(args, "path"))} (from byte {offset})"
                : $"read {Args.ShortPath(Args.String(args, "path"))}";

        public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
        {
            var path = root.Require(Args.String(args, "path"));
            if (!File.Exists(path.FullPath))
                return ToolResult.Error("not_found", $"No file at '{path.Relative}'.");
            TryOffset(args, out var requested);

            await using var stream = NoFollow.OpenRead(root.Path, path.Relative);
            var length = stream.Length;
            var start = Math.Min(requested, length);
            stream.Seek(start, SeekOrigin.Begin);

            // Up to 3 extra bytes on each side let the page snap to UTF-8 character boundaries.
            var buffer = new byte[Math.Min(length - start, MaxReadBytes + 6)];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);

            // Start at the first character boundary at or after the offset.
            var skip = 0;
            while (skip < 3 && skip < read && IsContinuation(buffer[skip]))
                skip++;

            // End before a character that wouldn't fit; the next page starts with it.
            var end = Math.Min(read, skip + MaxReadBytes);
            if (end < read)
            {
                var limit = end - 3;
                while (end > skip && end > limit && IsContinuation(buffer[end]))
                    end--;
            }

            var offset = start + skip;
            var next = start + end;
            var content = Encoding.UTF8.GetString(buffer, skip, end - skip);
            return next < length
                ? ToolResult.Success(new { path = path.Relative, content, bytes = length, offset, truncated = true, nextOffset = next })
                : ToolResult.Success(new { path = path.Relative, content, bytes = length, offset, truncated = false });
        }

        private static bool IsContinuation(byte b) => (b & 0b1100_0000) == 0b1000_0000;

        private static bool TryOffset(JsonElement args, out long offset)
        {
            offset = 0;
            if (!args.TryGetProperty("offset", out var el) || el.ValueKind == JsonValueKind.Null)
                return true;
            return el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out offset) && offset >= 0;
        }
    }

    /// <summary>Creates or overwrites a text file, atomically (temp file then rename).</summary>
    private sealed class WriteTool(WorkspaceRoot root) : IAgentTool
    {
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

            if (WorkspaceFiles.IsSensitive(path.Relative))
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
            var path = root.Require(Args.String(args, "path"));
            var content = Args.String(args, "content")!;
            var created = !File.Exists(path.FullPath);

            await WorkspaceFiles.WriteAtomicAsync(root, path, content, ct);
            return ToolResult.Success(new { path = path.Relative, bytes = Encoding.UTF8.GetByteCount(content), created });
        }
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
            var path = root.Require(Args.String(args, "path"));
            if (Directory.Exists(path.FullPath))
                return Task.FromResult(ToolResult.Error("not_a_file", "Only files can be deleted, not directories."));
            if (!File.Exists(path.FullPath))
                return Task.FromResult(ToolResult.Error("not_found", $"No file at '{path.Relative}'."));

            NoFollow.Delete(root.Path, path.Relative);
            return Task.FromResult(ToolResult.Success(new { path = path.Relative, deleted = true }));
        }
    }
}
