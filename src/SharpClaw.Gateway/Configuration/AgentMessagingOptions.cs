using System.ComponentModel.DataAnnotations;

namespace SharpClaw.Gateway.Configuration;

/// <summary>
/// Agent-to-agent asks (3.5 spec section 4). Which agents may ask which lives only here, in gateway
/// config: never in the agent, its workspace or a skill. Nothing is allowed by default.
/// </summary>
public sealed class AgentMessagingOptions : IHasProblem
{
    public const string Section = "AgentMessaging";
    public const int MaxDescriptionChars = 300;

    /// <summary>Per sender (agent name): the agents it may ask, in the order shown to its model.</summary>
    public Dictionary<string, string[]> Allowed { get; set; } = new();

    /// <summary>Per agent: one line telling other agents what it is for (shown in the tool description).</summary>
    public Dictionary<string, string> Descriptions { get; set; } = new();

    /// <summary>The longest chain: human → A is hop 0, A → B hop 1, and so on.</summary>
    [Range(1, 10)] public int MaxHops { get; set; } = 3;

    /// <summary>How long an ask waits for the recipient's reply (longer than the approval timeout by default).</summary>
    [Range(10, 3600)] public int AskTimeoutSeconds { get; set; } = 600;

    /// <summary>The longest reply returned to the asker; longer ones are cut and marked truncated.</summary>
    [Range(1000, 200_000)] public int MaxReplyChars { get; set; } = 32_000;

    /// <summary>The agents <paramref name="sender"/> may ask, matched ignoring case like <c>ToolBroker:Agents</c>.</summary>
    public IReadOnlyList<string> RecipientsOf(string sender) =>
        (Allowed.FirstOrDefault(a => string.Equals(a.Key, sender, StringComparison.OrdinalIgnoreCase)).Value ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool MayAsk(string sender, string recipient) =>
        RecipientsOf(sender).Contains(recipient, StringComparer.OrdinalIgnoreCase);

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
