namespace SharpClaw.Abstractions;

public interface ITranscriptionService
{
    bool IsAvailable { get; }

    Task<TranscriptionResult> TranscribeAsync(
        Stream audioStream,
        string sourceFormatHint,
        string? language = null,
        CancellationToken ct = default);
}

public sealed record TranscriptionResult(
    string Text,
    string? DetectedLanguage,
    TimeSpan Duration,
    TimeSpan ProcessingTime);
