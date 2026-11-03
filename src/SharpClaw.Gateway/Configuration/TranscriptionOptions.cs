using System.ComponentModel.DataAnnotations;
using SharpClaw.Gateway.Services.Transcription;

namespace SharpClaw.Gateway.Configuration;

/// <summary>Voice input: local speech-to-text with a whisper.cpp model (voice spec section 1).</summary>
public sealed class TranscriptionOptions : IHasProblem
{
    public const string Section = "Transcription";

    /// <summary>Path to a ggml whisper model file. Empty turns voice input off.</summary>
    public string? ModelPath { get; set; }

    /// <summary>Spoken language (a whisper code such as <c>en</c>), or <c>auto</c> to detect it.</summary>
    [Required, RegularExpression("^(auto|[a-z]{2,3})$")] public string Language { get; set; } = "en";

    /// <summary>The longest recording accepted, in seconds.</summary>
    [Range(1, 600)] public int MaxSeconds { get; set; } = 120;

    /// <summary>CPU threads one transcription may use.</summary>
    [Range(1, 64)] public int Threads { get; set; } = 4;

    public bool Enabled => !string.IsNullOrWhiteSpace(ModelPath);

    /// <summary>Request body limit: the longest valid recording plus room for the WAV headers.</summary>
    public long MaxBodyBytes => (long)MaxSeconds * WavAudio.BytesPerSecond + 4096;

    public string? Problem() =>
        Enabled && !File.Exists(ModelPath)
            ? $"Transcription:ModelPath '{ModelPath}' does not exist. Download a whisper model there, or leave it empty to turn voice input off."
            : null;
}
