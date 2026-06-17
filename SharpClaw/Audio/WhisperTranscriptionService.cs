using System.Diagnostics;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using Whisper.net;

namespace SharpClaw.Audio;

public sealed class WhisperTranscriptionService(
    IOptions<SttOptions> options,
    WhisperModelDownloader modelDownloader,
    AudioConverter audioConverter,
    ILogger<WhisperTranscriptionService> logger) : ITranscriptionService, IDisposable
{
    private readonly SttOptions _options = options.Value;
    private readonly SemaphoreSlim _concurrency = new(Math.Max(1, options.Value.MaxConcurrency));
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private WhisperFactory? _factory;
    private bool _disposed;

    public bool IsAvailable => _options.Enabled;

    public async Task<TranscriptionResult> TranscribeAsync(
        Stream audioStream,
        string sourceFormatHint,
        string? language = null,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("STT is disabled. Set Stt:Enabled = true.");

        var totalSw = Stopwatch.StartNew();

        await using var wavStream = new MemoryStream();
        await audioConverter.ConvertToWhisperWavAsync(audioStream, sourceFormatHint, wavStream, ct);
        wavStream.Position = 0;

        await EnsureFactoryAsync(ct);

        await _concurrency.WaitAsync(ct);
        try
        {
            var lang = string.IsNullOrWhiteSpace(language) ? _options.Language : language;

            var processorBuilder = _factory!.CreateBuilder()
                .WithSegmentEventHandler(_ => { });

            processorBuilder = string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase)
                ? processorBuilder.WithLanguage("auto")
                : processorBuilder.WithLanguage(lang);

            using var processor = processorBuilder.Build();

            var processSw = Stopwatch.StartNew();
            var segments = new List<string>();
            string? detectedLanguage = null;

            await foreach (var segment in processor.ProcessAsync(wavStream, ct))
            {
                segments.Add(segment.Text);
                detectedLanguage ??= segment.Language;
            }
            processSw.Stop();

            var text = string.Concat(segments).Trim();

            logger.LogInformation(
                "Transcribed audio in {ProcessMs}ms (total {TotalMs}ms, lang={Lang}, chars={Chars}, model={Model})",
                processSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds,
                detectedLanguage ?? lang, text.Length, _options.ModelSize);

            return new TranscriptionResult(
                Text: text,
                DetectedLanguage: detectedLanguage,
                Duration: TimeSpan.Zero,
                ProcessingTime: processSw.Elapsed);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task EnsureFactoryAsync(CancellationToken ct)
    {
        if (_factory is not null) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_factory is not null) return;

            var modelPath = await modelDownloader.EnsureModelAsync(ct);
            logger.LogInformation("Loading Whisper model from {Path}", modelPath);
            _factory = WhisperFactory.FromPath(modelPath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _factory?.Dispose();
        _concurrency.Dispose();
        _initLock.Dispose();
    }
}
