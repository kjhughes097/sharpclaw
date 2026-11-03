using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services.Transcription;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>POST /api/transcriptions</c>: local speech to text for voice input (voice spec section 3).</summary>
public sealed class TranscriptionTests : IDisposable
{
    private const string Spoken = "please summarise the open pull requests";
    private readonly string _model = Path.Combine(Path.GetTempPath(), $"sharpclaw-model-{Guid.NewGuid():N}.bin");

    public TranscriptionTests() => File.WriteAllBytes(_model, [0]);

    public void Dispose() => File.Delete(_model);

    private sealed class FakeTranscriber : ITranscriber
    {
        public ConcurrentQueue<float[]> Calls { get; } = new();

        public Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
        {
            Calls.Enqueue(samples);
            return Task.FromResult(Spoken);
        }
    }

    private async Task<(GatewayFixture Fx, FakeTranscriber Transcriber)> StartAsync(
        bool enabled = true, ConcurrentQueue<string>? logs = null)
    {
        var fake = new FakeTranscriber();
        var config = new Dictionary<string, string?> { ["Transcription:MaxSeconds"] = "2" };
        if (enabled)
            config["Transcription:ModelPath"] = _model;
        var fx = await GatewayFixture.CreateAsync(extraConfig: config, configure: b =>
        {
            b.Services.AddSingleton<ITranscriber>(fake);
            if (logs is not null)
                b.Logging.AddProvider(new TelemetryAndAbortTests.CapturingLoggerProvider(logs));
        });
        return (fx, fake);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, byte[] wav)
    {
        var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return http.PostAsync("/api/transcriptions", content);
    }

    private static async Task<ApiErrorResponse> ErrorAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!;

    [Fact]
    public async Task A_recording_comes_back_as_text()
    {
        var (fx, transcriber) = await StartAsync();
        await using var _ = fx;
        using var admin = fx.Admin();

        var resp = await PostAsync(admin, WavAudioTests.Silence(1.5));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<TranscriptionResponse>(SharpClawJson.Options);
        Assert.Equal(Spoken, body!.Text);
        Assert.Equal(24000, Assert.Single(transcriber.Calls).Length);
        var info = await admin.GetFromJsonAsync<SystemInfoResponse>("/api/system", SharpClawJson.Options);
        Assert.Equal(2, info!.TranscriptionMaxSeconds);
    }

    [Fact]
    public async Task Without_a_model_voice_input_is_off()
    {
        var (fx, transcriber) = await StartAsync(enabled: false);
        await using var _ = fx;
        using var admin = fx.Admin();

        var resp = await PostAsync(admin, WavAudioTests.Silence(1));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(ErrorCodes.TranscriptionUnavailable, (await ErrorAsync(resp)).Error.Code);
        Assert.Empty(transcriber.Calls);
        var info = await admin.GetFromJsonAsync<SystemInfoResponse>("/api/system", SharpClawJson.Options);
        Assert.Null(info!.TranscriptionMaxSeconds);
    }

    [Fact]
    public async Task Audio_in_another_format_is_rejected_without_transcribing()
    {
        var (fx, transcriber) = await StartAsync();
        await using var _ = fx;
        using var admin = fx.Admin();

        var stereo = await PostAsync(admin, WavAudioTests.Wav(new short[100], channels: 2));
        var webm = await PostAsync(admin, "\u001aE\u00df\u00a3 webm bytes"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, stereo.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, (await ErrorAsync(stereo)).Error.Code);
        Assert.Equal("The audio must be a WAV file.", (await ErrorAsync(webm)).Error.Message);
        Assert.Empty(transcriber.Calls);
    }

    [Fact]
    public async Task Recordings_over_the_limit_are_413()
    {
        var (fx, transcriber) = await StartAsync();
        await using var _ = fx;
        using var admin = fx.Admin();

        // Just over MaxSeconds still fits the body limit, so the duration check catches it…
        var slightlyLong = await PostAsync(admin, WavAudioTests.Silence(2.05));
        // …and far over is cut off by the request size limit.
        var wayLong = await PostAsync(admin, WavAudioTests.Silence(10));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, slightlyLong.StatusCode);
        Assert.Equal("The recording is longer than 2 seconds.", (await ErrorAsync(slightlyLong)).Error.Message);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, wayLong.StatusCode);
        Assert.Empty(transcriber.Calls);
    }

    [Fact]
    public async Task Needs_client_credentials_and_the_client_listener()
    {
        var (fx, transcriber) = await StartAsync();
        await using var _ = fx;
        using var anonymous = fx.ClientRaw();
        using var agentListener = fx.AgentRaw();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(anonymous, WavAudioTests.Silence(1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(agentListener, WavAudioTests.Silence(1))).StatusCode);
        Assert.Empty(transcriber.Calls);
    }

    [Fact]
    public async Task The_transcript_is_never_logged_and_the_duration_is_measured()
    {
        var logs = new ConcurrentQueue<string>();
        var (fx, _) = await StartAsync(logs: logs);
        await using var __ = fx;
        var observed = new ConcurrentBag<(string Name, string? Status)>();
        var meter = fx.App.Services.GetRequiredService<GatewayMetrics>().Meter;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, meter))
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<double>((inst, _, tags, _) =>
        {
            string? status = null;
            foreach (var t in tags)
                if (t.Key == "status") status = t.Value?.ToString();
            observed.Add((inst.Name, status));
        });
        listener.Start();
        using var admin = fx.Admin();

        (await PostAsync(admin, WavAudioTests.Silence(1))).EnsureSuccessStatusCode();

        Assert.Contains(observed, m => m.Name == "sharpclaw.transcription.duration" && m.Status == "ok");
        Assert.Contains(logs, l => l.Contains("Transcribed"));
        Assert.DoesNotContain(logs, l => l.Contains(Spoken));
    }
}
