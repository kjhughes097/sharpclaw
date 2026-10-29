using System.Text;
using System.Text.Json;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>
/// Replaces one exact occurrence of text in an existing file (agent tools spec section 4.3), so a
/// small change doesn't mean re-sending the whole file.
/// </summary>
internal sealed class EditTool(WorkspaceRoot root) : IAgentTool
{
    public string Name => "workspace_edit";
    public string Description =>
        "Replace text in an existing workspace text file. oldText must occur exactly once (include surrounding lines to make it unique); newText may be empty to delete it.";
    public string ParametersJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"oldText":{"type":"string","description":"Exact text to replace; must occur exactly once"},"newText":{"type":"string","description":"Replacement text (empty to delete)"}},"required":["path","oldText","newText"]}
        """;
    public bool ReturnsUntrustedContent => false;

    public ToolAssessment Assess(JsonElement args)
    {
        var (path, error) = root.Resolve(Args.String(args, "path"));
        if (error is not null)
            return new ToolAssessment.Blocked(error);
        if (Args.String(args, "oldText") is not { Length: > 0 } oldText)
            return new ToolAssessment.Blocked("oldText (a non-empty string) is required");
        if (Args.String(args, "newText") is not { } newText)
            return new ToolAssessment.Blocked("newText (a string, possibly empty) is required");
        if (Encoding.UTF8.GetByteCount(oldText) > WorkspaceTools.MaxWriteBytes || Encoding.UTF8.GetByteCount(newText) > WorkspaceTools.MaxWriteBytes)
            return new ToolAssessment.Blocked($"text exceeds {WorkspaceTools.MaxWriteBytes / 1024} KB");
        if (Directory.Exists(path!.FullPath))
            return new ToolAssessment.Blocked("path is a directory");

        return WorkspaceFiles.IsSensitive(path.Relative)
            ? new ToolAssessment.Uncertain("edits a script, executable or dotfile")
            : new ToolAssessment.Scored(RiskLevel.Medium, "changes an existing file");
    }

    public string Summarize(JsonElement args)
    {
        var removed = Encoding.UTF8.GetByteCount(Args.String(args, "oldText") ?? "");
        var added = Encoding.UTF8.GetByteCount(Args.String(args, "newText") ?? "");
        return $"edit {Args.ShortPath(Args.String(args, "path"))} (−{Args.Size(removed)}, +{Args.Size(added)})";
    }

    public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
    {
        var (path, _) = root.Resolve(Args.String(args, "path"));
        if (!File.Exists(path!.FullPath))
            return ToolResult.Error("not_found", $"No file at '{path.Relative}'.");

        var bytes = await File.ReadAllBytesAsync(path.FullPath, ct);
        if (bytes.Length > WorkspaceTools.MaxWriteBytes)
            return ToolResult.Error("too_large", $"The file is larger than {WorkspaceTools.MaxWriteBytes / 1024} KB; it can't be edited.");
        if (WorkspaceFiles.IsBinary(bytes))
            return ToolResult.Error("not_text", "The file is binary; only text files can be edited.");

        var text = Encoding.UTF8.GetString(bytes);
        var oldText = Args.String(args, "oldText")!;
        var newText = Args.String(args, "newText")!;

        var count = CountOccurrences(text, oldText);
        if (count == 0)
            return ToolResult.Error("no_match", "oldText was not found in the file. Read the file and copy the text exactly.");
        if (count > 1)
            return ToolResult.Error("ambiguous_match", $"oldText occurs {count} times; include more surrounding text so it matches exactly once.");

        var index = text.IndexOf(oldText, StringComparison.Ordinal);
        var updated = string.Concat(text.AsSpan(0, index), newText, text.AsSpan(index + oldText.Length));
        var size = Encoding.UTF8.GetByteCount(updated);
        if (size > WorkspaceTools.MaxWriteBytes)
            return ToolResult.Error("too_large", $"The edited file would be larger than {WorkspaceTools.MaxWriteBytes / 1024} KB.");

        await WorkspaceFiles.WriteAtomicAsync(path.FullPath, updated, ct);
        return ToolResult.Success(new { path = path.Relative, bytes = size });
    }

    /// <summary>Counts overlapping occurrences too, so "aa" in "aaa" is ambiguous rather than a guess.</summary>
    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}

/// <summary>
/// Appends text to a file, creating it (and its folders) if missing (agent tools spec section 4.4).
/// Read + write-temp + rename, so a crash never leaves a partial file.
/// </summary>
internal sealed class AppendTool(WorkspaceRoot root) : IAgentTool
{
    public const int MaxAppendBytes = 64 * 1024;

    public string Name => "workspace_append";
    public string Description =>
        $"Append text to the end of a workspace file, creating it if missing (at most {MaxAppendBytes / 1024} KB per call). No separator or newline is added.";
    public string ParametersJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"File path relative to the workspace root"},"content":{"type":"string","description":"Text to append, including any newlines you want"}},"required":["path","content"]}
        """;
    public bool ReturnsUntrustedContent => false;

    public ToolAssessment Assess(JsonElement args)
    {
        var (path, error) = root.Resolve(Args.String(args, "path"));
        if (error is not null)
            return new ToolAssessment.Blocked(error);
        if (Args.String(args, "content") is not { } content)
            return new ToolAssessment.Blocked("content (a string) is required");
        if (Encoding.UTF8.GetByteCount(content) > MaxAppendBytes)
            return new ToolAssessment.Blocked($"content exceeds {MaxAppendBytes / 1024} KB");
        if (Directory.Exists(path!.FullPath))
            return new ToolAssessment.Blocked("path is a directory");

        return WorkspaceFiles.IsSensitive(path.Relative)
            ? new ToolAssessment.Uncertain("appends to a script, executable or dotfile")
            : new ToolAssessment.Scored(RiskLevel.Low, "appends to a file");
    }

    public string Summarize(JsonElement args) =>
        $"append {Args.ShortPath(Args.String(args, "path"))} ({Args.Size(Encoding.UTF8.GetByteCount(Args.String(args, "content") ?? ""))})";

    public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
    {
        var (path, _) = root.Resolve(Args.String(args, "path"));
        var addition = new UTF8Encoding(false).GetBytes(Args.String(args, "content")!);
        var created = !File.Exists(path!.FullPath);

        var existing = created ? Array.Empty<byte>() : await File.ReadAllBytesAsync(path.FullPath, ct);
        var size = existing.Length + addition.Length;
        if (size > WorkspaceTools.MaxWriteBytes)
            return ToolResult.Error("too_large", $"The file would be larger than {WorkspaceTools.MaxWriteBytes / 1024} KB.");

        var combined = new byte[size];
        existing.CopyTo(combined, 0);
        addition.CopyTo(combined, existing.Length);
        await WorkspaceFiles.WriteAtomicAsync(path.FullPath, combined, ct);
        return ToolResult.Success(new { path = path.Relative, bytes = size, created });
    }
}
