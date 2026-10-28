using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// The ownership rule for agent-listener calls about a turn, in one place: the user message must
/// exist, belong to the calling agent, sit in the given thread, and (where required) be claimed.
/// Anything else is reported as "not found", so an agent learns nothing about other agents' data.
/// </summary>
public static class TurnGuard
{
    /// <summary>The agent's user message, in <paramref name="threadId"/> when given; null if it isn't theirs.</summary>
    public static Task<Message?> FindOwnedAsync(
        SharpClawDbContext db, Guid agentId, Guid messageId, Guid? threadId, CancellationToken ct, bool track = false)
    {
        var messages = track ? db.Messages : db.Messages.AsNoTracking();
        return messages.FirstOrDefaultAsync(m =>
            m.Id == messageId && m.AgentId == agentId && m.Role == Roles.User && m.Kind == MessageKinds.Message &&
            (threadId == null || m.ThreadId == threadId), ct);
    }

    /// <summary>As <see cref="FindOwnedAsync"/>, but only while the agent holds the claim.</summary>
    public static async Task<Message?> FindClaimedAsync(
        SharpClawDbContext db, Guid agentId, Guid messageId, Guid? threadId, CancellationToken ct, bool track = false)
    {
        var msg = await FindOwnedAsync(db, agentId, messageId, threadId, ct, track);
        return msg?.Status == MessageStatus.Claimed ? msg : null;
    }

    /// <summary>True when the thread exists and belongs to the agent.</summary>
    public static Task<bool> OwnsThreadAsync(SharpClawDbContext db, Guid agentId, Guid threadId, CancellationToken ct) =>
        db.Threads.AsNoTracking().AnyAsync(t => t.Id == threadId && t.AgentId == agentId, ct);
}
