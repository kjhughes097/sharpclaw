namespace SharpClaw.Contracts;

// ----- Messages -----

/// <summary><c>Unattended</c>: nobody will answer approvals for this message (scripts, schedules).</summary>
public sealed record PostMessageRequest(string Content, bool Unattended = false);

/// <summary>
/// A thread row. <c>Kind</c> is <c>message</c> (user messages and final replies), <c>tool_request</c>
/// (role assistant, <c>ToolCalls</c> set) or <c>tool_result</c> (role tool, <c>ToolCallId</c> set).
/// Rows are ordered by <c>Seq</c>. <c>FailReason</c> is the reason code on a failed user message.
/// <c>SentByAgent</c>: on a user message another agent sent (<c>ask_agent</c>), that agent's name.
/// </summary>
public sealed record MessageDto(
    Guid Id,
    string Role,
    string Content,
    string Status,
    Guid? CorrelationId,
    DateTime CreatedAt,
    string Kind = MessageKinds.Message,
    int Seq = 0,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    bool? ToolOk = null,
    bool Untrusted = false,
    bool Truncated = false,
    string? FailReason = null,
    string? SentByUser = null,
    string? SentByAgent = null);

/// <summary><c>HasMoreBefore</c>: older rows exist before the first one returned.</summary>
public sealed record MessageListResponse(IReadOnlyList<MessageDto> Messages, bool HasMoreBefore = false);

/// <summary>202 response when a client queues a user message.</summary>
public sealed record QueuedMessageDto(Guid Id, string Status, DateTime CreatedAt);

public sealed record QueuedMessageResponse(QueuedMessageDto Message);

// ----- Agent inbox / reply / fail -----

/// <summary>
/// <c>FromAgent</c>: the asking agent's name when another agent sent it (3.5 spec), else null.
/// <c>Tainted</c>: the asking turn had read untrusted content, so this turn starts tainted.
/// </summary>
public sealed record InboxMessageDto(
    Guid Id, Guid ThreadId, string Content, DateTime CreatedAt, string? FromAgent = null, bool Tainted = false);

public sealed record InboxMessageResponse(InboxMessageDto Message);

/// <summary>
/// The agent's final reply, with the tool exchanges of the turn (in order) so the thread keeps
/// the whole conversation the model took part in.
/// </summary>
public sealed record AgentReplyRequest(string Content, Guid CorrelationId, IReadOnlyList<ToolExchangeDto>? Exchanges = null);

/// <summary>One model round that asked for tools: any text it produced, its calls, and their results.</summary>
public sealed record ToolExchangeDto(
    string? AssistantContent, IReadOnlyList<ChatToolCall> ToolCalls, IReadOnlyList<ToolResultDto> Results);

/// <summary><c>Untrusted</c>: the content may carry prompt injection and is never replayed on later turns.</summary>
public sealed record ToolResultDto(string ToolCallId, string Content, bool Ok, bool Untrusted);

public sealed record AgentReplyDto(Guid Id, DateTime CreatedAt);

public sealed record AgentReplyResponse(AgentReplyDto Message);

public sealed record FailMessageRequest(string Reason);
