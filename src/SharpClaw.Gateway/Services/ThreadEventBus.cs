using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// A single Server-Sent Event. <c>Id</c> is set for durable events (stored in
/// <c>thread_events</c>) and null for ephemeral ones such as <c>delta</c>. For deltas,
/// <c>EndOffset</c> is the turn's buffered length after this delta (never sent to clients); a
/// stream that just sent a snapshot uses it to skip deltas the snapshot already contains.
/// </summary>
public sealed record ThreadSseEvent(
    string Event, string DataJson, long? Id = null, Guid? CorrelationId = null, int? EndOffset = null);

/// <summary>
/// In-memory pub/sub for live events. Subscribers either watch one thread or (with a null thread
/// id) every thread. Replay comes from <c>thread_events</c>, not from the bus.
/// </summary>
public interface IThreadEventBus
{
    /// <param name="threadId">The thread to watch, or null for all threads.</param>
    IDisposable Subscribe(Guid? threadId, out ChannelReader<ThreadSseEvent> reader);
    void Publish(Guid threadId, ThreadSseEvent evt);

    /// <summary>Open subscriptions: (per-thread, global).</summary>
    (int Thread, int Global) SubscriberCounts();
}

public sealed class ThreadEventBus : IThreadEventBus
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ThreadSseEvent>>> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, Channel<ThreadSseEvent>> _global = new();

    public IDisposable Subscribe(Guid? threadId, out ChannelReader<ThreadSseEvent> reader)
    {
        var channel = Channel.CreateUnbounded<ThreadSseEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        if (threadId is { } t)
            _subscribers.GetOrAdd(t, _ => new ConcurrentDictionary<Guid, Channel<ThreadSseEvent>>())[id] = channel;
        else
            _global[id] = channel;
        reader = channel.Reader;
        return new Subscription(this, threadId, id);
    }

    public void Publish(Guid threadId, ThreadSseEvent evt)
    {
        if (_subscribers.TryGetValue(threadId, out var map))
            foreach (var channel in map.Values)
                channel.Writer.TryWrite(evt);
        foreach (var channel in _global.Values)
            channel.Writer.TryWrite(evt);
    }

    public (int Thread, int Global) SubscriberCounts() =>
        (_subscribers.Values.Sum(m => m.Count), _global.Count);

    private void Unsubscribe(Guid? threadId, Guid id)
    {
        if (threadId is null)
        {
            if (_global.TryRemove(id, out var g))
                g.Writer.TryComplete();
            return;
        }
        if (_subscribers.TryGetValue(threadId.Value, out var map))
        {
            if (map.TryRemove(id, out var channel))
                channel.Writer.TryComplete();
            if (map.IsEmpty)
                _subscribers.TryRemove(threadId.Value, out _);
        }
    }

    private sealed class Subscription(ThreadEventBus bus, Guid? threadId, Guid id) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            bus.Unsubscribe(threadId, id);
        }
    }
}
