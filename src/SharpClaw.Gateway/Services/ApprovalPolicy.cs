using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The gateway's automatic approval decisions (3.6 spec section 6): autonomy mode first, then a
/// standing grant. Read fresh on every request, so a toggle or revoke applies to the next call.
/// </summary>
public static class ApprovalPolicy
{
    /// <summary>Who decides a new approval automatically, or null when a human (or the unattended rule) must.</summary>
    public sealed record AutoDecision(string DecidedBy, Guid? GrantId);

    public static async Task<AutoDecision?> DecideAsync(
        SharpClawDbContext db, Guid agentId, string toolName, Guid threadId, DateTime now, CancellationToken ct)
    {
        if (await db.Agents.AsNoTracking().AnyAsync(a => a.Id == agentId && a.Autonomous, ct))
            return new AutoDecision(ApprovalDecidedBy.Autonomy, null);

        // Tools that act as the user are never covered by a grant, even one made before this rule.
        if (BrokerTools.NonGrantable.Contains(toolName))
            return null;

        var grantId = await ActiveGrants(db, agentId, now)
            .Where(g => g.ToolName == toolName && (g.ThreadId == null || g.ThreadId == threadId))
            .OrderBy(g => g.CreatedAt).ThenBy(g => g.Id)
            .Select(g => (Guid?)g.Id)
            .FirstOrDefaultAsync(ct);
        return grantId is null ? null : new AutoDecision(ApprovalDecidedBy.Grant, grantId);
    }

    /// <summary>Grants that are neither revoked nor expired at <paramref name="now"/>.</summary>
    public static IQueryable<ApprovalGrant> ActiveGrants(SharpClawDbContext db, Guid agentId, DateTime now) =>
        db.ApprovalGrants.AsNoTracking()
            .Where(g => g.AgentId == agentId && g.RevokedAt == null && (g.ExpiresAt == null || g.ExpiresAt > now));
}
