using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The one place an approval leaves <c>pending</c>: a conditional update (so concurrent deciders
/// can't both win), then metrics, the agent's wake-up signal and a durable <c>approval.decided</c>.
/// </summary>
public sealed class ApprovalDecisions(
    GatewayMetrics metrics, ApprovalSignal signal, IEventPublisher events, TimeProvider time)
{
    /// <summary>Decides a pending approval. Returns false if it was no longer pending.</summary>
    public async Task<bool> TryDecideAsync(
        SharpClawDbContext db, Approval approval, string status, string decidedBy, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Approvals
            .Where(a => a.Id == approval.Id && a.Status == ApprovalStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.DecidedBy, decidedBy)
                .SetProperty(a => a.DecidedAt, now), ct);
        if (updated == 0)
            return false;

        approval.Status = status;
        approval.DecidedBy = decidedBy;
        approval.DecidedAt = now;

        var agentName = await db.Agents.Where(a => a.Id == approval.AgentId).Select(a => a.Name).FirstAsync(ct);
        metrics.RecordApproval(agentName, status);
        metrics.RecordApprovalDecisionTime(agentName, status, (now - approval.CreatedAt).TotalSeconds);
        signal.Signal(approval.Id);
        await events.PublishDurableAsync(approval.ThreadId, SseEventNames.ApprovalDecided,
            new ApprovalDecidedEvent(approval.Id, approval.CorrelationId, status, decidedBy, approval.ThreadId), ct);
        return true;
    }

    /// <summary>Expires every pending approval past its deadline (the sweeper and lazy reads).</summary>
    public Task<int> ExpireDueAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return ExpireAsync(db, db.Approvals.Where(a => a.Status == ApprovalStatuses.Pending && a.ExpiresAt <= now), ct);
    }

    /// <summary>A turn that ended (reply, fail, requeue) leaves nobody waiting for its approvals.</summary>
    public Task<int> ExpireForCorrelationAsync(SharpClawDbContext db, Guid correlationId, CancellationToken ct) =>
        ExpireAsync(db, db.Approvals.Where(a => a.Status == ApprovalStatuses.Pending && a.CorrelationId == correlationId), ct);

    private async Task<int> ExpireAsync(SharpClawDbContext db, IQueryable<Approval> pending, CancellationToken ct)
    {
        var expired = 0;
        foreach (var approval in await pending.AsNoTracking().ToListAsync(ct))
            if (await TryDecideAsync(db, approval, ApprovalStatuses.Expired, ApprovalDecidedBy.System, ct))
                expired++;
        return expired;
    }
}

/// <summary>What a notifier may know about a pending approval: ids and labels, never arguments or results.</summary>
public sealed record ApprovalNotification(
    Guid ApprovalId, Guid ThreadId, Guid CorrelationId, string Agent, string ToolName, string Risk, string Summary,
    DateTime ExpiresAt);

/// <summary>Seam for telling the human an approval is waiting (ntfy, Telegram… in a later phase).</summary>
public interface IApprovalNotifier
{
    Task NotifyRequestedAsync(ApprovalNotification approval, CancellationToken ct);
}

public sealed class NoApprovalNotifier : IApprovalNotifier
{
    public Task NotifyRequestedAsync(ApprovalNotification approval, CancellationToken ct) => Task.CompletedTask;
}
