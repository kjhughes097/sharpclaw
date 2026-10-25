using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;

namespace SharpClaw.Agent;

public enum LoopOutcome { Processed, Empty, Disabled, Transient, Exit }

public sealed class AgentLoop
{
    private readonly IGatewayClient _client;
    private readonly ISoulProvider _soul;
    private readonly AgentConfig _config;
    private readonly ILogger<AgentLoop> _logger;
    private readonly TimeProvider _time;

    private static readonly TimeSpan DisabledSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public AgentLoop(IGatewayClient client, ISoulProvider soul, AgentConfig config,
        ILogger<AgentLoop> logger, TimeProvider time)
    {
        _client = client;
        _soul = soul;
        _config = config;
        _logger = logger;
        _time = time;
    }

    /// <summary>Runs until shutdown. Returns the process exit code (1 if the token is invalid).</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("agent started");
        var backoff = MinBackoff;

        while (!ct.IsCancellationRequested)
        {
            LoopOutcome outcome;
            try
            {
                outcome = await ProcessOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            switch (outcome)
            {
                case LoopOutcome.Exit:
                    return 1;
                case LoopOutcome.Disabled:
                    await DelayAsync(DisabledSleep, ct);
                    break;
                case LoopOutcome.Transient:
                    await DelayAsync(Jitter(backoff), ct);
                    backoff = backoff < MaxBackoff ? backoff * 2 : MaxBackoff;
                    if (backoff > MaxBackoff) backoff = MaxBackoff;
                    break;
                default:
                    backoff = MinBackoff;
                    break;
            }
        }

        _logger.LogInformation("agent stopping");
        return 0;
    }

    /// <summary>Processes a single inbox turn. Exposed for unit testing.</summary>
    public async Task<LoopOutcome> ProcessOnceAsync(CancellationToken ct)
    {
        var inbox = await _client.PollInboxAsync(_config.LongPollSeconds, ct);
        switch (inbox.Status)
        {
            case InboxStatus.Empty:
                return LoopOutcome.Empty;
            case InboxStatus.Unauthorized:
                _logger.LogError("token invalid or revoked; exiting");
                return LoopOutcome.Exit;
            case InboxStatus.Disabled:
                _logger.LogWarning("agent disabled; backing off");
                return LoopOutcome.Disabled;
            case InboxStatus.Transient:
                _logger.LogWarning("transient gateway error; backing off");
                return LoopOutcome.Transient;
        }

        var msg = inbox.Message!;
        var start = _time.GetTimestamp();
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["message_id"] = msg.Id,
            ["thread_id"] = msg.ThreadId,
        });

        try
        {
            var history = await _client.GetThreadHistoryAsync(msg.ThreadId, ct);
            var system = _soul.Read();
            var messages = new List<ChatMessage> { new(Roles.System, system) };
            messages.AddRange(history);

            var chat = await _client.StreamChatAsync(messages, msg.Id, msg.ThreadId, ct);
            if (chat.Success)
            {
                await _client.PostReplyAsync(msg.ThreadId, chat.Content!, msg.Id, ct);
                _logger.LogInformation("message handled: outcome=ok duration_ms={DurationMs}",
                    (int)_time.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                await _client.FailAsync(msg.Id, chat.FailCode!, ct);
                _logger.LogWarning("message failed: outcome={Outcome} duration_ms={DurationMs}",
                    chat.FailCode, (int)_time.GetElapsedTime(start).TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("unexpected error handling message: {Error}", ex.GetType().Name);
            await SafeFail(msg.Id, "agent_error");
        }

        return LoopOutcome.Processed;
    }

    private async Task SafeFail(Guid messageId, string reason)
    {
        try { await _client.FailAsync(messageId, reason, CancellationToken.None); }
        catch (Exception ex) { _logger.LogError("could not post fail: {Error}", ex.GetType().Name); }
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, _time, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static TimeSpan Jitter(TimeSpan baseDelay)
    {
        var jitterMs = Random.Shared.Next(0, 500);
        return baseDelay + TimeSpan.FromMilliseconds(jitterMs);
    }
}
