using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Sessions;

public sealed class AgentSessionRegistry(IOptions<SharpClawOptions> options)
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly int _historyLimit = options.Value.ChatHistoryLimit;

    /// <summary>
    /// Get or create a session keyed by agent name.
    /// All channels talking to the same agent share a single session and LLM context.
    /// </summary>
    public AgentSession GetOrCreate(string agentName) =>
        _sessions.GetOrAdd(agentName, _ => new AgentSession(agentName, _historyLimit));

    public AgentSession? Get(string agentName) =>
        _sessions.TryGetValue(agentName, out var session) ? session : null;

    /// <summary>
    /// Returns all sessions that currently have an active LLM session (in-flight conversations).
    /// </summary>
    public IReadOnlyList<AgentSession> GetActiveSessions() =>
        _sessions.Values.Where(s => s.LlmSession is not null).ToList();
}
