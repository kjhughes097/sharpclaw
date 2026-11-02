using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services.Transcription;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// <c>POST /api/transcriptions</c> (voice spec section 3): turns a short WAV recording into text,
/// on this machine. The audio is held in memory for the request only; it is never stored, logged or
/// sent anywhere. The UI then posts the text as an ordinary message.
/// </summary>
public static class TranscriptionEndpoints
{
    public const string WavContentType = "audio/wav";

    public static void Map(RouteGroupBuilder client)
    {
        var maxBody = ((IEndpointRouteBuilder)client).ServiceProvider
            .GetRequiredService<IOptions<TranscriptionOptions>>().Value.MaxBodyBytes;
        client.MapPost("/transcriptions", Transcribe).WithName("createTranscription").WithTags("transcriptions")
            .WithMetadata(new RequestSizeLimitAttribute(maxBody))
            .Accepts<Stream>(WavContentType)
            .Produces<TranscriptionResponse>()
            .Produces<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> Transcribe(
        HttpRequest request, IOptions<TranscriptionOptions> options, ITranscriber transcriber, GatewayMetrics metrics,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, ErrorCodes.TranscriptionUnavailable,
                "Voice input is off: the gateway has no transcription model (Transcription:ModelPath).");

        byte[] body;
        try
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLong(o);
        }

        var audio = WavAudio.Parse(body);
        if (audio.Error is { } error)
            return ApiResults.ValidationFailed(error);
        if (audio.Seconds > o.MaxSeconds)
            return TooLong(o);

        var log = loggers.CreateLogger(typeof(TranscriptionEndpoints));
        var watch = Stopwatch.StartNew();
        string text;
        try
        {
            text = await transcriber.TranscribeAsync(audio.Samples!, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            metrics.RecordTranscription("error", watch.Elapsed.TotalSeconds);
            throw;
        }
        metrics.RecordTranscription("ok", watch.Elapsed.TotalSeconds);
        // Sizes and timings only: never the transcript.
        log.LogInformation("Transcribed {AudioSeconds:F1}s of audio in {ElapsedSeconds:F1}s ({Chars} chars)",
            audio.Seconds, watch.Elapsed.TotalSeconds, text.Length);
        return Results.Json(new TranscriptionResponse(text), SharpClawJson.Options);
    }

    private static IResult TooLong(TranscriptionOptions o) =>
        ApiResults.Error(StatusCodes.Status413PayloadTooLarge, ErrorCodes.ValidationFailed,
            $"The recording is longer than {o.MaxSeconds} seconds.");
}
