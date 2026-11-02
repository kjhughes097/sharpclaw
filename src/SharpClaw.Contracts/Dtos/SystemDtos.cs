namespace SharpClaw.Contracts;

// ----- System info (3.3 spec section 5.5) -----

/// <summary>
/// <c>Provider</c> is <c>OpenRouter</c> or <c>Fake</c>; <c>BudgetResetAtUtc</c> is the next UTC midnight.
/// <c>Models</c>: the ids a thread may switch to (<c>Models:Allowed</c>), possibly empty.
/// <c>Transcription</c>: whether voice input is on (a whisper model is configured; voice spec).
/// </summary>
public sealed record SystemInfoResponse(
    string Version, string Provider, DateTime UtcNow, DateTime BudgetResetAtUtc, IReadOnlyList<string> Models,
    bool Transcription);

// ----- Voice input (voice spec section 3) -----

/// <summary>What was said in the uploaded recording; empty when no speech was recognised.</summary>
public sealed record TranscriptionResponse(string Text);
