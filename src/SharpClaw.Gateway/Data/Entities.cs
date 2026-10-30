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

    /// <summary>Last authenticated agent-listener request; updated at most once a minute.</summary>
    public DateTime? LastSeenAt { get; set; }

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

    /// <summary>When the last <c>kind = 'message'</c> row was added (the thread's creation time before that).</summary>
    public DateTime LastMessageAt { get; set; }

    /// <summary>A short, whitespace-collapsed slice of the last message (at most 120 characters).</summary>
    public string? LastPreview { get; set; }

    public bool Archived { get; set; }

    /// <summary>Model override for this thread (one of <c>Models:Allowed</c>); null uses the agent's model.</summary>
    public string? Model { get; set; }
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

    /// <summary>On user messages: who posted it (user name, or <c>admin_key</c>); null on older rows.</summary>
    public string? SentByUser { get; set; }

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

    /// <summary>
    /// Who decided, when <see cref="DecidedBy"/> is <c>user</c>: the signed-in user's name, or
    /// <c>admin_key</c> for an admin-key client (the CLI). Null for system decisions.
    /// </summary>
    public string? DecidedByUser { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }

    /// <summary>
    /// SHA-256 of the call's canonical arguments (<c>ToolArgsHash</c>), sent by the agent. The broker
    /// accepts the approval only for a call whose recomputed hash matches.
    /// </summary>
    public string? ArgsHash { get; set; }

    /// <summary>When the broker consumed this approval; each approval allows one broker call.</summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// What the human sees in the approval detail for a broker call, rendered by the gateway from the
    /// arguments (e.g. the command, capped at 1,000 characters). Never in lists, events or logs.
    /// </summary>
    public string? Detail { get; set; }
}

/// <summary>
/// One call through the tool broker (external tools spec section 4). Append-only, and never holds
/// arguments or results: only who, what, where (host or repository) and how it went.
/// </summary>
public class BrokerCall
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid CorrelationId { get; set; }
    public string ToolCallId { get; set; } = "";
    public string Tool { get; set; } = "";

    /// <summary>The host contacted (e.g. <c>example.com</c>, <c>api.github.com</c>), when there is one.</summary>
    public string? TargetHost { get; set; }

    /// <summary>A content-free target description, e.g. <c>owner/repo#12</c>.</summary>
    public string? Target { get; set; }

    /// <summary><c>succeeded</c>, <c>failed</c> (the tool reported an error), <c>refused</c> or <c>error</c>.</summary>
    public string Status { get; set; } = "";
    public string? ErrorCode { get; set; }

    /// <summary>Size of the result returned to the agent.</summary>
    public long Bytes { get; set; }
    public int DurationMs { get; set; }
    public Guid? ApprovalId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Where an agent's sandbox clone came from (external tools spec section 7.4), recorded by the
/// gateway from the runner's answer at clone time. Kept here rather than in the sandbox, where code
/// running as the same user could rewrite it; <c>github_create_pr</c> trusts only this record.
/// </summary>
public class SandboxClone
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }

    /// <summary>The clone's workspace-relative path, as the runner normalised it.</summary>
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";

    /// <summary><c>owner/repo</c> (GitLab: <c>group/…/repo</c>).</summary>
    public string Repo { get; set; } = "";

    /// <summary>The commit the clone checked out: the base of any change set.</summary>
    public string BaseSha { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
