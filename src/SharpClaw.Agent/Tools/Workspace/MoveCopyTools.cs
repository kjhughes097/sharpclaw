using System.Text.Json;

namespace SharpClaw.Agent.Tools.Workspace;

/// <summary>
/// Shared rules for <see cref="MoveTool"/> and <see cref="CopyTool"/> (agent tools spec section 4.5):
/// files only, both paths through the hardline rules, no silent overwrite, and the most cautious
/// assessment wins (sensitive → uncertain, otherwise the overwrite level when replacing a file).
/// </summary>
internal abstract class FileTransferTool(WorkspaceRoot root) : IAgentTool
{
    protected WorkspaceRoot Root { get; } = root;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public bool ReturnsUntrustedContent => false;

    protected abstract string Verb { get; }
    protected abstract RiskLevel NewFileRisk { get; }
    protected abstract RiskLevel OverwriteRisk { get; }

    public string ParametersJson => """
        {"type":"object","properties":{"from":{"type":"string","description":"Existing file path relative to the workspace root"},"to":{"type":"string","description":"Destination file path relative to the workspace root"},"overwrite":{"type":"boolean","description":"Replace the destination if it exists (default false)"}},"required":["from","to"]}
        """;

    public ToolAssessment Assess(JsonElement args)
    {
        var (from, fromError) = Root.Resolve(Args.String(args, "from"));
        if (fromError is not null)
            return new ToolAssessment.Blocked($"from: {fromError}");
        var (to, toError) = Root.Resolve(Args.String(args, "to"));
        if (toError is not null)
            return new ToolAssessment.Blocked($"to: {toError}");
        if (Directory.Exists(from!.FullPath) || Directory.Exists(to!.FullPath))
            return new ToolAssessment.Blocked("files only");
        if (from.FullPath == to.FullPath)
            return new ToolAssessment.Blocked("from and to are the same file");
        if (!TryOverwrite(args, out var overwrite))
            return new ToolAssessment.Blocked("overwrite must be true or false");
        if (AssessSource(from) is { } blocked)
            return blocked;

        if (WorkspaceFiles.IsSensitive(from.Relative) || WorkspaceFiles.IsSensitive(to.Relative))
            return new ToolAssessment.Uncertain($"{Verb}s a script, executable or dotfile");
        return overwrite && File.Exists(to.FullPath)
            ? new ToolAssessment.Scored(OverwriteRisk, $"{Verb}s over an existing file")
            : new ToolAssessment.Scored(NewFileRisk, $"{Verb}s a file");
    }

    /// <summary>Extra hardline checks on the source; null when it's fine.</summary>
    protected virtual ToolAssessment.Blocked? AssessSource(WorkspaceRoot.Resolved from) => null;

    public string Summarize(JsonElement args)
    {
        var summary = $"{Verb} {Args.ShortPath(Args.String(args, "from"))} → {Args.ShortPath(Args.String(args, "to"))}";
        return TryOverwrite(args, out var overwrite) && overwrite ? $"{summary} (overwrite)" : summary;
    }

    public async Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct)
    {
        var from = Root.Require(Args.String(args, "from"));
        var to = Root.Require(Args.String(args, "to"));
        TryOverwrite(args, out var overwrite);

        if (!File.Exists(from.FullPath))
            return ToolResult.Error("not_found", $"No file at '{from.Relative}'.");
        var overwritten = File.Exists(to.FullPath);
        if (overwritten && !overwrite)
            return ToolResult.Error("exists", $"'{to.Relative}' already exists; pass overwrite: true to replace it.");

        return await TransferAsync(from, to, overwritten, ct);
    }

    protected abstract Task<ToolResult> TransferAsync(
        WorkspaceRoot.Resolved from, WorkspaceRoot.Resolved to, bool overwritten, CancellationToken ct);

    private static bool TryOverwrite(JsonElement args, out bool overwrite)
    {
        overwrite = false;
        if (!args.TryGetProperty("overwrite", out var el) || el.ValueKind == JsonValueKind.Null)
            return true;
        if (el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        overwrite = el.GetBoolean();
        return true;
    }
}

/// <summary>Moves or renames a file. Medium (the old path disappears); overwriting is high.</summary>
internal sealed class MoveTool(WorkspaceRoot root) : FileTransferTool(root)
{
    public override string Name => "workspace_move";
    public override string Description => "Move or rename a file in the workspace (files only). Fails if the destination exists unless overwrite is true.";
    protected override string Verb => "move";
    protected override RiskLevel NewFileRisk => RiskLevel.Medium;
    protected override RiskLevel OverwriteRisk => RiskLevel.High;

    protected override Task<ToolResult> TransferAsync(
        WorkspaceRoot.Resolved from, WorkspaceRoot.Resolved to, bool overwritten, CancellationToken ct)
    {
        // A rename within one filesystem is atomic; without overwrite it also refuses a destination
        // that appeared since the existence check.
        NoFollow.Move(Root.Path, from.Relative, to.Relative, overwrite: overwritten);
        return Task.FromResult(ToolResult.Success(new { from = from.Relative, to = to.Relative, overwritten }));
    }
}

/// <summary>Copies a file (at most 256 KB). Low; overwriting is medium.</summary>
internal sealed class CopyTool(WorkspaceRoot root) : FileTransferTool(root)
{
    public override string Name => "workspace_copy";
    public override string Description =>
        $"Copy a file in the workspace (files only, at most {WorkspaceTools.MaxWriteBytes / 1024} KB). Fails if the destination exists unless overwrite is true.";
    protected override string Verb => "copy";
    protected override RiskLevel NewFileRisk => RiskLevel.Low;
    protected override RiskLevel OverwriteRisk => RiskLevel.Medium;

    protected override ToolAssessment.Blocked? AssessSource(WorkspaceRoot.Resolved from) =>
        File.Exists(from.FullPath) && new FileInfo(from.FullPath).Length > WorkspaceTools.MaxWriteBytes
            ? new ToolAssessment.Blocked($"source exceeds {WorkspaceTools.MaxWriteBytes / 1024} KB")
            : null;

    protected override async Task<ToolResult> TransferAsync(
        WorkspaceRoot.Resolved from, WorkspaceRoot.Resolved to, bool overwritten, CancellationToken ct)
    {
        // Checked again here: the file may have grown since it was assessed.
        if (new FileInfo(from.FullPath).Length > WorkspaceTools.MaxWriteBytes)
            return ToolResult.Error("too_large", $"The file is larger than {WorkspaceTools.MaxWriteBytes / 1024} KB.");

        var bytes = await NoFollow.ReadAllBytesAsync(Root.Path, from.Relative, ct);
        await WorkspaceFiles.WriteAtomicAsync(Root, to, bytes, ct);
        return ToolResult.Success(new { from = from.Relative, to = to.Relative, bytes = bytes.Length, overwritten });
    }
}
