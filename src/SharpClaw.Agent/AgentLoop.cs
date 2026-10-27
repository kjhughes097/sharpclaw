using Microsoft.Extensions.Logging;
using SharpClaw.Agent.Tools;
using SharpClaw.Contracts;

namespace SharpClaw.Agent;

public enum LoopOutcome { Processed, Empty, Disabled, Transient, Exit }

/// <summary>The agent's tools: what to offer the model, how to run calls, and the round limit.</summary>
public sealed record AgentTools(ToolRegistry Registry, ToolDispatcher Dispatcher, int MaxToolIterations);

public sealed class AgentLoop
{
    private readonly IGatewayClient _client;
    private readonly ISoulProvider _soul;
    private readonly AgentConfig _config;
    private readonly ILogger<AgentLoop> _logger;
    private readonly TimeProvider _time;
    private readonly AgentTools? _tools;

    private static readonly TimeSpan DisabledSleep = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <param name="tools">The agent's tools; null runs chat-only, as in 3.1.</param>
    public AgentLoop(IGatewayClient client, ISoulProvider soul, AgentConfig config,
        ILogger<AgentLoop> logger, TimeProvider time, AgentTools? tools = null)
    {
        _tools = tools;
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
            var messages = new List<ChatMessage> { new(Roles.System, _soul.Read()) };
            messages.AddRange(history);

            var (reply, failCode, exchanges) = await RunTurnAsync(messages, new TurnContext(msg.Id, msg.ThreadId), ct);
            if (reply is not null)
            {
                await _client.PostReplyAsync(msg.ThreadId, reply, msg.Id, exchanges, ct);
                _logger.LogInformation("message handled: outcome=ok duration_ms={DurationMs}",
                    (int)_time.GetElapsedTime(start).TotalMilliseconds);
            }
            else
            {
                await _client.FailAsync(msg.Id, failCode!, ct);
                _logger.LogWarning("message failed: outcome={Outcome} duration_ms={DurationMs}",
                    failCode, (int)_time.GetElapsedTime(start).TotalMilliseconds);
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

    /// <summary>
    /// One turn: call the model; while it asks for tools, run them through the dispatcher and call
    /// it again with the results. Returns the final reply (with the turn's tool exchanges, truncated
    /// for storage) or a fail code. A failed turn stores no exchanges.
    /// </summary>
    private async Task<(string? Reply, string? FailCode, IReadOnlyList<ToolExchangeDto> Exchanges)> RunTurnAsync(
        List<ChatMessage> messages, TurnContext turn, CancellationToken ct)
    {
        var tools = _tools?.Registry.Tools ?? (IReadOnlyCollection<IAgentTool>)Array.Empty<IAgentTool>();
        var maxRounds = _tools?.MaxToolIterations ?? 0;
        var exchanges = new List<ToolExchangeDto>();

        for (var round = 0; ; round++)
        {
            var chat = await _client.StreamChatAsync(messages, tools, turn.CorrelationId, turn.ThreadId, ct);
            if (!chat.Success)
                return (null, chat.FailCode, exchanges);
            if (chat.ToolCalls.Count == 0)
                return (chat.Content ?? "", null, exchanges);

            // A model that asks for tools it wasn't offered, or keeps calling tools forever, stops here.
            if (_tools is null || round >= maxRounds)
            {
                _logger.LogWarning("tool loop limit reached after {Rounds} rounds", round);
                return (null, "tool_loop_limit", exchanges);
            }

            var assistantContent = string.IsNullOrEmpty(chat.Content) ? null : chat.Content;
            messages.Add(new ChatMessage(Roles.Assistant, assistantContent, chat.ToolCalls));
            var results = new List<ToolResultDto>();
            foreach (var call in chat.ToolCalls)
            {
                var result = await _tools.Dispatcher.DispatchAsync(new ToolCallRequest(call.Id, call.Name, call.Arguments), turn, ct);
                messages.Add(new ChatMessage(Roles.Tool, result.Content, ToolCallId: call.Id));
                results.Add(new ToolResultDto(call.Id,
                    ToolHistoryLimits.TruncateText(result.Content, ToolHistoryLimits.DefaultMaxStoredResultChars, out _),
                    result.Ok, result.Untrusted));
            }
            exchanges.Add(new ToolExchangeDto(assistantContent,
                chat.ToolCalls.Select(c => c with
                {
                    Arguments = ToolHistoryLimits.TruncateArguments(
                        c.Arguments, ToolHistoryLimits.DefaultMaxStoredArgumentStringChars, out _),
                }).ToList(),
                results));
        }
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
