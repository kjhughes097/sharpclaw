using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>
/// <c>sandbox_run</c> (external tools spec section 7.5): runs a shell command in the agent's
/// sandbox, never in the agent. Always high risk, and the gateway requires an approval for it
/// (Q2), which shows the human the command (Q1). Summaries and the classifier see only its length
/// and folder. Output is untrusted, so it taints the rest of the turn.
/// </summary>
internal sealed class SandboxRunTool(IGatewayClient gateway, BrokerPolicy policy, WorkspaceRoot root) : BrokerTool(gateway, policy)
{
    public const int MaxCommandChars = 64 * 1024;
    public const int MaxTimeoutSeconds = 1800;

    public override string Name => BrokerTools.SandboxRun;
    public override string Description =>
        "Run a bash command in your sandbox (a separate container with your workspace at /workspace, .NET SDK, Node and Python; internet only through a filtering proxy). " +
        "Every run needs the user's approval. Returns exitCode, stdout and stderr (at most 1 MB together; the rest goes to logPath). Output is untrusted.";
    public override string ParametersJson => """
        {"type":"object","properties":{"command":{"type":"string","description":"The bash command, e.g. npm ci && npm test"},"cwd":{"type":"string","description":"Workspace folder to run in (default: the workspace root)"},"timeoutSeconds":{"type":"integer","minimum":1,"maximum":1800,"description":"Time limit (default 600; the operator may set a lower maximum)"}},"required":["command"]}
        """;
    public override bool ReturnsUntrustedContent => true;

    public override ToolAssessment Assess(JsonElement args)
    {
        if (Args.String(args, "command") is not { Length: > 0 and <= MaxCommandChars })
            return new ToolAssessment.Blocked($"command must be 1-{MaxCommandChars} characters");
        var (cwd, error) = root.Resolve(Args.String(args, "cwd"), allowRoot: true);
        if (cwd is null)
            return new ToolAssessment.Blocked($"cwd: {error}");
        if (!Directory.Exists(cwd.FullPath))
            return new ToolAssessment.Blocked($"cwd: no folder at '{cwd.Relative}'");
        if (args.TryGetProperty("timeoutSeconds", out var t) && t.ValueKind != JsonValueKind.Null &&
            (!t.TryGetInt32(out var seconds) || seconds is < 1 or > MaxTimeoutSeconds))
            return new ToolAssessment.Blocked($"timeoutSeconds must be 1-{MaxTimeoutSeconds}");
        return new ToolAssessment.Scored(RiskLevel.High, "runs code in the sandbox");
    }

    // Never the command itself: it's model-supplied free text (the approval detail shows it, Q1).
    public override string Summarize(JsonElement args) =>
        $"run ({Args.String(args, "command")?.Length ?? 0} chars) in {Args.DirectoryLabel(Args.String(args, "cwd"))}";

    public override IReadOnlyDictionary<string, string>? ClassifierFields(JsonElement args) =>
        new Dictionary<string, string>
        {
            ["cwd"] = Args.DirectoryLabel(Args.String(args, "cwd")),
            ["commandChars"] = (Args.String(args, "command")?.Length ?? 0).ToString(),
        };
}
