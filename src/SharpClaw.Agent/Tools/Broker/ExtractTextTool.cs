using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>
/// <c>extract_text</c> (external tools spec section 7.3): the sandbox turns a workspace HTML or PDF
/// file into text in another workspace file. Low; a script, executable or dotfile destination is
/// uncertain. It only writes a file (read it with workspace_read), so it doesn't taint the turn.
/// </summary>
internal sealed class ExtractTextTool(IGatewayClient gateway, BrokerPolicy policy, WorkspaceRoot root)
    : BrokerTool(gateway, policy)
{
    private static readonly string[] Extensions = [".html", ".htm", ".pdf"];

    public override string Name => BrokerTools.ExtractText;
    public override string Description =>
        "Extract the text of a workspace .html/.htm (as Markdown) or .pdf (as laid-out text) file into a new workspace file. Read the result with workspace_read.";
    public override string ParametersJson => """
        {"type":"object","properties":{"path":{"type":"string","description":"The .html, .htm or .pdf file, relative to the workspace root"},"to":{"type":"string","description":"New file for the text, e.g. paper.txt or page.md"}},"required":["path","to"]}
        """;
    public override bool ReturnsUntrustedContent => false;

    public override ToolAssessment Assess(JsonElement args)
    {
        var (path, pathError) = root.Resolve(Args.String(args, "path"));
        if (path is null)
            return new ToolAssessment.Blocked($"path: {pathError}");
        var (to, toError) = root.Resolve(Args.String(args, "to"));
        if (to is null)
            return new ToolAssessment.Blocked($"to: {toError}");
        if (!Extensions.Contains(Path.GetExtension(path.Relative).ToLowerInvariant()))
            return new ToolAssessment.Blocked("path must be an .html, .htm or .pdf file");
        if (path.FullPath == to.FullPath)
            return new ToolAssessment.Blocked("to must differ from path");
        if (Directory.Exists(to.FullPath))
            return new ToolAssessment.Blocked("to is a directory");
        return WorkspaceFiles.IsSensitive(to.Relative)
            ? new ToolAssessment.Uncertain("writes a script, executable or dotfile")
            : new ToolAssessment.Scored(RiskLevel.Low, "extracts text into a new file");
    }

    public override string Summarize(JsonElement args) =>
        $"extract {Args.ShortPath(Args.String(args, "path"))} → {Args.ShortPath(Args.String(args, "to"))}";

    public override IReadOnlyDictionary<string, string>? ClassifierFields(JsonElement args) =>
        new Dictionary<string, string> { ["path"] = Args.ShortPath(Args.String(args, "path")), ["to"] = Args.ShortPath(Args.String(args, "to")) };
}
