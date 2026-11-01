using System.ComponentModel.DataAnnotations;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Agent-to-agent asks (3.5 spec section 4). Which agents may ask which lives only here, in gateway
/// config: never in the agent, its workspace or a skill. By default (<see cref="AllowAll"/>) any
/// agent may ask any other; a sender listed in <see cref="Allowed"/> may ask only those agents.
/// </summary>
public sealed class AgentMessagingOptions : IHasProblem
{
    public const string Section = "AgentMessaging";
    public const int MaxDescriptionChars = 300;

    /// <summary>
    /// True (the default): every agent may ask every other agent, except senders listed in
    /// <see cref="Allowed"/>. False: only the pairs in <see cref="Allowed"/>.
    /// </summary>
    public bool AllowAll { get; set; } = true;

    /// <summary>Per sender (agent name): the only agents it may ask, in the order shown to its model.</summary>
    public Dictionary<string, string[]> Allowed { get; set; } = new();

    /// <summary>Per agent: one line telling other agents what it is for (shown in the tool description).</summary>
    public Dictionary<string, string> Descriptions { get; set; } = new();

    /// <summary>The longest chain: human → A is hop 0, A → B hop 1, and so on.</summary>
    [Range(1, 10)] public int MaxHops { get; set; } = 3;

    /// <summary>
    /// The most asks that may follow from one human message, at every depth together. Bounds the
    /// fan-out (each agent can ask once per tool round) so a chain can't run on into hundreds of turns.
    /// </summary>
    [Range(1, 1000)] public int MaxAsksPerChain { get; set; } = 10;

    /// <summary>How long an ask waits for the recipient's reply (longer than the approval timeout by default).</summary>
    [Range(10, 3600)] public int AskTimeoutSeconds { get; set; } = 600;

    /// <summary>The longest reply returned to the asker; longer ones are cut and marked truncated.</summary>
    [Range(1000, 200_000)] public int MaxReplyChars { get; set; } = 32_000;

    /// <summary>
    /// The sender's own list, matched ignoring case like <c>ToolBroker:Agents</c>; null when it has none
    /// (then <see cref="AllowAll"/> decides).
    /// </summary>
    public IReadOnlyList<string>? ListFor(string sender) =>
        Allowed.FirstOrDefault(a => string.Equals(a.Key, sender, StringComparison.OrdinalIgnoreCase)).Value?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The agents <paramref name="sender"/> may ask, out of <paramref name="agents"/> (every agent's
    /// name): its own list in config order if it has one, else every other agent when
    /// <see cref="AllowAll"/>, else none. Returns the names as stored on the agents.
    /// </summary>
    public IReadOnlyList<string> RecipientsOf(string sender, IReadOnlyList<string> agents)
    {
        if (ListFor(sender) is { } list)
            return list
                .Select(n => agents.FirstOrDefault(a => string.Equals(a, n, StringComparison.OrdinalIgnoreCase)))
                .OfType<string>()
                .ToList();
        return AllowAll
            ? agents.Where(a => !string.Equals(a, sender, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    public bool MayAsk(string sender, string recipient) =>
        !string.Equals(sender, recipient, StringComparison.OrdinalIgnoreCase) &&
        (ListFor(sender) is { } list ? list.Contains(recipient, StringComparer.OrdinalIgnoreCase) : AllowAll);

    public string? DescriptionOf(string agent) =>
        Descriptions.FirstOrDefault(d => string.Equals(d.Key, agent, StringComparison.OrdinalIgnoreCase)).Value is { } text &&
        !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    public string? Problem()
    {
        foreach (var (sender, recipients) in Allowed)
            if (recipients.Any(r => string.Equals(r?.Trim(), sender, StringComparison.OrdinalIgnoreCase)))
                return $"AgentMessaging:Allowed:{sender} must not list the agent itself.";
        foreach (var (agent, text) in Descriptions)
            if (text is { Length: > MaxDescriptionChars })
                return $"AgentMessaging:Descriptions:{agent} must be at most {MaxDescriptionChars} characters.";
        return null;
    }
}
