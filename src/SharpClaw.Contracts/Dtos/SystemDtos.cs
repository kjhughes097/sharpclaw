namespace SharpClaw.Contracts;

// ----- System info (3.3 spec section 5.5) -----

/// <summary><c>Provider</c> is <c>OpenRouter</c> or <c>Fake</c>; <c>BudgetResetAtUtc</c> is the next UTC midnight.</summary>
public sealed record SystemInfoResponse(string Version, string Provider, DateTime UtcNow, DateTime BudgetResetAtUtc);
