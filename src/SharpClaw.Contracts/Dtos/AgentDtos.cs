namespace SharpClaw.Contracts;

// ----- Agents -----

public sealed record CreateAgentRequest(string Name, string Model, double DailyBudgetUsd, int? ContextWindowTokens = null);

public sealed record UpdateAgentRequest(string? Model, double? DailyBudgetUsd, bool? Enabled, int? ContextWindowTokens = null);

/// <summary>
/// <c>ContextWindowTokens</c> is the agent's override; null means the gateway default.
/// <c>LastSeenAt</c>: the agent's last authenticated request (null if never), accurate to a minute.
/// <c>Skills</c>: the skills the agent last reported loading, in ordinal name order (empty when
/// none); <c>SkillsReportedAt</c> is when it reported them, null if it never has (skills spec 9.3).
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
    IReadOnlyList<AgentSkillDto> Skills,
    int? ContextWindowTokens = null,
    DateTime? LastSeenAt = null,
    DateTime? SkillsReportedAt = null);

/// <summary>A skill an agent has loaded: its operator-chosen name and one-line description.</summary>
public sealed record AgentSkillDto(string Name, string Description);

/// <summary><c>PUT /agent/skills</c>: the agent's whole skill set, replacing what it reported before.</summary>
public sealed record ReportSkillsRequest(IReadOnlyList<AgentSkillDto>? Skills);

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
