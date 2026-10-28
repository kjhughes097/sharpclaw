using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Services;

/// <summary>How an attempt to finish a turn went.</summary>
public enum TurnOutcome { Ok, NotFound, NotClaimed, AlreadyDone }

/// <summary>
/// The lifecycle of a user turn: queued by a client (<see cref="PostAsync"/>), finished by the
/// agent with a reply (<see cref="CompleteAsync"/>) or a failure (<see cref="FailAsync"/>), or
/// abandoned and requeued (<see cref="ReleaseAsync"/>). Endpoints validate the HTTP input and
/// translate outcomes; the state changes, events, metrics and logs live here.
/// </summary>
public sealed class MessageTurns(
    SharpClawDbContext db, TimeProvider time, IEventPublisher events, ActiveTurnBuffers buffers,
    ApprovalDecisions decisions, IAgentInboxSignal inboxSignal, GatewayMetrics metrics,
    IOptions<ToolsOptions> toolsOptions, ILogger<MessageTurns> logger)
{
    /// <summary>
    /// Queues a user message on the thread and wakes its agent. Null when the thread doesn't exist.
    /// <paramref name="sentByUser"/> records who posted it (see <c>ClientAuthFilter.ActorName</c>).
    /// </summary>
    public async Task<Message?> PostAsync(
        Guid threadId, string content, bool unattended, CancellationToken ct, string? sentByUser = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var message = await events.WriteAsync(db, async batch =>
        {
            // Read inside the write transaction so the seq, preview and auto-title can't race another post.
            var thread = await db.Threads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
            if (thread is null)
                return null;
            var isFirst = !await db.Messages.AnyAsync(m => m.ThreadId == threadId && m.Role == Roles.User, ct);
            var msg = new Message
            {
                Id = Guid.CreateVersion7(),
                ThreadId = thread.Id,
                AgentId = thread.AgentId,
                Role = Roles.User,
                Kind = MessageKinds.Message,
                Content = content,
                Status = MessageStatus.Pending,
                Unattended = unattended,
                SentByUser = sentByUser,
                CreatedAt = now,
                Seq = await MessageQueries.NextSeqAsync(db, thread.Id, ct),
            };
            db.Messages.Add(msg);
            ThreadActivity.OnMessage(thread, content, now, isFirst);
            batch.Add(threadId, SseEventNames.MessageCreated, new MessageCreatedEvent(msg.Id, threadId, msg.Id));
            return msg;
        }, ct);
        if (message is null)
            return null;

        inboxSignal.Signal(message.AgentId);
        using (LogScopes.Turn(logger, threadId, message.Id, message.AgentId))
            logger.LogInformation("message queued");
        return message;
    }

    /// <summary>
    /// Stores the agent's tool exchanges and final reply, and marks the user message done, in one
    /// transaction with its <c>message.completed</c> event. The caller has already validated the
    /// content and exchanges.
    /// </summary>
    public async Task<(TurnOutcome Outcome, Message? Reply)> CompleteAsync(
        AuthenticatedAgent agent, Guid threadId, Guid correlationId, string content,
        IReadOnlyList<ToolExchangeDto> exchanges, CancellationToken ct)
    {
        var userMsg = await TurnGuard.FindOwnedAsync(db, agent.Id, correlationId, threadId, ct, track: true);
        if (userMsg is null)
            return (TurnOutcome.NotFound, null);
        if (userMsg.Status == MessageStatus.Done)
            return (TurnOutcome.AlreadyDone, null);
        if (userMsg.Status != MessageStatus.Claimed)
            return (TurnOutcome.NotClaimed, null);

        var now = time.GetUtcNow().UtcDateTime;
        Message reply;
        List<Approval> expired = new();
        try
        {
            reply = await events.WriteAsync(db, async batch =>
            {
                var seq = await MessageQueries.NextSeqAsync(db, threadId, ct);
                db.Messages.AddRange(ToolExchangeRows.Build(exchanges, userMsg, now, toolsOptions.Value, ref seq));
                var row = new Message
                {
                    Id = Guid.CreateVersion7(),
                    ThreadId = threadId,
                    AgentId = agent.Id,
                    Role = Roles.Assistant,
                    Kind = MessageKinds.Message,
                    Content = content,
                    Status = MessageStatus.Done,
                    CorrelationId = correlationId,
                    CreatedAt = now,
                    CompletedAt = now,
                    Seq = seq,
                };
                userMsg.Status = MessageStatus.Done;
                userMsg.CompletedAt = now;
                db.Messages.Add(row);
                var thread = await db.Threads.FirstAsync(t => t.Id == threadId, ct);
                ThreadActivity.OnMessage(thread, content, now, isFirstUserMessage: false);
                // Drop the streamed text before the event goes out, so a stream that connects
                // after message.completed never gets a snapshot of a finished turn.
                buffers.Remove(correlationId);
                // The turn is over: nobody is waiting for its approvals any more.
                expired = await decisions.StageExpiryForCorrelationAsync(db, batch, correlationId, ct);
                batch.Add(threadId, SseEventNames.MessageCompleted,
                    new MessageCompletedEvent(correlationId, row.Id, content, threadId));
                return row;
            }, ct);
        }
        catch (DbUpdateException)
        {
            // The unique index on a correlation id's final reply: another reply won the race.
            return (TurnOutcome.AlreadyDone, null);
        }

        await decisions.AfterDecidedAsync(db, expired, ct);
        var seconds = (now - userMsg.CreatedAt).TotalSeconds;
        metrics.RecordMessageDuration(agent.Name, seconds);
        using (LogScopes.Turn(logger, threadId, correlationId))
            logger.LogInformation("message completed: reply_id={ReplyId} duration_s={DurationSeconds}", reply.Id, seconds);
        return (TurnOutcome.Ok, reply);
    }

    /// <summary>
    /// Marks the agent's claimed message failed with a short reason code, with an <c>error</c>
    /// event carrying readable text. False if it isn't theirs or is no longer claimed (the update is
    /// conditional, so a reply or requeue that got there first wins).
    /// </summary>
    public async Task<bool> FailAsync(AuthenticatedAgent agent, Guid messageId, string reason, CancellationToken ct)
    {
        var msg = await TurnGuard.FindClaimedAsync(db, agent.Id, messageId, threadId: null, ct);
        if (msg is null)
            return false;

        var now = time.GetUtcNow().UtcDateTime;
        List<Approval> expired = new();
        var failed = await events.WriteAsync(db, async batch =>
        {
            var updated = await db.Messages
                .Where(m => m.Id == messageId && m.AgentId == agent.Id && m.Status == MessageStatus.Claimed)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(m => m.Status, MessageStatus.Failed)
                    .SetProperty(m => m.FailReason, reason)
                    .SetProperty(m => m.CompletedAt, now), ct);
            if (updated == 0)
                return false;
            buffers.Remove(messageId);
            expired = await decisions.StageExpiryForCorrelationAsync(db, batch, messageId, ct);
            batch.Add(msg.ThreadId, SseEventNames.Error,
                new ErrorEvent(msg.Id, reason, FailReasons.Describe(reason), msg.ThreadId));
            return true;
        }, ct);
        if (!failed)
            return false;

        using (LogScopes.Turn(logger, msg.ThreadId, msg.Id))
            logger.LogWarning("message failed: reason={Reason}", reason);
        await decisions.AfterDecidedAsync(db, expired, ct);
        return true;
    }

    /// <summary>
    /// A turn abandoned by its agent (requeued after a stale claim) leaves nothing in flight: drop
    /// its streamed text and expire any approval still waiting for it.
    /// </summary>
    public async Task ReleaseAsync(Guid correlationId, CancellationToken ct)
    {
        buffers.Remove(correlationId);
        await decisions.ExpireForCorrelationAsync(db, correlationId, ct);
    }
}
