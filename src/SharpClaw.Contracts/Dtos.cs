namespace SharpClaw.Contracts;

// ----- Agents -----

public sealed record CreateAgentRequest(string Name, string Model, double DailyBudgetUsd);

public sealed record UpdateAgentRequest(string? Model, double? DailyBudgetUsd, bool? Enabled);

public sealed record AgentDto(
    Guid Id,
    string Name,
    string Model,
    double DailyBudgetUsd,
    bool Enabled,
    DateTime CreatedAt,
    double TodaySpendUsd,
    double RemainingUsd);

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

public sealed record ThreadDto(Guid Id, Guid AgentId, string? Title, DateTime CreatedAt);

public sealed record CreatedThreadResponse(ThreadDto Thread);

public sealed record ThreadListResponse(IReadOnlyList<ThreadDto> Threads);

// ----- Messages -----

public sealed record PostMessageRequest(string Content);

public sealed record MessageDto(
    Guid Id,
    string Role,
    string Content,
    string Status,
    Guid? CorrelationId,
    DateTime CreatedAt);

public sealed record MessageListResponse(IReadOnlyList<MessageDto> Messages);

/// <summary>202 response when a client queues a user message.</summary>
public sealed record QueuedMessageDto(Guid Id, string Status, DateTime CreatedAt);

public sealed record QueuedMessageResponse(QueuedMessageDto Message);

// ----- Agent inbox / reply / fail -----

public sealed record InboxMessageDto(Guid Id, Guid ThreadId, string Content, DateTime CreatedAt);

public sealed record InboxMessageResponse(InboxMessageDto Message);

public sealed record AgentReplyRequest(string Content, Guid CorrelationId);

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
