namespace SharpClaw.Gateway.Providers;

/// <summary>A tool call the model asked for. <c>ArgumentsJson</c> is a JSON object as a string.</summary>
public sealed record ProviderToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A tool offered to the model. <c>ParametersJson</c> is a JSON Schema object, passed through.</summary>
public sealed record ProviderTool(string Name, string? Description, string? ParametersJson);

/// <summary>An image sent to the model with a user message (attachments spec section 5).</summary>
public sealed record ProviderImage(string MediaType, byte[] Data)
{
    public string DataUri() => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
}

/// <summary>
/// One conversation message. <c>Content</c> is null only for an assistant message that carries
/// <c>ToolCalls</c>; a <c>tool</c> message answers the call identified by <c>ToolCallId</c>. On a
/// user message, <c>ImageIds</c> are the attached images the agent asked for and <c>Images</c>
/// their bytes, filled in by the gateway once it has checked the ids.
/// </summary>
public sealed record ProviderChatMessage(
    string Role,
    string? Content,
    IReadOnlyList<ProviderToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    IReadOnlyList<Guid>? ImageIds = null,
    IReadOnlyList<ProviderImage>? Images = null);

public sealed record ProviderChatRequest(
    string Model,
    IReadOnlyList<ProviderChatMessage> Messages,
    double? Temperature = null,
    int? MaxTokens = null,
    double? TopP = null,
    IReadOnlyList<string>? Stop = null,
    IReadOnlyList<ProviderTool>? Tools = null,
    string? ToolChoice = null);

/// <summary>One event in a streamed completion.</summary>
public abstract record ChatStreamEvent
{
    public sealed record Delta(string Text) : ChatStreamEvent;

    /// <summary>A complete tool call. Providers accumulate any streamed fragments before emitting it.</summary>
    public sealed record ToolCall(string Id, string Name, string ArgumentsJson) : ChatStreamEvent;

    public sealed record Usage(int PromptTokens, int CompletionTokens, double? CostUsd) : ChatStreamEvent;
    public sealed record Done(string FinishReason) : ChatStreamEvent;
}

/// <summary><c>ContextLength</c>: the provider said the prompt exceeds the model's context window.</summary>
public enum ProviderErrorKind { Upstream, Timeout, Cancelled, ContextLength }

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
