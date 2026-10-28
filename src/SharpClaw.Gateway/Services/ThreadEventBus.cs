using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

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

    /// <summary>Subscriptions closed because their reader fell too far behind (since start).</summary>
    long Overflows { get; }
}

/// <remarks>
/// Each subscription has a bounded buffer. A reader that falls behind (a stalled browser tab) would
/// otherwise hold every event, deltas included, in memory. When its buffer is full the subscription
/// is closed instead: the SSE response ends after the buffered events, and the client reconnects with
/// <c>Last-Event-ID</c>, getting the missed durable events by replay and in-flight text by snapshot.
/// </remarks>
public sealed class ThreadEventBus(IOptions<EventsOptions> options, ILogger<ThreadEventBus> logger) : IThreadEventBus
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ThreadSseEvent>>> _subscribers = new();
    private readonly ConcurrentDictionary<Guid, Channel<ThreadSseEvent>> _global = new();
    private long _overflows;

    public long Overflows => Interlocked.Read(ref _overflows);

    public IDisposable Subscribe(Guid? threadId, out ChannelReader<ThreadSseEvent> reader)
    {
        var channel = Channel.CreateBounded<ThreadSseEvent>(new BoundedChannelOptions(options.Value.SubscriberBufferSize)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite then fails when full, and we close the subscription (see the remarks).
            FullMode = BoundedChannelFullMode.Wait,
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
            foreach (var (id, channel) in map)
                Write(channel, evt, threadId, id);
        foreach (var (id, channel) in _global)
            Write(channel, evt, null, id);
    }

    private void Write(Channel<ThreadSseEvent> channel, ThreadSseEvent evt, Guid? threadId, Guid id)
    {
        if (channel.Writer.TryWrite(evt))
            return;
        // Full (or already closed). Complete it once; the reader drains what it has, then ends.
        if (channel.Writer.TryComplete())
        {
            Interlocked.Increment(ref _overflows);
            logger.LogWarning("sse subscriber too slow; closing it: scope={Scope} buffer={Buffer}",
                threadId is null ? "global" : "thread", options.Value.SubscriberBufferSize);
        }
        Unsubscribe(threadId, id);
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
