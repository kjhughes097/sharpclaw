using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Cli;

/// <summary>
/// Runs one chat turn with the correct ordering: open the stream first (no replay), then post.
/// </summary>
public static class ChatRunner
{
    public static async Task RunOnceAsync(
        ICliGateway gateway, Guid threadId, string content, TextWriter output, CancellationToken ct)
    {
        await using var stream = await gateway.OpenThreadStreamAsync(threadId, ct);
        var messageId = await gateway.PostMessageAsync(threadId, content);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));

        try
        {
            await foreach (var ev in stream.ReadAsync(timeout.Token))
            {
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
            throw new CliException("Timed out after 120s waiting for a reply.");
        }
    }
}
