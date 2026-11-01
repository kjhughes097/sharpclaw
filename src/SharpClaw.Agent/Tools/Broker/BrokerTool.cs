using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>
/// A tool that runs outside the agent: in the gateway (web search, GitHub) or in the agent's sandbox,
/// always through the gateway's broker (external tools spec section 4). Hardline rules, risk, taint,
/// smart mode and approval stay in <see cref="ToolDispatcher"/> exactly as for local tools; the
/// gateway's checks are a second, independent gate.
/// </summary>
public abstract class BrokerTool(IGatewayClient gateway, BrokerPolicy policy) : IAgentTool
{
    protected BrokerPolicy Policy { get; } = policy;

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string ParametersJson { get; }
    public abstract bool ReturnsUntrustedContent { get; }

    /// <summary>The gateway will refuse this tool without an approval, so the dispatcher always asks.</summary>
    public bool AlwaysRequiresApproval => Policy.AlwaysApprove.Contains(Name);

    /// <summary>The gateway renders the approval detail (a command, a comment) from the arguments.</summary>
    public bool ApprovalNeedsArguments => true;

    public abstract ToolAssessment Assess(JsonElement args);
    public abstract string Summarize(JsonElement args);
    public abstract IReadOnlyDictionary<string, string>? ClassifierFields(JsonElement args);

    /// <summary>Broker tools need the call's identity; the dispatcher always uses the overload with context.</summary>
    public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, CancellationToken ct) =>
        throw new InvalidOperationException("Broker tools run with a ToolCallContext.");

    public Task<ToolResult> ExecuteUncheckedAsync(JsonElement args, ToolCallContext context, CancellationToken ct) =>
        gateway.InvokeToolAsync(Name, new InvokeToolRequest(
            context.CorrelationId, context.ThreadId, context.ToolCallId, args.Clone(), context.ApprovalId, context.Tainted), ct);
}

/// <summary>
/// The operator's broker settings for this agent, read from the gateway (<c>GET /agent/tools</c>)
/// at startup: which tools always need an approval, which <c>http_fetch</c> hosts are low risk,
/// which hosts <c>git_clone</c> may use, and which agents <c>ask_agent</c> may ask.
/// </summary>
public sealed record BrokerPolicy(IReadOnlySet<string> AlwaysApprove, IReadOnlyList<string> FetchAllowHosts)
{
    private static readonly string[] DefaultCloneHosts = ["github.com", "gitlab.com"];

    public IReadOnlyList<string> CloneAllowHosts { get; init; } = DefaultCloneHosts;

    /// <summary>The agents <c>ask_agent</c> may ask (3.5 spec section 5.7); empty means nobody.</summary>
    public IReadOnlyList<AskAgentTargetDto> AskAgents { get; init; } = [];

    /// <summary>When the gateway can't be asked: every broker tool needs approval and no host is allow-listed.</summary>
    public static BrokerPolicy Conservative { get; } = new(BrokerTools.All.ToHashSet(), Array.Empty<string>());

    public static BrokerPolicy From(AgentToolPolicyResponse response) =>
        new(response.AlwaysApprove.ToHashSet(StringComparer.Ordinal), response.FetchAllowHosts)
        {
            CloneAllowHosts = response.CloneAllowHosts ?? DefaultCloneHosts,
            AskAgents = response.AskAgents ?? [],
        };

    public bool IsFetchAllowed(string host) => FetchAllowHosts.Any(entry => ToolUrls.HostMatches(host, entry));
}
