namespace SharpClaw.Contracts;

/// <summary>Stable error codes used in the single error shape everywhere.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string Unsupported = "unsupported";
    public const string Unauthorized = "unauthorized";
    public const string AgentDisabled = "agent_disabled";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string BudgetExceeded = "budget_exceeded";
    public const string ProviderError = "provider_error";
    public const string ProviderTimeout = "provider_timeout";
    public const string InternalError = "internal_error";
}

/// <summary>Message roles.</summary>
public static class Roles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>Message statuses.</summary>
public static class MessageStatus
{
    public const string Pending = "pending";
    public const string Claimed = "claimed";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>LLM call statuses.</summary>
public static class LlmCallStatus
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

/// <summary>Why an LLM call was made (header <c>X-SharpClaw-Purpose</c>).</summary>
public static class LlmCallPurpose
{
    public const string Chat = "chat";
    public const string RiskAssessment = "risk_assessment";
}

/// <summary>Tool-call risk levels, in increasing order (3.2 spec section 3).</summary>
public static class RiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = new[] { Low, Medium, High, Critical };
}

/// <summary>How the agent decided on a tool call.</summary>
public static class ToolDecisions
{
    public const string Allowed = "allowed";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Blocked = "blocked";

    public static readonly IReadOnlyList<string> All = new[] { Allowed, Approved, Denied, Expired, Blocked };
}

/// <summary>Lifecycle of a <c>tool_calls</c> row. <c>requested</c> until the agent reports.</summary>
public static class ToolCallStatuses
{
    public const string Requested = "requested";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Denied = "denied";
    public const string Blocked = "blocked";
    public const string Expired = "expired";
    public const string UnknownTool = "unknown_tool";
    public const string InvalidArguments = "invalid_arguments";

    /// <summary>Statuses an agent may report (everything except <c>requested</c>).</summary>
    public static readonly IReadOnlyList<string> Reportable =
        new[] { Succeeded, Failed, Denied, Blocked, Expired, UnknownTool, InvalidArguments };
}

/// <summary>Lifecycle of an approval request (3.2 spec section 8.2).</summary>
public static class ApprovalStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Denied = "denied";
    public const string Expired = "expired";
}

/// <summary>Who decided an approval: the human, or the gateway (nobody watching, or timeout).</summary>
public static class ApprovalDecidedBy
{
    public const string User = "user";
    public const string System = "system";
}
