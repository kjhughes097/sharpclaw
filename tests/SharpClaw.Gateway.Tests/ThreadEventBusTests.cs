using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>The in-memory event bus: routing, unsubscribe, and the bounded per-subscriber buffer.</summary>
public class ThreadEventBusTests
{
    private static ThreadEventBus Bus(int buffer = 16) =>
        new(Options.Create(new EventsOptions { SubscriberBufferSize = buffer }), NullLogger<ThreadEventBus>.Instance);

    private static ThreadSseEvent Evt(long id) => new("message.created", "{}", id);

    private static List<ThreadSseEvent> Drain(ChannelReader<ThreadSseEvent> reader)
    {
        var items = new List<ThreadSseEvent>();
        while (reader.TryRead(out var e))
            items.Add(e);
        return items;
    }

    [Fact]
    public void Thread_subscribers_get_their_thread_and_global_ones_get_everything()
    {
        var bus = Bus();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var subA = bus.Subscribe(a, out var readerA);
        using var subAll = bus.Subscribe(null, out var readerAll);

        bus.Publish(a, Evt(1));
        bus.Publish(b, Evt(2));

        Assert.Equal(new long?[] { 1 }, (Drain(readerA)).Select(e => e.Id));
        Assert.Equal(new long?[] { 1, 2 }, (Drain(readerAll)).Select(e => e.Id));
        Assert.Equal((1, 1), bus.SubscriberCounts());
    }

    [Fact]
    public void Disposing_a_subscription_completes_its_reader_and_forgets_it()
    {
        var bus = Bus();
        var sub = bus.Subscribe(Guid.NewGuid(), out var reader);
        sub.Dispose();
        sub.Dispose();

        Assert.True(reader.Completion.IsCompleted);
        Assert.Equal((0, 0), bus.SubscriberCounts());
    }

    [Fact]
    public async Task A_subscriber_that_falls_behind_is_closed_after_its_buffered_events()
    {
        var bus = Bus(buffer: 16);
        var thread = Guid.NewGuid();
        using var slow = bus.Subscribe(thread, out var slowReader);
        using var other = bus.Subscribe(thread, out var otherReader);

        for (var i = 1; i <= 20; i++)
        {
            bus.Publish(thread, Evt(i));
            // The healthy subscriber keeps up.
            Assert.True(otherReader.TryRead(out _));
        }

        // The slow one still gets what was buffered, then its stream ends (the client reconnects
        // with Last-Event-ID and recovers the rest by replay).
        var got = Drain(slowReader);
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long?)i), got.Select(e => e.Id));
        await slowReader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, bus.Overflows);
        Assert.Equal((1, 0), bus.SubscriberCounts());
    }
}
