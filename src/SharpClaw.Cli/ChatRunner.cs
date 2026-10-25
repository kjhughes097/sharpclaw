using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Cli;

/// <summary>
/// Runs one chat turn with the correct ordering: open the stream first (no replay), then post.
/// Prints deltas, marks tool calls, and asks the user when the agent needs approval.
/// </summary>
public static class ChatRunner
{
    /// <summary>Give up if nothing arrives for this long (the clock pauses while the user is answering).</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    public static async Task RunOnceAsync(
        ICliGateway gateway, Guid threadId, string content, TextWriter output, TextReader input, CancellationToken ct)
    {
        await using var stream = await gateway.OpenThreadStreamAsync(threadId, ct);
        var messageId = await gateway.PostMessageAsync(threadId, content);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleTimeout);

        try
        {
            await foreach (var ev in stream.ReadAsync(timeout.Token))
            {
                // Any activity for our message restarts the idle clock (tool turns can be long).
                timeout.CancelAfter(IdleTimeout);

                switch (ev.Event)
                {
                    case SseEventNames.Delta:
                        var delta = JsonSerializer.Deserialize<DeltaEvent>(ev.DataJson, SharpClawJson.Options);
                        if (delta is not null && delta.CorrelationId == messageId)
                        {
                            await output.WriteAsync(delta.Content);
                            await output.FlushAsync(ct);
                        }
                        break;

                    case SseEventNames.ToolRequested:
                        var tool = JsonSerializer.Deserialize<ToolRequestedEvent>(ev.DataJson, SharpClawJson.Options);
                        if (tool is not null && tool.CorrelationId == messageId)
                            await output.WriteLineAsync($"\n[tool] {tool.ToolName}");
                        break;

                    case SseEventNames.ApprovalRequested:
                        var approval = JsonSerializer.Deserialize<ApprovalRequestedEvent>(ev.DataJson, SharpClawJson.Options);
                        if (approval is not null && approval.CorrelationId == messageId)
                        {
                            // Don't time out while the human is thinking.
                            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                            await AskForApprovalAsync(gateway, approval, output, input, ct);
                            timeout.CancelAfter(IdleTimeout);
                        }
                        break;

                    case SseEventNames.MessageCompleted:
                        var completed = JsonSerializer.Deserialize<MessageCompletedEvent>(ev.DataJson, SharpClawJson.Options);
                        if (completed is not null && completed.CorrelationId == messageId)
                        {
                            await output.WriteLineAsync();
                            return;
                        }
                        break;

                    case SseEventNames.Error:
                        var error = JsonSerializer.Deserialize<ErrorEvent>(ev.DataJson, SharpClawJson.Options);
                        if (error is not null && error.CorrelationId == messageId)
                        {
                            await output.WriteLineAsync();
                            throw new CliException($"Error: {error.Message}");
                        }
                        break;
                }
            }

            throw new CliException("Stream ended before a reply arrived.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CliException($"Timed out after {IdleTimeout.TotalSeconds:0}s without a reply.");
        }
    }

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
