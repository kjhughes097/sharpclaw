using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Cli;

/// <summary>
/// Runs one chat turn: open the stream, post the message, print the reply as it streams. Prints
/// tool calls, asks the user when the agent needs approval, and reconnects with
/// <c>Last-Event-ID</c> if the stream drops, printing every character exactly once.
/// </summary>
public static class ChatRunner
{
    /// <summary>Give up if nothing arrives for this long (the clock pauses while the user is answering).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(60);

    public static async Task RunOnceAsync(
        ICliGateway gateway, Guid threadId, string content, TextWriter output, TextReader input, CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;

        // Open first so the stream's baseline precedes our message.
        var stream = await gateway.OpenThreadStreamAsync(threadId, null, ct);
        var turn = new Turn(await gateway.PostMessageAsync(threadId, content));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleTimeout);
        try
        {
            var failedFor = TimeSpan.Zero;
            var backoff = FirstBackoff;
            while (true)
            {
                var gotEvents = false;
                try
                {
                    await foreach (var ev in stream.ReadAsync(timeout.Token))
                    {
                        gotEvents = true;
                        timeout.CancelAfter(IdleTimeout);
                        if (await HandleAsync(ev, turn, gateway, threadId, output, input, timeout, ct))
                            return;
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    // Dropped connection: reconnect below.
                }
                finally
                {
                    await stream.DisposeAsync();
                }

                if (gotEvents)
                {
                    failedFor = TimeSpan.Zero;
                    backoff = FirstBackoff;
                }

                // Reconnect from the last durable event we handled.
                while (true)
                {
                    if (failedFor >= GiveUpAfter)
                        throw new CliException("Lost the connection to the gateway.");
                    await delay(backoff, timeout.Token);
                    failedFor += backoff;
                    backoff = backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
                    try
                    {
                        stream = await gateway.OpenThreadStreamAsync(threadId, turn.LastEventId, timeout.Token);
                        break;
                    }
                    catch (Exception ex) when (ex is CliException or IOException or HttpRequestException)
                    {
                        // Gateway unreachable: keep trying until the give-up time.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CliException($"Timed out after {IdleTimeout.TotalSeconds:0}s without a reply.");
        }
    }

    /// <summary>What has been handled for the posted message.</summary>
    private sealed class Turn(Guid messageId)
    {
        public Guid MessageId { get; } = messageId;
        public long? LastEventId { get; set; }
        public int Printed { get; set; }
    }

    /// <summary>Handles one event; returns true when the turn is finished.</summary>
    private static async Task<bool> HandleAsync(
        CliSseEvent ev, Turn turn, ICliGateway gateway, Guid threadId, TextWriter output, TextReader input,
        CancellationTokenSource timeout, CancellationToken ct)
    {
        if (ev.Id is { } id)
        {
            // A reconnect can replay events we already handled.
            if (ev.Event != SseEventNames.Ready && id <= turn.LastEventId)
                return false;
            turn.LastEventId = id;
        }

        switch (ev.Event)
        {
            case SseEventNames.Delta:
                var delta = Deserialize<DeltaEvent>(ev);
                if (delta is not null && delta.CorrelationId == turn.MessageId)
                {
                    await output.WriteAsync(delta.Content);
                    await output.FlushAsync(ct);
                    turn.Printed += delta.Content.Length;
                }
                return false;

            case SseEventNames.Snapshot:
                var snapshot = Deserialize<SnapshotEvent>(ev);
                if (snapshot is not null && snapshot.CorrelationId == turn.MessageId)
                    await PrintRestAsync(snapshot.Content, turn, output, ct);
                return false;

            case SseEventNames.ToolRequested:
                var tool = Deserialize<ToolRequestedEvent>(ev);
                if (tool is not null && tool.CorrelationId == turn.MessageId)
                    await output.WriteLineAsync($"\n[tool] {tool.ToolName}");
                return false;

            case SseEventNames.ApprovalRequested:
                var approval = Deserialize<ApprovalRequestedEvent>(ev);
                if (approval is not null && approval.CorrelationId == turn.MessageId)
                {
                    // Don't time out while the human is thinking.
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await AskForApprovalAsync(gateway, approval, output, input, ct);
                    timeout.CancelAfter(IdleTimeout);
                }
                return false;

            case SseEventNames.MessageCompleted:
                var completed = Deserialize<MessageCompletedEvent>(ev);
                if (completed is null || completed.CorrelationId != turn.MessageId)
                    return false;
                await PrintRestAsync(completed.Content, turn, output, ct);
                await output.WriteLineAsync();
                return true;

            case SseEventNames.Error:
                var error = Deserialize<ErrorEvent>(ev);
                if (error is not null && error.CorrelationId == turn.MessageId)
                {
                    await output.WriteLineAsync();
                    throw new CliException($"Error: {error.Message}");
                }
                return false;

            case SseEventNames.Reset:
                // The replay point was pruned: history is the source of truth now.
                return await CatchUpFromHistoryAsync(gateway, threadId, turn, output, ct);

            default:
                return false;
        }
    }

    private static async Task<bool> CatchUpFromHistoryAsync(
        ICliGateway gateway, Guid threadId, Turn turn, TextWriter output, CancellationToken ct)
    {
        var messages = await gateway.GetMessagesAsync(threadId);
        var reply = messages.FirstOrDefault(m => m.Role == Roles.Assistant && m.CorrelationId == turn.MessageId);
        if (reply is not null)
        {
            await PrintRestAsync(reply.Content, turn, output, ct);
            await output.WriteLineAsync();
            return true;
        }
        var mine = messages.FirstOrDefault(m => m.Id == turn.MessageId);
        if (mine?.Status == MessageStatus.Failed)
        {
            await output.WriteLineAsync();
            throw new CliException("Error: the message failed.");
        }
        return false;
    }

    /// <summary>Prints only the part of <paramref name="content"/> not yet printed.</summary>
    private static async Task PrintRestAsync(string content, Turn turn, TextWriter output, CancellationToken ct)
    {
        if (content.Length <= turn.Printed)
            return;
        await output.WriteAsync(content[turn.Printed..]);
        await output.FlushAsync(ct);
        turn.Printed = content.Length;
    }

    private static T? Deserialize<T>(CliSseEvent ev) => JsonSerializer.Deserialize<T>(ev.DataJson, SharpClawJson.Options);

    /// <summary>Shows the request and posts the answer. Anything but y/yes is a denial.</summary>
    private static async Task AskForApprovalAsync(
        ICliGateway gateway, ApprovalRequestedEvent approval, TextWriter output, TextReader input, CancellationToken ct)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync($"[approval needed] {approval.ToolName} ({approval.Risk} risk): {approval.Summary}");
        await output.WriteLineAsync($"  reason: {approval.Reason}");
        await output.WriteAsync("  Allow? [y/N] ");
        await output.FlushAsync(ct);

        var answer = (await input.ReadLineAsync(ct))?.Trim().ToLowerInvariant();
        var decision = answer is "y" or "yes" ? ApprovalStatuses.Approved : ApprovalStatuses.Denied;
        try
        {
            await gateway.DecideApprovalAsync(approval.ApprovalId, decision);
            await output.WriteLineAsync(decision == ApprovalStatuses.Approved ? "  approved" : "  denied");
        }
        catch (CliException ex)
        {
            await output.WriteLineAsync($"  approval expired or already decided ({ex.Message})");
        }
    }
}
