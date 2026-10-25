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

    public Task<bool> WaitAsync(Guid key, TimeSpan timeout, CancellationToken ct) =>
        For(key).WaitAsync(timeout, ct);
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
