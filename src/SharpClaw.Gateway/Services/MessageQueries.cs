using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

public static class MessageQueries
{
    /// <summary>All of a thread's rows in <c>seq</c> order; without <paramref name="includeTools"/>, only user messages and final replies.</summary>
    public static async Task<List<MessageDto>> ListAsync(
        SharpClawDbContext db, Guid threadId, bool includeTools, CancellationToken ct)
    {
        var query = db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId);
        if (!includeTools)
            query = query.Where(m => m.Kind == MessageKinds.Message);
        var rows = await query.OrderBy(m => m.Seq).ToListAsync(ct);
        var senders = await SenderNamesAsync(db, rows, ct);
        var attachments = await AttachmentsAsync(db, rows, ct);
        return rows.Select(m => ToDto(m, senders, attachments)).ToList();
    }

    /// <summary>
    /// One page of a thread's rows, ascending by <c>seq</c> (3.3 spec section 5.2). Without a cursor
    /// it is the latest <paramref name="limit"/> rows; <paramref name="beforeSeq"/> pages backwards;
    /// <paramref name="afterSeq"/> (or the <paramref name="after"/> message id) pages forwards.
    /// </summary>
    public static async Task<(List<MessageDto> Rows, bool HasMoreBefore)> PageAsync(
        SharpClawDbContext db, Guid threadId, bool includeTools, int limit, int? beforeSeq, int? afterSeq, Guid? after,
        CancellationToken ct)
    {
        var scope = db.Messages.AsNoTracking().Where(m => m.ThreadId == threadId);
        if (!includeTools)
            scope = scope.Where(m => m.Kind == MessageKinds.Message);

        if (after is { } afterId)
        {
            // An unknown anchor keeps the pre-3.3 behaviour: no lower bound.
            var anchor = await db.Messages.AsNoTracking()
                .Where(m => m.Id == afterId && m.ThreadId == threadId)
                .Select(m => (int?)m.Seq)
                .FirstOrDefaultAsync(ct);
            if (anchor is { } seq)
                afterSeq = afterSeq is { } given ? Math.Max(given, seq) : seq;
        }

        List<Message> rows;
        if (afterSeq is { } low)
            rows = await scope.Where(m => m.Seq > low).OrderBy(m => m.Seq).Take(limit).ToListAsync(ct);
        else
        {
            var q = beforeSeq is { } high ? scope.Where(m => m.Seq < high) : scope;
            rows = await q.OrderByDescending(m => m.Seq).Take(limit).ToListAsync(ct);
            rows.Reverse();
        }

        var hasMoreBefore = rows.Count > 0
            ? await scope.AnyAsync(m => m.Seq < rows[0].Seq, ct)
            : afterSeq is { } a && await scope.AnyAsync(m => m.Seq <= a, ct);
        var senders = await SenderNamesAsync(db, rows, ct);
        var attachments = await AttachmentsAsync(db, rows, ct);
        return (rows.Select(m => ToDto(m, senders, attachments)).ToList(), hasMoreBefore);
    }

    /// <param name="senderNames">Names of the agents that sent messages in the page (asks), by id.</param>
    /// <param name="attachments">Attachment metadata by message id.</param>
    public static MessageDto ToDto(
        Message m, IReadOnlyDictionary<Guid, string>? senderNames = null,
        IReadOnlyDictionary<Guid, List<AttachmentDto>>? attachments = null) => new(
        m.Id, m.Role, m.Content, m.Status, m.CorrelationId, m.CreatedAt, m.Kind, m.Seq,
        m.ToolCalls is null ? null : JsonSerializer.Deserialize<List<ChatToolCall>>(m.ToolCalls, SharpClawJson.Options),
        m.ToolCallId, m.ToolOk, m.Untrusted, m.Truncated, m.FailReason, m.SentByUser,
        m.SenderAgentId is { } sender ? senderNames?.GetValueOrDefault(sender) : null,
        attachments?.GetValueOrDefault(m.Id));

    /// <summary>Attachment metadata (never the bytes) for the user messages in <paramref name="rows"/>, by message id.</summary>
    public static async Task<IReadOnlyDictionary<Guid, List<AttachmentDto>>> AttachmentsAsync(
        SharpClawDbContext db, IEnumerable<Message> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.Role == Roles.User && r.Kind == MessageKinds.Message).Select(r => r.Id).ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, List<AttachmentDto>>();
        var found = await db.MessageAttachments.AsNoTracking()
            .Where(a => ids.Contains(a.MessageId))
            .OrderBy(a => a.Position)
            .Select(a => new { a.MessageId, Dto = new AttachmentDto(a.Id, a.FileName, a.Kind, a.MediaType, a.SizeBytes) })
            .ToListAsync(ct);
        return found.GroupBy(a => a.MessageId).ToDictionary(g => g.Key, g => g.Select(a => a.Dto).ToList());
    }

    /// <summary>Names of the agents that sent any of <paramref name="rows"/>; null when none did.</summary>
    public static async Task<IReadOnlyDictionary<Guid, string>?> SenderNamesAsync(
        SharpClawDbContext db, IEnumerable<Message> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.SenderAgentId is not null).Select(r => r.SenderAgentId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return null;
        return await db.Agents.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    /// <summary>
    /// The next <c>seq</c> for the thread. Call inside a write transaction (SQLite <c>BEGIN
    /// IMMEDIATE</c>) so concurrent inserts can't take the same number.
    /// </summary>
    public static async Task<int> NextSeqAsync(SharpClawDbContext db, Guid threadId, CancellationToken ct) =>
        (await db.Messages.Where(m => m.ThreadId == threadId).MaxAsync(m => (int?)m.Seq, ct) ?? 0) + 1;
}
