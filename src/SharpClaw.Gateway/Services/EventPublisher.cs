using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The single path for client events. Durable events are stored in <c>thread_events</c> (their row
/// id is the SSE id) in the same transaction as the state change they describe, then published;
/// ephemeral ones (<c>delta</c>) only go to the live bus.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Runs a state change that emits durable events, atomically: <paramref name="work"/> makes its
    /// changes on <paramref name="db"/> and stages events on the batch; both are saved and committed
    /// together, then the events are published. Nothing is published if the work or the commit fails.
    /// Must not be nested.
    /// </summary>
    Task<T> WriteAsync<T>(SharpClawDbContext db, Func<EventBatch, Task<T>> work, CancellationToken ct);

    /// <summary>A durable event with no accompanying state change (its own scope and transaction).</summary>
    Task PublishDurableAsync<T>(Guid threadId, string type, T payload, CancellationToken ct = default);

    /// <summary>A streamed content delta; <paramref name="endOffset"/> is the turn buffer's length after it.</summary>
    void PublishDelta(Guid threadId, Guid correlationId, string text, int? endOffset);
}

/// <summary>Durable events staged by one <see cref="IEventPublisher.WriteAsync{T}"/>.</summary>
public sealed class EventBatch(SharpClawDbContext db, TimeProvider time)
{
    private readonly List<ThreadEvent> _rows = new();

    internal IReadOnlyList<ThreadEvent> Rows => _rows;

    public void Add<TPayload>(Guid threadId, string type, TPayload payload)
    {
        var row = new ThreadEvent
        {
            ThreadId = threadId,
            Type = type,
            Data = JsonSerializer.Serialize(payload, SharpClawJson.Options),
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.ThreadEvents.Add(row);
        _rows.Add(row);
    }
}

public sealed class EventPublisher(IThreadEventBus bus, IServiceScopeFactory scopes, TimeProvider time) : IEventPublisher
{
    // Every event-producing write runs under this lock, from BEGIN to publish, so the bus sees ids
    // in increasing order (streams rely on that to drop already-replayed events by id). It is taken
    // before the SQLite write transaction begins, never inside one, so it can't deadlock with the
    // database's own write lock.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly AsyncLocal<bool> InWrite = new();

    public async Task<T> WriteAsync<T>(SharpClawDbContext db, Func<EventBatch, Task<T>> work, CancellationToken ct)
    {
        if (InWrite.Value)
            throw new InvalidOperationException("Event writes must not be nested (the publish lock is not re-entrant).");

        await _lock.WaitAsync(ct);
        InWrite.Value = true;
        try
        {
            var batch = new EventBatch(db, time);
            T result;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                result = await work(batch);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            foreach (var row in batch.Rows)
                bus.Publish(row.ThreadId, new ThreadSseEvent(row.Type, row.Data, row.Id));
            return result;
        }
        finally
        {
            InWrite.Value = false;
            _lock.Release();
        }
    }

    public async Task PublishDurableAsync<T>(Guid threadId, string type, T payload, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        // Not tied to the caller's request: an event announcing something that already happened
        // (e.g. a tool call already streamed to the agent) must be stored even if the client left.
        await WriteAsync(db, batch =>
        {
            batch.Add(threadId, type, payload);
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    public void PublishDelta(Guid threadId, Guid correlationId, string text, int? endOffset) =>
        bus.Publish(threadId, new ThreadSseEvent(SseEventNames.Delta,
            JsonSerializer.Serialize(new DeltaEvent(correlationId, text, threadId), SharpClawJson.Options), null, correlationId, endOffset));
}
