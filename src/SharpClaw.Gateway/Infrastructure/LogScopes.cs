namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// The structured-log scopes we use (log attributes → Loki structured metadata). One place for the
/// key names so gateway and agent logs for one turn join on the same keys. Never put content here.
/// </summary>
public static class LogScopes
{
    public const string Agent = "agent";
    public const string AgentId = "agent_id";
    public const string ThreadId = "thread_id";
    public const string CorrelationId = "correlation_id";

    /// <summary>
    /// A turn: the thread and the user message being processed (the correlation id), plus the
    /// agent id when the caller isn't already inside the agent's scope (client-listener requests).
    /// </summary>
    public static IDisposable? Turn(ILogger logger, Guid threadId, Guid correlationId, Guid? agentId = null)
    {
        var state = new Dictionary<string, object>
        {
            [ThreadId] = threadId.ToString(),
            [CorrelationId] = correlationId.ToString(),
        };
        if (agentId is { } id)
            state[AgentId] = id.ToString();
        return logger.BeginScope(state);
    }

    /// <summary>An authenticated agent request.</summary>
    public static IDisposable? ForAgent(ILogger logger, string agentName, Guid agentId) =>
        logger.BeginScope(new Dictionary<string, object>
        {
            [Agent] = agentName,
            [AgentId] = agentId.ToString(),
        });
}
