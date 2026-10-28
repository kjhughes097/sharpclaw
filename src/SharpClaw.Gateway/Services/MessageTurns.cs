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
    /// <summary>Queues a user message on the thread and wakes its agent. Null when the thread doesn't exist.</summary>
    public async Task<Message?> PostAsync(Guid threadId, string content, bool unattended, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        Message message;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Loaded inside the write transaction so the preview and auto-title can't race another post.
            var thread = await db.Threads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
            if (thread is null)
                return null;
            var isFirst = !await db.Messages.AnyAsync(m => m.ThreadId == threadId && m.Role == Roles.User, ct);
            message = new Message
            {
                Id = Guid.CreateVersion7(),
                ThreadId = thread.Id,
                AgentId = thread.AgentId,
                Role = Roles.User,
                Kind = MessageKinds.Message,
                Content = content,
                Status = MessageStatus.Pending,
                Unattended = unattended,
                CreatedAt = now,
                Seq = await MessageQueries.NextSeqAsync(db, thread.Id, ct),
            };
            db.Messages.Add(message);
            ThreadActivity.OnMessage(thread, content, now, isFirst);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await events.PublishDurableAsync(threadId, SseEventNames.MessageCreated,
            new MessageCreatedEvent(message.Id, threadId, message.Id), ct);
        inboxSignal.Signal(message.AgentId);

        using (LogScopes.Turn(logger, threadId, message.Id, message.AgentId))
            logger.LogInformation("message queued");
        return message;
    }

    /// <summary>
    /// Stores the agent's tool exchanges and final reply, and marks the user message done, in one
    /// transaction. The caller has already validated the content and exchanges.
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
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var seq = await MessageQueries.NextSeqAsync(db, threadId, ct);
            db.Messages.AddRange(ToolExchangeRows.Build(exchanges, userMsg, now, toolsOptions.Value, ref seq));
            reply = new Message
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
            db.Messages.Add(reply);
            var thread = await db.Threads.FirstAsync(t => t.Id == threadId, ct);
            ThreadActivity.OnMessage(thread, content, now, isFirstUserMessage: false);

            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The unique index on a correlation id's final reply: another reply won the race.
                return (TurnOutcome.AlreadyDone, null);
            }
        }

        await ReleaseAsync(correlationId, ct);
        await events.PublishDurableAsync(threadId, SseEventNames.MessageCompleted,
            new MessageCompletedEvent(correlationId, reply.Id, content, threadId), ct);
        var seconds = (now - userMsg.CreatedAt).TotalSeconds;
        metrics.RecordMessageDuration(agent.Name, seconds);
        using (LogScopes.Turn(logger, threadId, correlationId))
            logger.LogInformation("message completed: reply_id={ReplyId} duration_s={DurationSeconds}", reply.Id, seconds);
        return (TurnOutcome.Ok, reply);
    }

    /// <summary>Marks the agent's claimed message failed with a short reason code. False if it isn't theirs or isn't claimed.</summary>
    public async Task<bool> FailAsync(AuthenticatedAgent agent, Guid messageId, string reason, CancellationToken ct)
    {
        var msg = await TurnGuard.FindClaimedAsync(db, agent.Id, messageId, threadId: null, ct, track: true);
        if (msg is null)
            return false;

        msg.Status = MessageStatus.Failed;
        msg.FailReason = reason;
        msg.CompletedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        using (LogScopes.Turn(logger, msg.ThreadId, msg.Id))
            logger.LogWarning("message failed: reason={Reason}", reason);

        await ReleaseAsync(msg.Id, ct);
        await events.PublishDurableAsync(msg.ThreadId, SseEventNames.Error,
            new ErrorEvent(msg.Id, reason, reason, msg.ThreadId), ct);
        return true;
    }

    /// <summary>
    /// A turn that ended (reply, fail or requeue) leaves nothing in flight: drop its streamed text
    /// and expire any approval still waiting for it.
    /// </summary>
    public async Task ReleaseAsync(Guid correlationId, CancellationToken ct)
    {
        buffers.Remove(correlationId);
        await decisions.ExpireForCorrelationAsync(db, correlationId, ct);
    }
}
