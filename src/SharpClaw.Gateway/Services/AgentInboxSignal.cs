using System.Collections.Concurrent;

namespace SharpClaw.Gateway.Services;

/// <summary>In-process per-agent wake signal raised when a user message is queued.</summary>
public interface IAgentInboxSignal
{
    void Signal(Guid agentId);
    Task<bool> WaitAsync(Guid agentId, TimeSpan timeout, CancellationToken ct);
}

public sealed class AgentInboxSignal : IAgentInboxSignal
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _signals = new();

    private SemaphoreSlim For(Guid agentId) =>
        _signals.GetOrAdd(agentId, _ => new SemaphoreSlim(0));

    public void Signal(Guid agentId)
    {
        var sem = For(agentId);
        // Avoid unbounded permit accumulation; one pending permit is enough to wake a poller.
        if (sem.CurrentCount == 0)
        {
            try { sem.Release(); } catch (SemaphoreFullException) { /* ignore */ }
        }
    }

    public Task<bool> WaitAsync(Guid agentId, TimeSpan timeout, CancellationToken ct) =>
        For(agentId).WaitAsync(timeout, ct);
}
