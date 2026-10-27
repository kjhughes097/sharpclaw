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
        agent.MapGet("/threads/{id}/context", Context);
        agent.MapPost("/threads/{id}/messages", Reply);
        agent.MapPost("/messages/{id}/fail", Fail);
    }

    private static async Task<IResult> Inbox(
        HttpContext http, int? wait, SharpClawDbContext db, TimeProvider time,
        IAgentInboxSignal signal, ActiveTurnBuffers buffers, ApprovalDecisions decisions, IOptions<InboxOptions> inboxOpt,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Inbox");
        var agent = AuthContext.Agent(http);
        var opt = inboxOpt.Value;
        var waitSeconds = Math.Clamp(wait ?? 30, 0, opt.MaxWaitSeconds);

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var threshold = now.AddSeconds(-opt.ClaimTimeoutSeconds);
            var requeued = await InboxRepository.RequeueStaleAsync(db, agent.Id, threshold, ct);
            foreach (var stale in requeued)
            {
                buffers.Remove(stale);
                await decisions.ExpireForCorrelationAsync(db, stale, ct);
            }

            var claimed = await InboxRepository.ClaimNextAsync(db, agent.Id, now, ct);
            if (claimed is not null)
            {
                using (logger.BeginScope(new Dictionary<string, object>
                {
                    ["thread_id"] = claimed.ThreadId.ToString(),
                    ["correlation_id"] = claimed.Id.ToString(),
                }))
                    logger.LogInformation("message claimed");
                return Results.Json(new InboxMessageResponse(claimed), SharpClawJson.Options);
            }

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

        var messages = await MessageQueries.ListAsync(db, id, null, includeTools: true, ct);
        return Results.Json(new MessageListResponse(messages), SharpClawJson.Options);
    }

    /// <summary>The model-ready history that fits the agent's context window (pre-3.3 spec section 6.4).</summary>
    private static async Task<IResult> Context(
        Guid id, int? reserveTokens, HttpContext http, SharpClawDbContext db, IContextSummariser summariser,
        IOptions<ContextOptions> contextOptions, GatewayMetrics metrics, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var reserve = reserveTokens ?? 0;
        if (reserve < 0)
            return ApiResults.ValidationFailed("reserveTokens must be >= 0.");
        var thread = await db.Threads.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (thread is null || thread.AgentId != agent.Id)
            return ApiResults.NotFound("Thread not found.");

        var rows = await db.Messages.AsNoTracking().Where(m => m.ThreadId == id).OrderBy(m => m.Seq).ToListAsync(ct);
        var users = rows.Where(r => r.Role == Roles.User && r.Kind == MessageKinds.Message).ToList();
        // The turn being processed: the agent's claimed message, else the latest one.
        var current = users.LastOrDefault(r => r.Status == MessageStatus.Claimed) ?? users.LastOrDefault();
        var options = contextOptions.Value;
        var window = options.WindowFor(agent.ContextWindowTokens);
        if (current is null)
            return Results.Json(new ContextResponse(Array.Empty<ContextMessage>(), false, 0, 0, window), SharpClawJson.Options);

        var result = await new ContextBuilder(options, summariser).BuildAsync(id, rows, current.Id, window, reserve, ct);
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Context");
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["thread_id"] = id.ToString(),
            ["correlation_id"] = current.Id.ToString(),
        });
        if (result.TooLarge)
        {
            metrics.RecordContextRejection(agent.Name);
            logger.LogWarning("context rejected: estimated_tokens={EstimatedTokens} window_tokens={WindowTokens} reserve_tokens={ReserveTokens}",
                result.EstimatedTokens, window, reserve);
            return ApiResults.Error(400, ErrorCodes.ContextTooLarge,
                "The current message does not fit in the agent's context window.");
        }

        await db.Threads.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.ContextDroppedTurns, result.DroppedTurns)
            .SetProperty(t => t.ContextEstimatedTokens, result.EstimatedTokens), ct);
        metrics.RecordContextEstimate(agent.Name, result.EstimatedTokens);
        if (result.DroppedTurns > 0)
        {
            metrics.RecordContextTruncation(agent.Name);
            logger.LogInformation("context truncated: dropped_turns={DroppedTurns} estimated_tokens={EstimatedTokens} window_tokens={WindowTokens}",
                result.DroppedTurns, result.EstimatedTokens, window);
        }

        return Results.Json(new ContextResponse(result.Messages, result.DroppedTurns > 0, result.DroppedTurns,
            result.EstimatedTokens, window), SharpClawJson.Options);
    }

    private static async Task<IResult> Reply(
        Guid id, AgentReplyRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IEventPublisher events, ActiveTurnBuffers buffers, ApprovalDecisions decisions, GatewayMetrics metrics,
        IOptions<MessagesOptions> messages, IOptions<ToolsOptions> toolsOptions, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Messages");
        if (body is null || string.IsNullOrEmpty(body.Content))
            return ApiResults.ValidationFailed("content is required.");
        if (body.Content.Length > messages.Value.MaxContentChars)
            return ApiResults.ValidationFailed($"content exceeds {messages.Value.MaxContentChars} characters.");
        var exchanges = body.Exchanges ?? Array.Empty<ToolExchangeDto>();
        if (ToolExchangeRows.Validate(exchanges, toolsOptions.Value) is { } invalid)
            return ApiResults.ValidationFailed(invalid);

        var userMsg = await db.Messages.FirstOrDefaultAsync(m => m.Id == body.CorrelationId, ct);
        if (userMsg is null || userMsg.AgentId != agent.Id || userMsg.ThreadId != id || userMsg.Role != Roles.User)
            return ApiResults.NotFound("Correlation message not found.");
        if (userMsg.Status == MessageStatus.Done)
            return ApiResults.Conflict("A reply already exists for this message.");
        if (userMsg.Status != MessageStatus.Claimed)
            return ApiResults.NotFound("Correlation message is not claimed.");

        var now = time.GetUtcNow().UtcDateTime;
        Message assistant;
        // Exchanges, the final reply and the user message's status change commit together or not at all.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var seq = await MessageQueries.NextSeqAsync(db, id, ct);
            db.Messages.AddRange(ToolExchangeRows.Build(exchanges, userMsg, now, toolsOptions.Value, ref seq));
            assistant = new Message
            {
                Id = Guid.CreateVersion7(),
                ThreadId = id,
                AgentId = agent.Id,
                Role = Roles.Assistant,
                Kind = MessageKinds.Message,
                Content = body.Content,
                Status = MessageStatus.Done,
                CorrelationId = body.CorrelationId,
                CreatedAt = now,
                CompletedAt = now,
                Seq = seq,
            };
            userMsg.Status = MessageStatus.Done;
            userMsg.CompletedAt = now;
            db.Messages.Add(assistant);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                return ApiResults.Conflict("A reply already exists for this message.");
            }
        }

        buffers.Remove(body.CorrelationId);
        await decisions.ExpireForCorrelationAsync(db, body.CorrelationId, ct);
        await events.PublishDurableAsync(id, SseEventNames.MessageCompleted,
            new MessageCompletedEvent(body.CorrelationId, assistant.Id, body.Content), ct);
        var messageSeconds = (now - userMsg.CreatedAt).TotalSeconds;
        metrics.RecordMessageDuration(agent.Name, messageSeconds);
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["thread_id"] = id.ToString(),
            ["correlation_id"] = body.CorrelationId.ToString(),
        }))
            logger.LogInformation("message completed: reply_id={ReplyId} duration_s={DurationSeconds}",
                assistant.Id, messageSeconds);

        return Results.Json(new AgentReplyResponse(new AgentReplyDto(assistant.Id, now)),
            SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> Fail(
        Guid id, FailMessageRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IEventPublisher events, ActiveTurnBuffers buffers, ApprovalDecisions decisions, ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Messages");
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

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["thread_id"] = msg.ThreadId.ToString(),
            ["correlation_id"] = msg.Id.ToString(),
        }))
            logger.LogWarning("message failed: reason={Reason}", reason);

        buffers.Remove(msg.Id);
        await decisions.ExpireForCorrelationAsync(db, msg.Id, ct);
        await events.PublishDurableAsync(msg.ThreadId, SseEventNames.Error, new ErrorEvent(msg.Id, reason, reason), ct);
        return Results.StatusCode(204);
    }
}
