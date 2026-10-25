using SharpClaw.Contracts;

namespace SharpClaw.Agent;

public enum InboxStatus { Message, Empty, Unauthorized, Disabled, Transient }

public sealed record InboxResult(InboxStatus Status, InboxMessageDto? Message)
{
    public static readonly InboxResult Empty = new(InboxStatus.Empty, null);
    public static readonly InboxResult Unauthorized = new(InboxStatus.Unauthorized, null);
    public static readonly InboxResult Disabled = new(InboxStatus.Disabled, null);
    public static readonly InboxResult Transient = new(InboxStatus.Transient, null);
    public static InboxResult Of(InboxMessageDto m) => new(InboxStatus.Message, m);
}

/// <summary>Outcome of streaming a completion: either accumulated content or a short fail code.</summary>
public sealed record ChatOutcome(bool Success, string? Content, string? FailCode)
{
    public static ChatOutcome Ok(string content) => new(true, content, null);
    public static ChatOutcome Fail(string code) => new(false, null, code);
}

/// <summary>Everything the agent loop needs from the gateway's agent listener.</summary>
public interface IGatewayClient
{
    Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> GetThreadHistoryAsync(Guid threadId, CancellationToken ct);
    Task<ChatOutcome> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct);
    Task PostReplyAsync(Guid threadId, string content, Guid correlationId, CancellationToken ct);
    Task FailAsync(Guid messageId, string reason, CancellationToken ct);
}

public interface ISoulProvider
{
    /// <summary>Reads the soul (system prompt) fresh each turn.</summary>
    string Read();
}
