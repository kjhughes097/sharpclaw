using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The single path for client events. Durable events are stored first (getting their SSE id),
/// then published; ephemeral ones (<c>delta</c>) only go to the live bus.
/// </summary>
public interface IEventPublisher
{
    Task PublishDurableAsync<T>(Guid threadId, string type, T payload, CancellationToken ct = default);
    void PublishEphemeral<T>(Guid threadId, string type, T payload);
}

public sealed class EventPublisher(IThreadEventBus bus, IServiceScopeFactory scopes, TimeProvider time) : IEventPublisher
{
    // Persist-and-publish is serialised so the bus sees ids in increasing order; streams rely on
    // that to drop already-replayed events by id.
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task PublishDurableAsync<T>(Guid threadId, string type, T payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, SharpClawJson.Options);
        // Not tied to the caller's request: once the state change is saved, its event must be too.
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var row = new ThreadEvent
            {
                ThreadId = threadId,
                Type = type,
                Data = json,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };
            db.ThreadEvents.Add(row);
            await db.SaveChangesAsync(CancellationToken.None);
            bus.Publish(threadId, new ThreadSseEvent(type, json, row.Id));
        }
        finally
        {
            _lock.Release();
        }
    }

    public void PublishEphemeral<T>(Guid threadId, string type, T payload) =>
        bus.Publish(threadId, new ThreadSseEvent(type, JsonSerializer.Serialize(payload, SharpClawJson.Options)));
}
