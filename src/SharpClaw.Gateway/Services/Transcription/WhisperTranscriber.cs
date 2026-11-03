using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using Whisper.net;

namespace SharpClaw.Gateway.Services.Transcription;

/// <summary>Speech to text. The seam lets tests run without a model file.</summary>
public interface ITranscriber
{
    /// <summary>The spoken text in 16 kHz mono samples; empty when nothing was said.</summary>
    Task<string> TranscribeAsync(float[] samples, CancellationToken ct);
}

/// <summary>
/// Transcribes on the CPU with whisper.cpp (Whisper.net). The model loads on first use and stays
/// loaded. One transcription runs at a time: each already uses several threads, and running them
/// side by side would only slow every one down.
/// </summary>
public sealed partial class WhisperTranscriber(IOptions<TranscriptionOptions> options) : ITranscriber, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var o = options.Value;
            _factory ??= WhisperFactory.FromPath(o.ModelPath!);
            await using var processor = _factory.CreateBuilder()
                .WithLanguage(o.Language)
                .WithThreads(o.Threads)
                .Build();

            var text = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples, ct))
            {
                // Whisper marks silence and noise with tags such as [BLANK_AUDIO] or (wind blowing).
                if (!NonSpeech().IsMatch(segment.Text))
                    text.Append(segment.Text);
            }
            return text.ToString().Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }

    [GeneratedRegex(@"^\s*(\[[^\]]*\]|\([^)]*\))\s*$")]
    private static partial Regex NonSpeech();
}
