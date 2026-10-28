namespace SharpClaw.Contracts;

// ----- Agents -----

public sealed record CreateAgentRequest(string Name, string Model, double DailyBudgetUsd, int? ContextWindowTokens = null);

public sealed record UpdateAgentRequest(string? Model, double? DailyBudgetUsd, bool? Enabled, int? ContextWindowTokens = null);

/// <summary>
/// <c>ContextWindowTokens</c> is the agent's override; null means the gateway default.
/// <c>LastSeenAt</c>: the agent's last authenticated request (null if never), accurate to a minute.
/// </summary>
public sealed record AgentDto(
    Guid Id,
    string Name,
    string Model,
    double DailyBudgetUsd,
    bool Enabled,
    DateTime CreatedAt,
    double TodaySpendUsd,
    double RemainingUsd,
    int? ContextWindowTokens = null,
    DateTime? LastSeenAt = null);

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
