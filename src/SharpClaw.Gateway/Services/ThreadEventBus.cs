using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SharpClaw.Gateway.Services;

/// <summary>A single Server-Sent Event to be written to the client thread stream.</summary>
public sealed record ThreadSseEvent(string Event, string DataJson);

/// <summary>In-memory, per-thread pub/sub. No replay: late subscribers miss earlier events.</summary>
public interface IThreadEventBus
{
    IDisposable Subscribe(Guid threadId, out ChannelReader<ThreadSseEvent> reader);
    void Publish(Guid threadId, ThreadSseEvent evt);
}

public sealed class ThreadEventBus : IThreadEventBus
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Channel<ThreadSseEvent>>> _subscribers = new();

    public IDisposable Subscribe(Guid threadId, out ChannelReader<ThreadSseEvent> reader)
    {
        var channel = Channel.CreateUnbounded<ThreadSseEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        var map = _subscribers.GetOrAdd(threadId, _ => new ConcurrentDictionary<Guid, Channel<ThreadSseEvent>>());
        map[id] = channel;
        reader = channel.Reader;
        return new Subscription(this, threadId, id);
    }

    public void Publish(Guid threadId, ThreadSseEvent evt)
    {
        if (_subscribers.TryGetValue(threadId, out var map))
            foreach (var channel in map.Values)
                channel.Writer.TryWrite(evt);
    }

    private void Unsubscribe(Guid threadId, Guid id)
    {
        if (_subscribers.TryGetValue(threadId, out var map))
        {
            if (map.TryRemove(id, out var channel))
                channel.Writer.TryComplete();
            if (map.IsEmpty)
                _subscribers.TryRemove(threadId, out _);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ThreadEventBus _bus;
        private readonly Guid _threadId;
        private readonly Guid _id;
        private bool _disposed;

        public Subscription(ThreadEventBus bus, Guid threadId, Guid id)
        {
            _bus = bus;
            _threadId = threadId;
            _id = id;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Unsubscribe(_threadId, _id);
        }
    }
}
