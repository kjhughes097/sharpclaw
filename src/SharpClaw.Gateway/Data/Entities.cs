namespace SharpClaw.Gateway.Data;

public class Agent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public double DailyBudgetUsd { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public List<AgentToken> Tokens { get; set; } = new();
}

public class AgentToken
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string TokenHash { get; set; } = "";
    public string Prefix { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public class ConversationThread
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class Message
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    public Guid AgentId { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Status { get; set; } = "";
    public Guid? CorrelationId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailReason { get; set; }
}

public class LlmCall
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid CorrelationId { get; set; }
    public string Model { get; set; } = "";
    public string Status { get; set; } = "";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double CostUsd { get; set; }
    public bool UsageKnown { get; set; }
    public int LatencyMs { get; set; }
    public int? TtftMs { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }

    /// <summary><c>chat</c> or <c>risk_assessment</c> (smart-mode classification).</summary>
    public string Purpose { get; set; } = "chat";

    public DateTime CreatedAt { get; set; }
}
