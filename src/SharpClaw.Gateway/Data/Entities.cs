namespace SharpClaw.Gateway.Data;

public class Agent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public double DailyBudgetUsd { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    /// <summary>Model context window override; null uses <c>Context:DefaultWindowTokens</c>.</summary>
    public int? ContextWindowTokens { get; set; }

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

    /// <summary>Turns left out of the model's context the last time it was built.</summary>
    public int ContextDroppedTurns { get; set; }
    public int ContextEstimatedTokens { get; set; }
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

    /// <summary>On user messages: nobody will answer approvals, so they are denied at once.</summary>
    public bool Unattended { get; set; }

    /// <summary>Per-thread order, assigned at insert as max+1 inside the write transaction.</summary>
    public int Seq { get; set; }

    /// <summary><c>message</c>, <c>tool_request</c> or <c>tool_result</c> (see <c>MessageKinds</c>).</summary>
    public string Kind { get; set; } = "message";

    /// <summary>On <c>tool_request</c> rows: JSON array of <c>{ id, name, arguments }</c>.</summary>
    public string? ToolCalls { get; set; }

    public string? ToolCallId { get; set; }
    public bool? ToolOk { get; set; }
    public bool Untrusted { get; set; }
    public bool Truncated { get; set; }
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

    /// <summary>The gateway's prompt estimate, kept beside the provider's count to tune the heuristic.</summary>
    public int? EstimatedPromptTokens { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One tool call the model requested (3.2). The gateway inserts it from the LLM stream; the agent
/// later reports the outcome. Named <c>ToolCallRecord</c> to avoid clashing with the stream event.
/// </summary>
public class ToolCallRecord
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid LlmCallId { get; set; }
    public string ToolCallId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Decision { get; set; }
    public string? Risk { get; set; }
    public bool Tainted { get; set; }
    public Guid? ApprovalId { get; set; }
    public string? ErrorCode { get; set; }
    public int? DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReportedAt { get; set; }
}

/// <summary>A browser user (pre-3.3 spec section 4). The password is stored only as a PBKDF2 hash.</summary>
public class User
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A browser session. The cookie value is stored only as its SHA-256 hex.</summary>
public class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public string TokenHash { get; set; } = "";
    public string CsrfToken { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>A durable SSE event (pre-3.3 §8). <c>Id</c> is the SSE event id: global and monotonic.</summary>
public class ThreadEvent
{
    public long Id { get; set; }
    public Guid ThreadId { get; set; }
    public string Type { get; set; } = "";
    public string Data { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Single row: the highest event id removed by retention pruning.</summary>
public class EventLogState
{
    public int Id { get; set; }
    public long PrunedThrough { get; set; }
}

/// <summary>A request for the human to allow a tool call (3.2).</summary>
public class Approval
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid CorrelationId { get; set; }
    public string ToolCallId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "";
    public string? DecidedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
}
