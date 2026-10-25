using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Endpoints;

public static class AgentEndpoints
{
    public static void Map(RouteGroupBuilder agent)
    {
        agent.MapGet("/inbox", Inbox);
        agent.MapGet("/threads/{id}/messages", ThreadHistory);
        agent.MapPost("/threads/{id}/messages", Reply);
        agent.MapPost("/messages/{id}/fail", Fail);
    }

    private static async Task<IResult> Inbox(
        HttpContext http, int? wait, SharpClawDbContext db, TimeProvider time,
        IAgentInboxSignal signal, IOptions<InboxOptions> inboxOpt, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var opt = inboxOpt.Value;
        var waitSeconds = Math.Clamp(wait ?? 30, 0, opt.MaxWaitSeconds);

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var threshold = now.AddSeconds(-opt.ClaimTimeoutSeconds);
            await InboxRepository.RequeueStaleAsync(db, agent.Id, threshold, ct);

            var claimed = await InboxRepository.ClaimNextAsync(db, agent.Id, now, ct);
            if (claimed is not null)
                return Results.Json(new InboxMessageResponse(claimed), SharpClawJson.Options);

            var remaining = TimeSpan.FromSeconds(waitSeconds) - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return Results.StatusCode(204);

            var slice = remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
            try
            {
                await signal.WaitAsync(agent.Id, slice, ct);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(204);
            }
        }
    }

    private static async Task<IResult> ThreadHistory(
        Guid id, HttpContext http, SharpClawDbContext db, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread is null || thread.AgentId != agent.Id)
            return ApiResults.NotFound("Thread not found.");

        var messages = await MessageQueries.ListAsync(db, id, null, ct);
        return Results.Json(new MessageListResponse(messages), SharpClawJson.Options);
    }

    private static async Task<IResult> Reply(
        Guid id, AgentReplyRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IThreadEventBus bus, GatewayMetrics metrics, IOptions<MessagesOptions> messages, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        if (body is null || string.IsNullOrEmpty(body.Content))
            return ApiResults.ValidationFailed("content is required.");
        if (body.Content.Length > messages.Value.MaxContentChars)
            return ApiResults.ValidationFailed($"content exceeds {messages.Value.MaxContentChars} characters.");

        var userMsg = await db.Messages.FirstOrDefaultAsync(m => m.Id == body.CorrelationId, ct);
        if (userMsg is null || userMsg.AgentId != agent.Id || userMsg.ThreadId != id || userMsg.Role != Roles.User)
            return ApiResults.NotFound("Correlation message not found.");
        if (userMsg.Status == MessageStatus.Done)
            return ApiResults.Conflict("A reply already exists for this message.");
        if (userMsg.Status != MessageStatus.Claimed)
            return ApiResults.NotFound("Correlation message is not claimed.");

        var now = time.GetUtcNow().UtcDateTime;
        var assistant = new Message
        {
            Id = Guid.CreateVersion7(),
            ThreadId = id,
            AgentId = agent.Id,
            Role = Roles.Assistant,
            Content = body.Content,
            Status = MessageStatus.Done,
            CorrelationId = body.CorrelationId,
            CreatedAt = now,
            CompletedAt = now,
        };
        userMsg.Status = MessageStatus.Done;
        userMsg.CompletedAt = now;
        db.Messages.Add(assistant);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ApiResults.Conflict("A reply already exists for this message.");
        }

        Publish(bus, id, SseEventNames.MessageCompleted,
            new MessageCompletedEvent(body.CorrelationId, assistant.Id, body.Content));
        metrics.RecordMessageDuration(agent.Name, (now - userMsg.CreatedAt).TotalSeconds);

        return Results.Json(new AgentReplyResponse(new AgentReplyDto(assistant.Id, now)),
            SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> Fail(
        Guid id, FailMessageRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IThreadEventBus bus, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var reason = body?.Reason;
        if (string.IsNullOrWhiteSpace(reason))
            return ApiResults.ValidationFailed("reason is required.");
        if (reason.Length > 100)
            return ApiResults.ValidationFailed("reason must be at most 100 characters.");

        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (msg is null || msg.AgentId != agent.Id || msg.Role != Roles.User || msg.Status != MessageStatus.Claimed)
            return ApiResults.NotFound("Message not found.");

        var now = time.GetUtcNow().UtcDateTime;
        msg.Status = MessageStatus.Failed;
        msg.FailReason = reason;
        msg.CompletedAt = now;
        await db.SaveChangesAsync(ct);

        Publish(bus, msg.ThreadId, SseEventNames.Error, new ErrorEvent(msg.Id, reason, reason));
        return Results.StatusCode(204);
    }

    private static void Publish<T>(IThreadEventBus bus, Guid threadId, string eventName, T payload)
    {
        var json = JsonSerializer.Serialize(payload, SharpClawJson.Options);
        bus.Publish(threadId, new ThreadSseEvent(eventName, json));
    }
}
