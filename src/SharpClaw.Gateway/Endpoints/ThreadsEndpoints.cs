using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Infrastructure.Sse;

namespace SharpClaw.Gateway.Endpoints;

public static class ThreadsEndpoints
{
    private const int DefaultListLimit = 50;
    private const int MaxListLimit = 100;
    private const int DefaultMessageLimit = 100;
    private const int MaxMessageLimit = 500;

    public static void Map(RouteGroupBuilder client)
    {
        var threads = client.MapGroup("/threads").WithTags("threads");
        threads.MapPost("", CreateThread).WithName("createThread")
            .Produces<CreatedThreadResponse>(StatusCodes.Status201Created);
        threads.MapGet("", ListThreads).WithName("listThreads").Produces<ThreadListResponse>();
        threads.MapGet("/{id}", GetThread).WithName("getThread").Produces<ThreadDetailResponse>();
        threads.MapPatch("/{id}", PatchThread).WithName("updateThread").Produces<ThreadResponse>();
        threads.MapGet("/{id}/trace", TraceEndpoints.GetTrace).WithName("getThreadTrace").Produces<TraceResponse>();

        var messages = client.MapGroup("/threads/{id}").WithTags("messages");
        messages.MapGet("/messages", GetMessages).WithName("listMessages").Produces<MessageListResponse>();
        messages.MapPost("/messages", PostMessage).WithName("postMessage")
            .Produces<QueuedMessageResponse>(StatusCodes.Status202Accepted);
        messages.MapGet("/stream", StreamThread).WithName("streamThread").ProducesEventStream();

        client.MapGet("/events", StreamAll).WithName("streamEvents").WithTags("events").ProducesEventStream();
    }

    private static async Task<IResult> CreateThread(
        CreateThreadRequest? body, SharpClawDbContext db, TimeProvider time, IOptions<ContextOptions> context,
        IEventPublisher events, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
            return ApiResults.ValidationFailed("agentId is required.");
        if (body.Title is not null && TitleError(body.Title) is { } titleError)
            return ApiResults.ValidationFailed(titleError);

        var agent = await AgentQueries.ResolveAsync(db, body.AgentId, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var now = time.GetUtcNow().UtcDateTime;
        var thread = new ConversationThread
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            Title = body.Title?.Trim(),
            CreatedAt = now,
            LastMessageAt = now,
        };
        await events.WriteAsync(db, batch =>
        {
            db.Threads.Add(thread);
            batch.Add(thread.Id, SseEventNames.ThreadCreated, new ThreadCreatedEvent(thread.Id, agent.Id));
            return Task.FromResult(true);
        }, ct);

        var dto = ToDto(thread, context.Value.WindowFor(agent.ContextWindowTokens));
        return Results.Json(new CreatedThreadResponse(dto), SharpClawJson.Options, statusCode: 201);
    }

    private static ThreadDto ToDto(ConversationThread t, int windowTokens) => new(t.Id, t.AgentId, t.Title, t.CreatedAt,
        new ThreadContextDto(t.ContextDroppedTurns, t.ContextEstimatedTokens, windowTokens), t.Archived, t.LastMessageAt);

    private static string? TitleError(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length is < 1 or > ThreadActivity.MaxTitleChars
            ? $"title must be 1 to {ThreadActivity.MaxTitleChars} characters."
            : null;
    }

    /// <summary>
    /// Newest activity first with keyset paging. The badge counts are computed for the whole page
    /// with one grouped query each, never per thread.
    /// </summary>
    private static async Task<IResult> ListThreads(
        string? agentId, bool? archived, int? limit, string? cursor, SharpClawDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultListLimit, 1, MaxListLimit);
        var showArchived = archived ?? false;
        IQueryable<ConversationThread> q = db.Threads.AsNoTracking().Where(t => t.Archived == showArchived);
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var agent = await AgentQueries.ResolveAsync(db, agentId, ct);
            if (agent is null)
                return ApiResults.NotFound("Agent not found.");
            q = q.Where(t => t.AgentId == agent.Id);
        }
        if (cursor is not null)
        {
            if (!PageCursor.TryDecode(cursor, out var c))
                return ApiResults.ValidationFailed("cursor is invalid.");
            q = q.Where(t => t.LastMessageAt < c.At || (t.LastMessageAt == c.At && t.Id.CompareTo(c.Id) < 0));
        }

        var page = await q.OrderByDescending(t => t.LastMessageAt).ThenByDescending(t => t.Id).Take(take + 1)
            .Select(t => new { Thread = t, AgentName = t.Agent!.Name })
            .ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore)
            page.RemoveAt(page.Count - 1);

        var ids = page.Select(p => p.Thread.Id).ToList();
        var approvals = await db.Approvals.AsNoTracking()
            .Where(a => ids.Contains(a.ThreadId) && a.Status == ApprovalStatuses.Pending)
            .GroupBy(a => a.ThreadId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var userStates = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.ThreadId) && m.Role == Roles.User && m.Kind == MessageKinds.Message &&
                        (m.Status == MessageStatus.Pending || m.Status == MessageStatus.Claimed))
            .GroupBy(m => new { m.ThreadId, m.Status }).Select(g => new { g.Key.ThreadId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var dtos = page.Select(p => new ThreadSummaryDto(
            p.Thread.Id, p.Thread.AgentId, p.AgentName, p.Thread.Title, p.Thread.Archived, p.Thread.CreatedAt,
            p.Thread.LastMessageAt, p.Thread.LastPreview,
            approvals.GetValueOrDefault(p.Thread.Id),
            userStates.Any(s => s.ThreadId == p.Thread.Id && s.Status == MessageStatus.Claimed),
            userStates.Where(s => s.ThreadId == p.Thread.Id && s.Status == MessageStatus.Pending).Sum(s => s.Count)))
            .ToList();
        var next = hasMore && page.Count > 0
            ? new PageCursor(page[^1].Thread.LastMessageAt, page[^1].Thread.Id).Encode()
            : null;
        return Results.Json(new ThreadListResponse(dtos, next), SharpClawJson.Options);
    }

    private static async Task<IResult> GetThread(
        Guid id, SharpClawDbContext db, TimeProvider time, IOptions<ContextOptions> context, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().Include(t => t.Agent).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread?.Agent is null)
            return ApiResults.NotFound("Thread not found.");

        var spend = await AgentQueries.TodaySpendAsync(db, time, thread.AgentId, ct);
        var active = await db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == id && m.Role == Roles.User && m.Kind == MessageKinds.Message &&
                        (m.Status == MessageStatus.Pending || m.Status == MessageStatus.Claimed))
            .Select(m => new { m.Id, m.Status, m.ClaimedAt, m.Seq })
            .ToListAsync(ct);
        var claimed = active.Where(m => m.Status == MessageStatus.Claimed).MaxBy(m => m.Seq);
        var pending = await db.Approvals.AsNoTracking()
            .Where(a => a.ThreadId == id && a.Status == ApprovalStatuses.Pending)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new PendingApprovalDto(a.Id, a.CorrelationId, a.ToolName, a.Risk, a.Summary, a.Reason,
                a.CreatedAt, a.ExpiresAt))
            .ToListAsync(ct);

        var response = new ThreadDetailResponse(
            ToDto(thread, context.Value.WindowFor(thread.Agent.ContextWindowTokens)),
            AgentQueries.ToDto(thread.Agent, spend),
            claimed is null ? null : new InFlightTurnDto(claimed.Id, claimed.ClaimedAt),
            active.Count(m => m.Status == MessageStatus.Pending),
            pending);
        return Results.Json(response, SharpClawJson.Options);
    }

    private static async Task<IResult> PatchThread(
        Guid id, UpdateThreadRequest? body, SharpClawDbContext db, IOptions<ContextOptions> context,
        IEventPublisher events, CancellationToken ct)
    {
        var thread = await db.Threads.Include(t => t.Agent).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread?.Agent is null)
            return ApiResults.NotFound("Thread not found.");
        if (body is null || (body.Title is null && body.Archived is null))
            return ApiResults.ValidationFailed("title or archived is required.");
        if (body.Title is not null)
        {
            if (TitleError(body.Title) is { } titleError)
                return ApiResults.ValidationFailed(titleError);
            thread.Title = body.Title.Trim();
        }
        if (body.Archived is { } archived)
            thread.Archived = archived;

        await events.WriteAsync(db, batch =>
        {
            batch.Add(thread.Id, SseEventNames.ThreadUpdated, new ThreadUpdatedEvent(thread.Id, thread.Title, thread.Archived));
            return Task.FromResult(true);
        }, ct);
        return Results.Json(new ThreadResponse(ToDto(thread, context.Value.WindowFor(thread.Agent.ContextWindowTokens))),
            SharpClawJson.Options);
    }

    private static async Task<IResult> GetMessages(
        Guid id, Guid? after, bool? includeTools, int? limit, int? beforeSeq, int? afterSeq,
        SharpClawDbContext db, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread is null)
            return ApiResults.NotFound("Thread not found.");
        if (beforeSeq is not null && (afterSeq is not null || after is not null))
            return ApiResults.ValidationFailed("beforeSeq cannot be combined with afterSeq or after.");

        var take = Math.Clamp(limit ?? DefaultMessageLimit, 1, MaxMessageLimit);
        var (messages, hasMoreBefore) = await MessageQueries.PageAsync(
            db, id, includeTools ?? false, take, beforeSeq, afterSeq, after, ct);
        return Results.Json(new MessageListResponse(messages, hasMoreBefore), SharpClawJson.Options);
    }

    private static async Task<IResult> PostMessage(
        Guid id, PostMessageRequest? body, MessageTurns turns, IOptions<MessagesOptions> messages, CancellationToken ct)
    {
        if (body is null || string.IsNullOrEmpty(body.Content))
            return ApiResults.ValidationFailed("content is required.");
        if (body.Content.Length > messages.Value.MaxContentChars)
            return ApiResults.ValidationFailed($"content exceeds {messages.Value.MaxContentChars} characters.");

        var message = await turns.PostAsync(id, body.Content, body.Unattended, ct);
        if (message is null)
            return ApiResults.NotFound("Thread not found.");

        var dto = new QueuedMessageDto(message.Id, message.Status, message.CreatedAt);
        return Results.Json(new QueuedMessageResponse(dto), SharpClawJson.Options, statusCode: 202);
    }

    private static async Task<IResult> StreamThread(
        Guid id, HttpContext http, SharpClawDbContext db, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread is null)
            return ApiResults.NotFound("Thread not found.");

        return new EventStreamResult(id, EventStreamResult.ReadLastEventId(http.Request));
    }

    private static IResult StreamAll(HttpContext http) =>
        new EventStreamResult(null, EventStreamResult.ReadLastEventId(http.Request));
}
