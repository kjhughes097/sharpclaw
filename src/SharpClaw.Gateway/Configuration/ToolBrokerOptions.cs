using System.ComponentModel.DataAnnotations;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// The tool broker (external tools spec sections 4 and 9). Nothing is enabled by default: an agent
/// can only call a broker tool listed under its name here, whatever its own <c>agent.json</c> says.
/// </summary>
public sealed class ToolBrokerOptions : IHasProblem
{
    public const string Section = "ToolBroker";
    public const int MinSandboxSecretChars = 16;

    /// <summary>Per agent (by name): the broker tools it may call and how to reach its sandbox.</summary>
    public Dictionary<string, BrokerAgentOptions> Agents { get; set; } = new();

    /// <summary>
    /// Tools that always need a gateway-checked approval (Q2). Null (unset) means
    /// <see cref="BrokerTools.DefaultAlwaysApprove"/>; empty entries are ignored, so an operator can
    /// remove every default with <c>ToolBroker__AlwaysApprove__0=</c>.
    /// </summary>
    public string[]? AlwaysApprove { get; set; }

    /// <summary>An approved approval must be used within this many seconds of its decision.</summary>
    [Range(1, 86400)] public int ApprovalUseSeconds { get; set; } = 600;

    /// <summary>The largest result the broker returns to an agent (sandbox_run alone may return 1 MB of output).</summary>
    [Range(1024, 64 * 1024 * 1024)] public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>The longest a <c>sandbox_run</c> may take (Q6: default 10 minutes, at most 30).</summary>
    [Range(1, 1800)] public int SandboxRunMaxSeconds { get; set; } = 600;

    /// <summary>
    /// Hosts whose <c>http_fetch</c> calls the agent scores <c>low</c> (others are <c>medium</c>, Q3).
    /// Matched exactly or as a parent domain. Sent to the agent by <c>GET /agent/tools</c>.
    /// </summary>
    public string[] FetchAllowHosts { get; set; } = [];

    /// <summary>
    /// Hosts <c>git_clone</c> may clone public repositories from, over HTTPS (exact host match).
    /// Null (unset) means github.com and gitlab.com.
    /// </summary>
    public string[]? CloneAllowHosts { get; set; }

    public static readonly string[] DefaultCloneAllowHosts = ["github.com", "gitlab.com"];

    public IReadOnlyList<string> CloneHosts =>
        (CloneAllowHosts ?? DefaultCloneAllowHosts).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant()).ToList();

    public IReadOnlySet<string> AlwaysApproveSet =>
        (AlwaysApprove ?? BrokerTools.DefaultAlwaysApprove.ToArray())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The agent's broker settings, matching its name case-insensitively; null when it has none.</summary>
    public BrokerAgentOptions? ForAgent(string agentName) =>
        Agents.FirstOrDefault(a => string.Equals(a.Key, agentName, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Null when valid; otherwise the validation message.</summary>
    public string? Problem()
    {
        foreach (var name in AlwaysApproveSet)
            if (!BrokerTools.All.Contains(name))
                return $"ToolBroker:AlwaysApprove: unknown tool '{name}'.";
        foreach (var (agent, options) in Agents)
        {
            foreach (var tool in options.Tools)
                if (!BrokerTools.All.Contains(tool))
                    return $"ToolBroker:Agents:{agent}:Tools: unknown tool '{tool}'.";
            if (options.SandboxUrl is { Length: > 0 } url &&
                (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
                return $"ToolBroker:Agents:{agent}:SandboxUrl must be an absolute http(s) URL.";
            // Empty means "no sandbox yet" (its tools answer 503); a short one is a mistake.
            if (options.SandboxSecret is { Length: > 0 and < MinSandboxSecretChars })
                return $"ToolBroker:Agents:{agent}:SandboxSecret must be at least {MinSandboxSecretChars} characters.";
        }
        if (FetchAllowHosts.Any(string.IsNullOrWhiteSpace))
            return "ToolBroker:FetchAllowHosts must not contain empty entries.";
        return null;
    }
}

public sealed class BrokerAgentOptions
{
    /// <summary>Broker tools this agent may call (the operator's gate; see <see cref="BrokerTools"/>).</summary>
    public string[] Tools { get; set; } = [];

    /// <summary>The agent's own sandbox runner, e.g. <c>http://sandbox-coder:8090</c>. Empty: no sandbox tools.</summary>
    public string? SandboxUrl { get; set; }

    /// <summary>Service secret the gateway presents to the runner (<c>SANDBOX_&lt;NAME&gt;_SECRET</c>).</summary>
    public string? SandboxSecret { get; set; }
}

/// <summary><c>web_search</c> (external tools spec section 7.2): the cheap model that runs OpenRouter's web plugin.</summary>
public sealed class WebSearchOptions
{
    public const string Section = "WebSearch";

    [Required(ErrorMessage = "WebSearch:Model is required.")]
    public string Model { get; set; } = "openai/gpt-4o-mini";

    [Range(1, 600)] public int TimeoutSeconds { get; set; } = 60;
}
