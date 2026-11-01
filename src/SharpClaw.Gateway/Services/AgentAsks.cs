using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services.Broker;

namespace SharpClaw.Gateway.Services;

/// <summary>
/// One agent asking another (3.5 spec section 5): policy, chain checks, queueing the question in the
/// recipient's inbox (a linked thread it owns), and waiting for its reply. The broker endpoint has
/// already checked the agent's token, that <c>ask_agent</c> is enabled for it and that the turn is
/// its own and claimed.
/// </summary>
public sealed class AgentAsks(
    SharpClawDbContext db, TimeProvider time, IEventPublisher events, IAgentInboxSignal inbox,
    AskFinishedSignal finished, MessageTurns turns, IOptions<AgentMessagingOptions> options,
    IOptions<MessagesOptions> messages, ILogger<AgentAsks> logger)
{
    /// <summary>How often a waiting ask refreshes the asker's claim, so its turn isn't requeued as stale.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound on one wait before the ask's state is read again (a missed signal costs this much).</summary>
    private static readonly TimeSpan PollSlice = TimeSpan.FromSeconds(1);

    public static class Refusals
    {
        public const string AgentUnavailable = "agent_unavailable";
        public const string HopLimit = "hop_limit";
        public const string AgentCycle = "agent_cycle";
        public const string AgentFailed = "agent_failed";
    }

    public async Task<BrokerResult> AskAsync(BrokerInvocation call, CancellationToken ct)
    {
        var o = options.Value;
        var recipientName = Args(call.Arguments, "agent")?.Trim();
        var message = Args(call.Arguments, "message");
        if (string.IsNullOrEmpty(recipientName))
            return BrokerResult.Refused(400, ErrorCodes.ValidationFailed, "agent is required.");
        if (string.IsNullOrWhiteSpace(message))
            return BrokerResult.Refused(400, ErrorCodes.ValidationFailed, "message is required.");
        if (message.Length > messages.Value.MaxContentChars)
            return BrokerResult.Refused(400, ErrorCodes.ValidationFailed,
                $"message exceeds {messages.Value.MaxContentChars} characters.");

        using var scope = LogScopes.Turn(logger, call.ThreadId, call.CorrelationId);

        // Not allowed and unknown look the same, so an agent can't probe for other agents' names.
        var lowered = recipientName.ToLowerInvariant();
        var recipient = o.MayAsk(call.Agent.Name, recipientName)
            ? await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name.ToLower() == lowered, ct)
            : null;
        if (recipient is null)
        {
            logger.LogInformation("agent ask refused: reason={Reason}", ErrorCodes.NotFound);
            return BrokerResult.Refused(404, ErrorCodes.NotFound, "Agent not found.");
        }
        if (!recipient.Enabled)
            return Refuse(recipient.Name, Refusals.AgentUnavailable, $"Agent {recipient.Name} is disabled.");

        var origin = await db.Messages.AsNoTracking().FirstAsync(m => m.Id == call.CorrelationId, ct);

        // The same tool call again (a retry after a dropped connection) waits on the first ask.
        var askId = await ExistingAskAsync(origin.Id, call.ToolCallId, ct);
        if (askId is null)
        {
            if (await ChainProblemAsync(origin, recipient.Id, call.Agent.Id, o.MaxHops, ct) is { } problem)
                return Refuse(recipient.Name, problem.Code, problem.Message);

            askId = await QueueAsync(call, origin, recipient, message, ct);
            inbox.Signal(recipient.Id);
            logger.LogInformation("agent ask queued: recipient={Recipient} hop={Hop}", recipient.Name, origin.Hop + 1);
        }

        var sw = Stopwatch.StartNew();
        var result = await WaitAsync(call, origin.Id, askId.Value, recipient.Name, ct);
        logger.LogInformation("agent ask finished: recipient={Recipient} outcome={Outcome} duration_ms={DurationMs}",
            recipient.Name, result.Outcome, sw.ElapsedMilliseconds);
        return result.Result;
    }

    private BrokerResult Refuse(string recipient, string code, string message)
    {
        logger.LogInformation("agent ask refused: recipient={Recipient} reason={Reason}", recipient, code);
        return BrokerResult.ToolError(code, message, target: recipient);
    }

    private Task<Guid?> ExistingAskAsync(Guid originId, string toolCallId, CancellationToken ct) =>
        db.Messages.AsNoTracking()
            .Where(m => m.OriginMessageId == originId && m.OriginToolCallId == toolCallId)
            .Select(m => (Guid?)m.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Hop limit, then cycles (section 5.3): the recipient must not be anywhere in the chain that
    /// led to this turn (it would be waiting for itself), and must not be waiting on an ask it sent
    /// to the sender from another chain (each would wait for the other).
    /// </summary>
    private async Task<(string Code, string Message)?> ChainProblemAsync(
        Message origin, Guid recipientId, Guid senderId, int maxHops, CancellationToken ct)
    {
        if (origin.Hop + 1 > maxHops)
            return (Refusals.HopLimit, $"This conversation already passes through {origin.Hop + 1} agents; the limit is {maxHops}.");

        var chain = new HashSet<Guid> { senderId };
        var current = origin;
        for (var i = 0; i < maxHops && current.OriginMessageId is { } up; i++)
        {
            current = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == up, ct);
            if (current is null)
                break;
            chain.Add(current.AgentId);
        }
        if (chain.Contains(recipientId))
            return (Refusals.AgentCycle, "That agent is already waiting on this conversation, so it can't answer.");

        var crossed = await db.Messages.AsNoTracking().AnyAsync(m =>
            m.AgentId == senderId && m.SenderAgentId == recipientId &&
            (m.Status == MessageStatus.Pending || m.Status == MessageStatus.Claimed), ct);
        if (crossed)
            return (Refusals.AgentCycle, "That agent is waiting for an answer from you, so it can't answer.");
        return null;
    }

    /// <summary>
    /// Queues the question in the linked thread (one per originating thread, sender and recipient;
    /// created on first use) in one transaction with its <c>message.created</c> event (section 5.2).
    /// </summary>
    private Task<Guid> QueueAsync(BrokerInvocation call, Message origin, Agent recipient, string message, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return events.WriteAsync(db, async batch =>
        {
            // Checked again inside the write lock: a concurrent retry may have queued it already.
            if (await ExistingAskAsync(origin.Id, call.ToolCallId, ct) is { } existing)
                return existing;

            var thread = await db.Threads.FirstOrDefaultAsync(t =>
                t.AgentId == recipient.Id && t.OriginAgentId == call.Agent.Id && t.OriginThreadId == call.ThreadId, ct);
            if (thread is null)
            {
                thread = new ConversationThread
                {
                    Id = Guid.CreateVersion7(),
                    AgentId = recipient.Id,
                    Title = LinkedTitle(call.Agent.Name, message),
                    CreatedAt = now,
                    LastMessageAt = now,
                    OriginThreadId = call.ThreadId,
                    OriginAgentId = call.Agent.Id,
                };
                db.Threads.Add(thread);
            }

            var ask = await MessageTurns.AddUserMessageAsync(db, batch, thread, message, now, ct, m =>
            {
                m.SenderAgentId = call.Agent.Id;
                m.OriginMessageId = origin.Id;
                m.OriginToolCallId = call.ToolCallId;
                m.Hop = origin.Hop + 1;
                m.OriginTainted = call.Tainted;
                // Q6: whoever started the chain decides whether a human can answer approvals.
                m.Unattended = origin.Unattended;
            });
            return ask.Id;
        }, ct);
    }

    internal static string LinkedTitle(string sender, string message)
    {
        var prefix = $"From {sender}: ";
        var room = Math.Max(0, ThreadActivity.MaxTitleChars - prefix.Length);
        return prefix + ThreadActivity.Collapse(message, Math.Min(room, ThreadActivity.MaxAutoTitleChars));
    }

    private sealed record Waited(BrokerResult Result, string Outcome);

    /// <summary>
    /// Waits for the ask to be answered or fail, refreshing the asker's claim as it goes (section
    /// 5.4). On timeout or cancellation an ask nobody has claimed yet is failed (section 5.5).
    /// </summary>
    private async Task<Waited> WaitAsync(BrokerInvocation call, Guid originId, Guid askId, string recipient, CancellationToken ct)
    {
        var o = options.Value;
        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(o.AskTimeoutSeconds);
        var lastTouch = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                if (await AnswerAsync(askId, recipient, ct) is { } answered)
                    return answered;

                var now = time.GetUtcNow();
                if (now >= deadline)
                {
                    await turns.FailPendingAsksAsync(originId, FailReasons.AskTimeout, CancellationToken.None, askId);
                    // It may have finished, or been claimed, just before the deadline.
                    return await AnswerAsync(askId, recipient, CancellationToken.None) ?? new Waited(
                        Of(new { ok = false, error = FailReasons.AskTimeout,
                            message = $"{recipient} did not answer within {o.AskTimeoutSeconds} seconds.",
                            threadId = await ThreadOfAsync(askId) }, recipient),
                        FailReasons.AskTimeout);
                }

                if (now - lastTouch >= HeartbeatInterval)
                {
                    await InboxRepository.TouchClaimAsync(db, call.Agent.Id, originId, now.UtcDateTime, ct);
                    lastTouch = now;
                }
                await finished.WaitAsync(askId, PollSlice, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await turns.FailPendingAsksAsync(originId, FailReasons.AskCancelled, CancellationToken.None, askId);
            throw;
        }
        finally
        {
            finished.Remove(askId);
        }
    }

    /// <summary>The ask's outcome once it is done or failed; null while it is pending or claimed.</summary>
    private async Task<Waited?> AnswerAsync(Guid askId, string recipient, CancellationToken ct)
    {
        var ask = await db.Messages.AsNoTracking()
            .Where(m => m.Id == askId)
            .Select(m => new { m.Status, m.FailReason, m.ThreadId })
            .FirstAsync(ct);
        if (ask.Status == MessageStatus.Done)
        {
            var reply = await db.Messages.AsNoTracking()
                .Where(m => m.CorrelationId == askId && m.Role == Roles.Assistant && m.Kind == MessageKinds.Message)
                .Select(m => m.Content)
                .FirstOrDefaultAsync(ct) ?? "";
            var max = options.Value.MaxReplyChars;
            var truncated = reply.Length > max;
            return new Waited(BrokerResult.Ok(new
            {
                agent = recipient,
                threadId = ask.ThreadId,
                reply = truncated ? reply[..max] : reply,
                truncated,
            }, target: recipient), "done");
        }
        if (ask.Status == MessageStatus.Failed)
        {
            var reason = ask.FailReason ?? FailReasons.AgentError;
            if (reason == FailReasons.AskTimeout)
                return new Waited(Of(new
                {
                    ok = false,
                    error = FailReasons.AskTimeout,
                    message = $"{recipient} did not pick up the question within {options.Value.AskTimeoutSeconds} seconds.",
                    threadId = ask.ThreadId,
                }, recipient), FailReasons.AskTimeout);
            return new Waited(Of(new
            {
                ok = false,
                error = Refusals.AgentFailed,
                reason,
                message = FailReasons.Describe(reason),
                threadId = ask.ThreadId,
            }, recipient), reason == FailReasons.AskCancelled ? reason : "failed");
        }
        return null;
    }

    private Task<Guid> ThreadOfAsync(Guid askId) =>
        db.Messages.AsNoTracking().Where(m => m.Id == askId).Select(m => m.ThreadId).FirstAsync(CancellationToken.None);

    private static BrokerResult Of(object payload, string recipient) =>
        BrokerResult.Of(JsonSerializer.SerializeToElement(payload, BrokerResult.Json), target: recipient);

    private static string? Args(JsonElement args, string name) =>
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
