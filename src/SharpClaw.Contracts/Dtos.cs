using System.Text.Json.Serialization;

namespace SharpClaw.Contracts;

// ----- Agents -----

public sealed record CreateAgentRequest(string Name, string Model, double DailyBudgetUsd, int? ContextWindowTokens = null);

public sealed record UpdateAgentRequest(string? Model, double? DailyBudgetUsd, bool? Enabled, int? ContextWindowTokens = null);

/// <summary><c>ContextWindowTokens</c> is the agent's override; null means the gateway default.</summary>
public sealed record AgentDto(
    Guid Id,
    string Name,
    string Model,
    double DailyBudgetUsd,
    bool Enabled,
    DateTime CreatedAt,
    double TodaySpendUsd,
    double RemainingUsd,
    int? ContextWindowTokens = null);

public sealed record AgentListResponse(IReadOnlyList<AgentDto> Agents);

public sealed record CreatedAgentResponse(AgentDto Agent, TokenValueDto Token);

// ----- Tokens -----

/// <summary>Returned only when a token is created; carries the one-time value.</summary>
public sealed record TokenValueDto(Guid Id, string Value, string Prefix, DateTime? ExpiresAt);

public sealed record CreateTokenRequest(DateTime? ExpiresAt);

/// <summary>Token metadata, never the hash or value.</summary>
public sealed record TokenMetaDto(
    Guid Id,
    string Prefix,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    DateTime? LastUsedAt);

public sealed record TokenListResponse(IReadOnlyList<TokenMetaDto> Tokens);

public sealed record CreatedTokenResponse(TokenValueDto Token);

// ----- Threads -----

public sealed record CreateThreadRequest(string AgentId, string? Title);

/// <summary>
/// <c>Context</c>: how the thread fit the model's window the last time the agent built its context.
/// <c>DroppedTurns</c> &gt; 0 means older messages are no longer seen by the model.
/// </summary>
public sealed record ThreadDto(Guid Id, Guid AgentId, string? Title, DateTime CreatedAt, ThreadContextDto? Context = null);

public sealed record ThreadContextDto(int DroppedTurns, int EstimatedTokens, int WindowTokens);

public sealed record CreatedThreadResponse(ThreadDto Thread);

public sealed record ThreadListResponse(IReadOnlyList<ThreadDto> Threads);

// ----- Messages -----

/// <summary><c>Unattended</c>: nobody will answer approvals for this message (scripts, schedules).</summary>
public sealed record PostMessageRequest(string Content, bool Unattended = false);

/// <summary>
/// A thread row. <c>Kind</c> is <c>message</c> (user messages and final replies), <c>tool_request</c>
/// (role assistant, <c>ToolCalls</c> set) or <c>tool_result</c> (role tool, <c>ToolCallId</c> set).
/// Rows are ordered by <c>Seq</c>.
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
    bool Truncated = false);

public sealed record MessageListResponse(IReadOnlyList<MessageDto> Messages);

/// <summary>202 response when a client queues a user message.</summary>
public sealed record QueuedMessageDto(Guid Id, string Status, DateTime CreatedAt);

public sealed record QueuedMessageResponse(QueuedMessageDto Message);

// ----- Agent inbox / reply / fail -----

public sealed record InboxMessageDto(Guid Id, Guid ThreadId, string Content, DateTime CreatedAt);

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

// ----- Audit -----

public sealed record LlmCallDto(
    Guid Id,
    Guid AgentId,
    Guid ThreadId,
    Guid CorrelationId,
    string Model,
    string Status,
    int PromptTokens,
    int CompletionTokens,
    double CostUsd,
    bool UsageKnown,
    int LatencyMs,
    int? TtftMs,
    string? ErrorCode,
    string? ErrorDetail,
    DateTime CreatedAt);

public sealed record AuditResponse(IReadOnlyList<LlmCallDto> Calls);

// ----- Tool calls (3.2) -----

/// <summary>The agent's report of what happened to a tool call the model requested.</summary>
public sealed record ToolCallReportRequest(
    Guid CorrelationId,
    string ToolCallId,
    string Status,
    string? Decision,
    string? Risk,
    bool Tainted,
    Guid? ApprovalId,
    string? ErrorCode,
    int? DurationMs);

public sealed record ToolCallDto(
    Guid Id,
    Guid AgentId,
    Guid ThreadId,
    Guid CorrelationId,
    Guid LlmCallId,
    string ToolCallId,
    string ToolName,
    string Arguments,
    string Status,
    string? Decision,
    string? Risk,
    bool Tainted,
    Guid? ApprovalId,
    string? ErrorCode,
    int? DurationMs,
    DateTime CreatedAt,
    DateTime? ReportedAt);

public sealed record ToolCallAuditResponse(IReadOnlyList<ToolCallDto> Calls);

// ----- Approvals (3.2) -----

/// <summary>The agent asks the human to allow a tool call. <c>Summary</c> never contains content.</summary>
public sealed record CreateApprovalRequest(
    Guid CorrelationId,
    Guid ThreadId,
    string ToolCallId,
    string ToolName,
    string Summary,
    string Risk,
    string Reason);

public sealed record ApprovalDto(Guid Id, string Status, string? DecidedBy, DateTime ExpiresAt);

public sealed record ApprovalResponse(ApprovalDto Approval);

/// <summary><c>Decision</c> is <c>approved</c> or <c>denied</c>.</summary>
public sealed record DecideApprovalRequest(string Decision);

// ----- Browser auth and users (pre-3.3 spec section 4) -----

public sealed record LoginRequest(string Username, string Password);

public sealed record UserDto(Guid Id, string Name);

public sealed record LoginResponse(UserDto User, string CsrfToken, DateTime ExpiresAt);

/// <summary><c>Method</c> is <c>session</c> or <c>admin_key</c>; the rest is omitted when not authenticated.</summary>
public sealed record SessionInfoResponse(
    bool Authenticated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UserDto? User = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CsrfToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? ExpiresAt = null);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record CreateUserRequest(string Name, string Password);

public sealed record SetPasswordRequest(string Password);

public sealed record UpdateUserRequest(bool? Disabled);

/// <summary>A user as shown to the admin. Never carries the password hash.</summary>
public sealed record UserAdminDto(Guid Id, string Name, bool Disabled, DateTime CreatedAt);

public sealed record UserListResponse(IReadOnlyList<UserAdminDto> Users);

public sealed record CreatedUserResponse(UserAdminDto User);

/// <summary>An approval as shown in the client queue. Never carries tool arguments.</summary>
public sealed record ApprovalDetailDto(
    Guid Id,
    string Agent,
    Guid ThreadId,
    Guid CorrelationId,
    string ToolName,
    string Summary,
    string Risk,
    string Reason,
    string Status,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    string? DecidedBy,
    DateTime? DecidedAt);

public sealed record ApprovalListResponse(IReadOnlyList<ApprovalDetailDto> Approvals);

public sealed record ApprovalDetailResponse(ApprovalDetailDto Approval);
