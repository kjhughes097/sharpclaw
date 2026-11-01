using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// <c>GET /api/activity?agent=&amp;limit=&amp;before=</c> (3.3.1 spec section 3.2): the message feed on
/// the Overview and agent tabs. Only <c>kind = 'message'</c> rows; never tool requests or results.
/// </summary>
public static class ActivityEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    /// <summary>Shown as the human side when a user message has no recorded sender (older rows).</summary>
    public const string UnknownUser = "user";

    public static void Map(RouteGroupBuilder client)
    {
        client.MapGet("/activity", List).WithName("listActivity").WithTags("activity").Produces<ActivityResponse>();
    }

    private static async Task<IResult> List(
        string? agent, int? limit, string? before, SharpClawDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var q = db.Messages.AsNoTracking()
            .Where(m => m.Kind == MessageKinds.Message && (m.Role == Roles.User || m.Role == Roles.Assistant));

        if (!string.IsNullOrWhiteSpace(agent))
        {
            var resolved = await AgentQueries.ResolveAsync(db, agent, ct);
            if (resolved is null)
                return ApiResults.NotFound("Agent not found.");
            q = q.Where(m => m.AgentId == resolved.Id);
        }
        if (before is not null)
        {
            if (!PageCursor.TryDecode(before, out var c))
                return ApiResults.ValidationFailed("before is invalid.");
            q = q.Where(m => m.CreatedAt < c.At || (m.CreatedAt == c.At && m.Id.CompareTo(c.Id) < 0));
        }

        var page = await q.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1)
            .Select(m => new
            {
                m.Id, m.ThreadId, m.AgentId, m.Role, m.Status, m.CreatedAt, m.Content, m.SentByUser, m.CorrelationId,
                SenderAgentName = db.Agents.Where(a => a.Id == m.SenderAgentId).Select(a => a.Name).FirstOrDefault(),
                ThreadTitle = db.Threads.Where(t => t.Id == m.ThreadId).Select(t => t.Title).FirstOrDefault(),
                AgentName = db.Agents.Where(a => a.Id == m.AgentId).Select(a => a.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        // A reply goes back to whoever sent its prompt: the user message whose id is the correlation id.
        var promptIds = page.Where(m => m.Role == Roles.Assistant && m.CorrelationId is not null)
            .Select(m => m.CorrelationId!.Value).Distinct().ToList();
        // An agent's question (3.5 spec) was sent by that agent, so the reply goes back to it.
        var senders = await db.Messages.AsNoTracking()
            .Where(m => promptIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id,
                Sender = db.Agents.Where(a => a.Id == m.SenderAgentId).Select(a => a.Name).FirstOrDefault() ?? m.SentByUser,
            })
            .ToDictionaryAsync(m => m.Id, m => m.Sender, ct);

        var items = page.Select(m =>
        {
            var agentName = m.AgentName ?? "";
            var (from, to) = m.Role == Roles.User
                ? (m.SenderAgentName ?? m.SentByUser ?? UnknownUser, agentName)
                : (agentName, (m.CorrelationId is { } cid ? senders.GetValueOrDefault(cid) : null) ?? UnknownUser);
            return new ActivityItemDto(m.Id, m.ThreadId, m.ThreadTitle, m.AgentId, agentName, m.Role, from, to,
                m.Status, m.CreatedAt, ThreadActivity.Collapse(m.Content, ThreadActivity.MaxPreviewChars));
        }).ToList();

        var next = hasMore && page.Count > 0 ? new PageCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return Results.Json(new ActivityResponse(items, next), SharpClawJson.Options);
    }
}
