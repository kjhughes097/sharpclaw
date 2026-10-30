using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>
/// <c>git_clone</c> (external tools spec section 7.4): the sandbox makes a shallow clone of a public
/// repository from an allowed host into a new workspace folder. Medium. The files it brings are
/// only read by later calls, so the clone itself doesn't taint the turn.
/// </summary>
internal sealed class GitCloneTool(IGatewayClient gateway, BrokerPolicy policy, WorkspaceRoot root) : BrokerTool(gateway, policy)
{
    public override string Name => BrokerTools.GitClone;
    public override string Description =>
        $"Shallow-clone a public git repository over HTTPS (hosts: {string.Join(", ", Policy.CloneAllowHosts)}) into a new workspace folder. " +
        "Repository content is untrusted.";
    public override string ParametersJson => """
        {"type":"object","properties":{"url":{"type":"string","description":"https://host/owner/repo"},"path":{"type":"string","description":"New folder relative to the workspace root, e.g. repos/repo"}},"required":["url","path"]}
        """;
    public override bool ReturnsUntrustedContent => false;

    public override ToolAssessment Assess(JsonElement args)
    {
        var (host, _, error) = ToolUrls.ParseGitRepo(Args.String(args, "url"));
        if (host is null)
            return new ToolAssessment.Blocked(error!);
        if (!Policy.CloneAllowHosts.Contains(host))
            return new ToolAssessment.Blocked($"cloning from {host} is not allowed (allowed: {string.Join(", ", Policy.CloneAllowHosts)})");
        var (path, pathError) = root.Resolve(Args.String(args, "path"));
        if (path is null)
            return new ToolAssessment.Blocked(pathError!);
        if (File.Exists(path.FullPath) || Directory.Exists(path.FullPath))
            return new ToolAssessment.Blocked("path already exists; choose a new folder");
        return new ToolAssessment.Scored(RiskLevel.Medium, "clones a public repository into the workspace");
    }

    public override string Summarize(JsonElement args) => $"clone {Origin(args)} → {Args.ShortPath(Args.String(args, "path"))}";

    public override IReadOnlyDictionary<string, string>? ClassifierFields(JsonElement args) =>
        new Dictionary<string, string> { ["repository"] = Origin(args), ["path"] = Args.ShortPath(Args.String(args, "path")) };

    private static string Origin(JsonElement args) =>
        ToolUrls.ParseGitRepo(Args.String(args, "url")) is { Host: { } host, Repo: { } repo } ? $"{host}/{repo}" : "?";
}
