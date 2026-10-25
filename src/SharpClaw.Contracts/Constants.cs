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
