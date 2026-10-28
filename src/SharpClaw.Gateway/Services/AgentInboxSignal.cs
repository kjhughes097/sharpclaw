using System.Collections.Concurrent;

namespace SharpClaw.Gateway.Services;

/// <summary>In-process wake-up signal keyed by an id, used to end long polls early.</summary>
public abstract class KeyedSignal
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _signals = new();

    private SemaphoreSlim For(Guid key) => _signals.GetOrAdd(key, _ => new SemaphoreSlim(0));

    public void Signal(Guid key)
    {
        var sem = For(key);
        // One pending permit is enough to wake a poller; avoid accumulating permits.
        if (sem.CurrentCount == 0)
        {
            try { sem.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }
    }

    private int _waiters;

    public async Task<bool> WaitAsync(Guid key, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiters);
        try
        {
            return await For(key).WaitAsync(timeout, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }
    }

    /// <summary>Pollers currently blocked in <see cref="WaitAsync"/> (lets tests act once a long poll is waiting).</summary>
    public int Waiters => Volatile.Read(ref _waiters);

    /// <summary>
    /// Forgets a key that will never be signalled again (e.g. a decided approval), so the map
    /// doesn't grow forever. A waiter already blocked on it still wakes from an earlier
    /// <see cref="Signal"/>; one arriving later re-creates the entry and waits one poll slice at most.
    /// </summary>
    public void Remove(Guid key) => _signals.TryRemove(key, out _);

    /// <summary>Keys currently tracked (for tests and diagnostics).</summary>
    public int Count => _signals.Count;
}

/// <summary>Per-agent signal raised when a user message is queued.</summary>
public interface IAgentInboxSignal
{
    void Signal(Guid agentId);
    Task<bool> WaitAsync(Guid agentId, TimeSpan timeout, CancellationToken ct);
}

public sealed class AgentInboxSignal : KeyedSignal, IAgentInboxSignal;

/// <summary>Per-approval signal raised when the human decides.</summary>
public sealed class ApprovalSignal : KeyedSignal;
