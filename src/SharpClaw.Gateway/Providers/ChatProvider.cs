namespace SharpClaw.Gateway.Providers;

public sealed record ProviderChatMessage(string Role, string Content);

public sealed record ProviderChatRequest(
    string Model,
    IReadOnlyList<ProviderChatMessage> Messages,
    double? Temperature = null,
    int? MaxTokens = null,
    double? TopP = null,
    IReadOnlyList<string>? Stop = null);

/// <summary>One event in a streamed completion.</summary>
public abstract record ChatStreamEvent
{
    public sealed record Delta(string Text) : ChatStreamEvent;
    public sealed record Usage(int PromptTokens, int CompletionTokens, double? CostUsd) : ChatStreamEvent;
    public sealed record Done(string FinishReason) : ChatStreamEvent;
}

public enum ProviderErrorKind { Upstream, Timeout, Cancelled }

public sealed class ProviderException : Exception
{
    public ProviderErrorKind Kind { get; }
    public int? HttpStatus { get; }

    public ProviderException(ProviderErrorKind kind, int? httpStatus, string shortMessage)
        : base(shortMessage)
    {
        Kind = kind;
        HttpStatus = httpStatus;
    }
}

public interface IChatProvider
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderChatRequest request, CancellationToken ct);
}
