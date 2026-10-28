namespace SharpClaw.Contracts;

// ----- System info (3.3 spec section 5.5) -----

/// <summary>
/// <c>Provider</c> is <c>OpenRouter</c> or <c>Fake</c>; <c>BudgetResetAtUtc</c> is the next UTC midnight.
/// <c>Models</c>: the ids a thread may switch to (<c>Models:Allowed</c>), possibly empty.
/// </summary>
public sealed record SystemInfoResponse(
    string Version, string Provider, DateTime UtcNow, DateTime BudgetResetAtUtc, IReadOnlyList<string> Models);
