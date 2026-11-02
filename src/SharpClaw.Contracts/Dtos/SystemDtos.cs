namespace SharpClaw.Contracts;

// ----- System info (3.3 spec section 5.5) -----

/// <summary>
/// <c>Provider</c> is <c>OpenRouter</c> or <c>Fake</c>; <c>BudgetResetAtUtc</c> is the next UTC midnight.
/// <c>Models</c>: the ids a thread may switch to (<c>Models:Allowed</c>), possibly empty.
/// <c>TranscriptionMaxSeconds</c>: the longest voice recording accepted, or null when voice input is
/// off (no whisper model configured; voice spec).
/// </summary>
public sealed record SystemInfoResponse(
    string Version, string Provider, DateTime UtcNow, DateTime BudgetResetAtUtc, IReadOnlyList<string> Models,
    int? TranscriptionMaxSeconds);

// ----- Voice input (voice spec section 3) -----

/// <summary>What was said in the uploaded recording; empty when no speech was recognised.</summary>
public sealed record TranscriptionResponse(string Text);
