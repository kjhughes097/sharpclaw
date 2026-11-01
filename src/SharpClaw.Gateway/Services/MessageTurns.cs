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
    ApprovalDecisions decisions, IAgentInboxSignal inboxSignal, AskFinishedSignal askFinished, GatewayMetrics metrics,
    IOptions<ToolsOptions> toolsOptions, ILogger<MessageTurns> logger)
{
    /// <summary>
    /// Queues a user message on the thread and wakes its agent. Null when the thread doesn't exist.
    /// <paramref name="sentByUser"/> records who posted it (see <c>ClientAuthFilter.ActorName</c>).
    /// <paramref name="attachments"/> are stored in the same transaction as the message.
    /// </summary>
    public async Task<Message?> PostAsync(
        Guid threadId, string content, bool unattended, CancellationToken ct, string? sentByUser = null,
        IReadOnlyList<ValidatedAttachment>? attachments = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var message = await events.WriteAsync(db, async batch =>
        {
            // Read inside the write transaction so the seq, preview and auto-title can't race another post.
            var thread = await db.Threads.FirstOrDefaultAsync(t => t.Id == threadId, ct);
            if (thread is null)
                return null;
            var msg = await AddUserMessageAsync(db, batch, thread, content, now, ct, m =>
            {
                m.Unattended = unattended;
                m.SentByUser = sentByUser;
            }, attachments?.Select(a => a.FileName).ToList());
            foreach (var (a, position) in (attachments ?? []).Select((a, i) => (a, i)))
                db.MessageAttachments.Add(new MessageAttachment
                {
                    Id = Guid.CreateVersion7(),
                    MessageId = msg.Id,
                    ThreadId = thread.Id,
                    Position = position,
                    Kind = a.Kind,
                    FileName = a.FileName,
                    MediaType = a.MediaType,
                    SizeBytes = a.Data.Length,
                    Data = a.Data,
                    CreatedAt = now,
                });
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
    /// Adds a <c>pending</c> user message to <paramref name="thread"/> with its <c>message.created</c>
    /// event. Call inside <see cref="IEventPublisher.WriteAsync{T}"/>, so the seq, preview and
    /// auto-title can't race another post. <paramref name="init"/> sets who sent it.
    /// <paramref name="attachmentNames"/> stand in for the preview and title when the text is empty.
    /// </summary>
    internal static async Task<Message> AddUserMessageAsync(
        SharpClawDbContext db, EventBatch batch, ConversationThread thread, string content, DateTime now,
        CancellationToken ct, Action<Message> init, IReadOnlyList<string>? attachmentNames = null)
    {
        var isFirst = !await db.Messages.AnyAsync(m => m.ThreadId == thread.Id && m.Role == Roles.User, ct);
        var msg = new Message
        {
            Id = Guid.CreateVersion7(),
            ThreadId = thread.Id,
            AgentId = thread.AgentId,
            Role = Roles.User,
            Kind = MessageKinds.Message,
            Content = content,
            Status = MessageStatus.Pending,
            CreatedAt = now,
            Seq = await MessageQueries.NextSeqAsync(db, thread.Id, ct),
        };
        init(msg);
        db.Messages.Add(msg);
        ThreadActivity.OnMessage(thread,
            string.IsNullOrWhiteSpace(content) && attachmentNames is { Count: > 0 }
                ? "Attached: " + string.Join(", ", attachmentNames)
                : content,
            now, isFirst);
        batch.Add(thread.Id, SseEventNames.MessageCreated, new MessageCreatedEvent(msg.Id, thread.Id, msg.Id));
        return msg;
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
        if (userMsg.SenderAgentId is not null)
            askFinished.SignalIfWaiting(userMsg.Id);
        // Asks this turn made that nobody picked up are no longer wanted.
        await FailPendingAsksAsync(correlationId, FailReasons.AskCancelled, ct);
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
        if (msg.SenderAgentId is not null)
            askFinished.SignalIfWaiting(msg.Id);
        await FailPendingAsksAsync(msg.Id, FailReasons.AskCancelled, ct);
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
        await FailPendingAsksAsync(correlationId, FailReasons.AskCancelled, ct);
    }

    /// <summary>
    /// Fails the asks made by turn <paramref name="originMessageId"/> (only <paramref name="askId"/>
    /// when given) that are still <c>pending</c>, with an <c>error</c> event on each linked thread
    /// (3.5 spec section 5.5). An ask the recipient has already claimed is left to finish. Returns
    /// the ids it failed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> FailPendingAsksAsync(
        Guid originMessageId, string reason, CancellationToken ct, Guid? askId = null)
    {
        // Cheap check first: most turns ask nobody, and this runs at the end of every turn.
        var asks = await db.Messages.AsNoTracking()
            .Where(m => m.OriginMessageId == originMessageId && m.Status == MessageStatus.Pending &&
                        (askId == null || m.Id == askId))
            .Select(m => new { m.Id, m.ThreadId })
            .ToListAsync(ct);
        if (asks.Count == 0)
            return [];

        var now = time.GetUtcNow().UtcDateTime;
        var failed = await events.WriteAsync(db, async batch =>
        {
            var done = new List<Guid>();
            foreach (var ask in asks)
            {
                // Conditional, so a claim that got there first wins.
                var updated = await db.Messages
                    .Where(m => m.Id == ask.Id && m.Status == MessageStatus.Pending)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(m => m.Status, MessageStatus.Failed)
                        .SetProperty(m => m.FailReason, reason)
                        .SetProperty(m => m.CompletedAt, now), ct);
                if (updated == 0)
                    continue;
                batch.Add(ask.ThreadId, SseEventNames.Error,
                    new ErrorEvent(ask.Id, reason, FailReasons.Describe(reason), ask.ThreadId));
                done.Add(ask.Id);
            }
            return done;
        }, ct);

        foreach (var id in failed)
        {
            askFinished.SignalIfWaiting(id);
            using (LogScopes.Turn(logger, asks.First(a => a.Id == id).ThreadId, id))
                logger.LogInformation("agent ask dropped: reason={Reason}", reason);
        }
        return failed;
    }
}
