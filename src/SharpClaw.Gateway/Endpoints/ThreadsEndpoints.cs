using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

public static class ThreadsEndpoints
{
    public static void Map(RouteGroupBuilder client)
    {
        client.MapPost("/threads", CreateThread);
        client.MapGet("/threads", ListThreads);
        client.MapGet("/threads/{id}/messages", GetMessages);
        client.MapPost("/threads/{id}/messages", PostMessage);
        client.MapGet("/threads/{id}/stream", StreamThread);
        client.MapGet("/events", StreamAll);
    }

    private static async Task<IResult> CreateThread(
        CreateThreadRequest? body, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
            return ApiResults.ValidationFailed("agentId is required.");

        var agent = await AgentQueries.ResolveAsync(db, body.AgentId, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var thread = new ConversationThread
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            Title = body.Title,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Threads.Add(thread);
        await db.SaveChangesAsync(ct);

        var dto = new ThreadDto(thread.Id, thread.AgentId, thread.Title, thread.CreatedAt);
        return Results.Json(new CreatedThreadResponse(dto), SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> ListThreads(
        string? agentId, SharpClawDbContext db, CancellationToken ct)
    {
        IQueryable<ConversationThread> q = db.Threads.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var agent = await AgentQueries.ResolveAsync(db, agentId, ct);
            if (agent is null)
                return ApiResults.NotFound("Agent not found.");
            q = q.Where(t => t.AgentId == agent.Id);
        }

        var threads = await q.OrderByDescending(t => t.CreatedAt).Take(50)
            .Select(t => new ThreadDto(t.Id, t.AgentId, t.Title, t.CreatedAt))
            .ToListAsync(ct);
        return Results.Json(new ThreadListResponse(threads), SharpClawJson.Options);
    }

    private static async Task<IResult> GetMessages(
        Guid id, Guid? after, SharpClawDbContext db, CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread is null)
            return ApiResults.NotFound("Thread not found.");

        var messages = await MessageQueries.ListAsync(db, id, after, ct);
        return Results.Json(new MessageListResponse(messages), SharpClawJson.Options);
    }

    private static async Task<IResult> PostMessage(
        Guid id, PostMessageRequest? body, SharpClawDbContext db, TimeProvider time,
        IAgentInboxSignal signal, IEventPublisher events, IOptions<MessagesOptions> messages, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread is null)
            return ApiResults.NotFound("Thread not found.");

        if (body is null || string.IsNullOrEmpty(body.Content))
            return ApiResults.ValidationFailed("content is required.");
        if (body.Content.Length > messages.Value.MaxContentChars)
            return ApiResults.ValidationFailed($"content exceeds {messages.Value.MaxContentChars} characters.");

        var message = new Message
        {
            Id = Guid.CreateVersion7(),
            ThreadId = thread.Id,
            AgentId = thread.AgentId,
            Role = Roles.User,
            Content = body.Content,
            Status = MessageStatus.Pending,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);

        await events.PublishDurableAsync(thread.Id, SseEventNames.MessageCreated,
            new MessageCreatedEvent(message.Id, thread.Id, message.Id), ct);
        signal.Signal(thread.AgentId);

        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Messages");
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["agent_id"] = thread.AgentId.ToString(),
            ["thread_id"] = thread.Id.ToString(),
            ["correlation_id"] = message.Id.ToString(),
        }))
            logger.LogInformation("message queued");

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
