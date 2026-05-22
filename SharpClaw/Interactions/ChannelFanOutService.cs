using System.Collections.Concurrent;
using SharpClaw.Abstractions;

namespace SharpClaw.Interactions;

/// <summary>
/// Manages active channel sinks per agent and broadcasts messages to all connected channels.
/// </summary>
public sealed class ChannelFanOutService(ILogger<ChannelFanOutService> logger)
{
    private readonly ConcurrentDictionary<string, List<IChannelSink>> _sinks = new();
    private readonly Lock _lock = new();

    /// <summary>
    /// Register a sink for an agent. Messages broadcast to this agent will be delivered to this sink.
    /// </summary>
    public void Register(string agentName, IChannelSink sink)
    {
        lock (_lock)
        {
            var sinks = _sinks.GetOrAdd(agentName, _ => []);
            sinks.Add(sink);
        }

        logger.LogInformation(
            "Registered {ChannelType} sink {ChannelId} for agent {AgentName}",
            sink.Type, sink.ChannelId, agentName);
    }

    /// <summary>
    /// Unregister a sink. No more messages will be delivered to it.
    /// </summary>
    public void Unregister(string agentName, IChannelSink sink)
    {
        lock (_lock)
        {
            if (_sinks.TryGetValue(agentName, out var sinks))
            {
                sinks.Remove(sink);
                if (sinks.Count == 0)
                    _sinks.TryRemove(agentName, out _);
            }
        }

        logger.LogInformation(
            "Unregistered {ChannelType} sink {ChannelId} for agent {AgentName}",
            sink.Type, sink.ChannelId, agentName);
    }

    /// <summary>
    /// Broadcast a message to all sinks registered for an agent, optionally excluding the origin channel.
    /// </summary>
    public async Task BroadcastAsync(
        string agentName,
        string text,
        string? excludeChannelId = null,
        CancellationToken ct = default)
    {
        List<IChannelSink> targets;

        lock (_lock)
        {
            if (!_sinks.TryGetValue(agentName, out var sinks) || sinks.Count == 0)
                return;

            targets = sinks
                .Where(s => s.ChannelId != excludeChannelId)
                .ToList();
        }

        if (targets.Count == 0)
            return;

        logger.LogDebug(
            "Broadcasting to {Count} sink(s) for agent {AgentName} (excluding {Excluded})",
            targets.Count, agentName, excludeChannelId ?? "none");

        var tasks = targets.Select(async sink =>
        {
            try
            {
                await sink.DeliverAsync(text, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to deliver message to {ChannelType} sink {ChannelId}",
                    sink.Type, sink.ChannelId);
            }
        });

        await Task.WhenAll(tasks);
    }
}
