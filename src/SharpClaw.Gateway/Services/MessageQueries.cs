using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

public static class MessageQueries
{
    public static async Task<List<MessageDto>> ListAsync(
        SharpClawDbContext db, Guid threadId, Guid? after, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId);

        if (after is { } afterId)
        {
            var anchor = await db.Messages.AsNoTracking()
                .Where(m => m.Id == afterId)
                .Select(m => new { m.CreatedAt, m.Id })
                .FirstOrDefaultAsync(ct);
            if (anchor is not null)
            {
                query = query.Where(m =>
                    m.CreatedAt > anchor.CreatedAt ||
                    (m.CreatedAt == anchor.CreatedAt && m.Id.CompareTo(anchor.Id) > 0));
            }
        }

        return await query
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new MessageDto(m.Id, m.Role, m.Content, m.Status, m.CorrelationId, m.CreatedAt))
            .ToListAsync(ct);
    }
}
