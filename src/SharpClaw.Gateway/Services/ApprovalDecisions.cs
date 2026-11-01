using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The one place an approval leaves <c>pending</c>: a conditional update (so concurrent deciders
/// can't both win) committed with its durable <c>approval.decided</c>, then metrics and the agent's
/// wake-up signal.
/// </summary>
public sealed class ApprovalDecisions(
    GatewayMetrics metrics, ApprovalSignal signal, IEventPublisher events, TimeProvider time)
{
    /// <summary>
    /// Decides a pending approval. Returns false if it was no longer pending.
    /// <paramref name="decidedByUser"/> names the human (or <c>admin_key</c>) for user decisions.
    /// </summary>
    public async Task<bool> TryDecideAsync(
        SharpClawDbContext db, Approval approval, string status, string decidedBy, CancellationToken ct,
        string? decidedByUser = null)
    {
        var decided = await events.WriteAsync(db,
            batch => StageDecisionAsync(db, batch, approval, status, decidedBy, decidedByUser, ct), ct);
        if (decided)
            await AfterDecidedAsync(db, new[] { approval }, ct);
        return decided;
    }

    /// <summary>
    /// Inside a caller's event write: expires the turn's pending approvals, staging their
    /// <c>approval.decided</c> events in the same batch (so they precede the turn's own final event).
    /// Call <see cref="AfterDecidedAsync"/> with the result once the write has committed.
    /// </summary>
    public async Task<List<Approval>> StageExpiryForCorrelationAsync(
        SharpClawDbContext db, EventBatch batch, Guid correlationId, CancellationToken ct)
    {
        var expired = new List<Approval>();
        var pending = await db.Approvals.AsNoTracking()
            .Where(a => a.Status == ApprovalStatuses.Pending && a.CorrelationId == correlationId)
            .ToListAsync(ct);
        foreach (var approval in pending)
            if (await StageDecisionAsync(db, batch, approval, ApprovalStatuses.Expired, ApprovalDecidedBy.System, null, ct))
                expired.Add(approval);
        return expired;
    }

    /// <summary>Expires every pending approval past its deadline (the sweeper and lazy reads).</summary>
    public Task<int> ExpireDueAsync(SharpClawDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return ExpireAsync(db, db.Approvals.Where(a => a.Status == ApprovalStatuses.Pending && a.ExpiresAt <= now), ct);
    }

    /// <summary>A turn that ended (requeue) leaves nobody waiting for its approvals.</summary>
    public Task<int> ExpireForCorrelationAsync(SharpClawDbContext db, Guid correlationId, CancellationToken ct) =>
        ExpireAsync(db, db.Approvals.Where(a => a.Status == ApprovalStatuses.Pending && a.CorrelationId == correlationId), ct);

    /// <summary>Metrics and the agent's wake-up, once the decisions have committed.</summary>
    public async Task AfterDecidedAsync(SharpClawDbContext db, IReadOnlyCollection<Approval> decided, CancellationToken ct)
    {
        if (decided.Count == 0)
            return;
        var agentIds = decided.Select(a => a.AgentId).Distinct().ToList();
        var names = await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        foreach (var approval in decided)
        {
            var agentName = names.GetValueOrDefault(approval.AgentId, "");
            metrics.RecordApproval(agentName, approval.Status, approval.DecidedBy ?? "");
            metrics.RecordApprovalDecisionTime(agentName, approval.Status, approval.DecidedBy ?? "",
                (approval.DecidedAt!.Value - approval.CreatedAt).TotalSeconds);
            // Wake the agent's poll, then forget the key: a decided approval is never signalled again.
            signal.Signal(approval.Id);
            signal.Remove(approval.Id);
        }
    }

    /// <summary>The conditional update plus its staged event; updates <paramref name="approval"/> when it wins.</summary>
    private async Task<bool> StageDecisionAsync(
        SharpClawDbContext db, EventBatch batch, Approval approval, string status, string decidedBy,
        string? decidedByUser, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var updated = await db.Approvals
            .Where(a => a.Id == approval.Id && a.Status == ApprovalStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.DecidedBy, decidedBy)
                .SetProperty(a => a.DecidedByUser, decidedByUser)
                .SetProperty(a => a.DecidedAt, now), ct);
        if (updated == 0)
            return false;

        approval.Status = status;
        approval.DecidedBy = decidedBy;
        approval.DecidedByUser = decidedByUser;
        approval.DecidedAt = now;
        batch.Add(approval.ThreadId, SseEventNames.ApprovalDecided,
            new ApprovalDecidedEvent(approval.Id, approval.CorrelationId, status, decidedBy, approval.ThreadId, decidedByUser));
        return true;
    }

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
