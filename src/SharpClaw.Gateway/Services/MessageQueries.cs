using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

public static class MessageQueries
{
    /// <summary>
    /// A thread's rows in <c>seq</c> order. Without <paramref name="includeTools"/> only user
    /// messages and final replies; <paramref name="after"/> is a message id to continue from.
    /// </summary>
    public static async Task<List<MessageDto>> ListAsync(
        SharpClawDbContext db, Guid threadId, Guid? after, bool includeTools, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId);
        if (!includeTools)
            query = query.Where(m => m.Kind == MessageKinds.Message);

        if (after is { } afterId)
        {
            var anchor = await db.Messages.AsNoTracking()
                .Where(m => m.Id == afterId && m.ThreadId == threadId)
                .Select(m => (int?)m.Seq)
                .FirstOrDefaultAsync(ct);
            if (anchor is { } seq)
                query = query.Where(m => m.Seq > seq);
        }

        var rows = await query.OrderBy(m => m.Seq).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public static MessageDto ToDto(Message m) => new(
        m.Id, m.Role, m.Content, m.Status, m.CorrelationId, m.CreatedAt, m.Kind, m.Seq,
        m.ToolCalls is null ? null : JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, SharpClawJson.Options),
        m.ToolCallId, m.ToolOk, m.Untrusted, m.Truncated);

    /// <summary>
    /// The next <c>seq</c> for the thread. Call inside a write transaction (SQLite <c>BEGIN
    /// IMMEDIATE</c>) so concurrent inserts can't take the same number.
    /// </summary>
    public static async Task<int> NextSeqAsync(SharpClawDbContext db, Guid threadId, CancellationToken ct) =>
        (await db.Messages.Where(m => m.ThreadId == threadId).MaxAsync(m => (int?)m.Seq, ct) ?? 0) + 1;
}
