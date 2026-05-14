using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Sessions;

public sealed class AgentSessionRegistry(IOptions<SharpClawOptions> options)
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly int _historyLimit = options.Value.ChatHistoryLimit;

    /// <summary>
    /// Get or create a session keyed by a channel identifier (e.g. Telegram chat ID or web session ID).
    /// </summary>
    public AgentSession GetOrCreate(string channelKey, string agentId) =>
        _sessions.GetOrAdd(channelKey, _ => new AgentSession(agentId, _historyLimit));

    public AgentSession? Get(string channelKey) =>
        _sessions.TryGetValue(channelKey, out var session) ? session : null;
}
