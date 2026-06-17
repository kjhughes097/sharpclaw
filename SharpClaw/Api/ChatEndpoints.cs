using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Auditing;
using SharpClaw.Configuration;
using SharpClaw.Interactions;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using SharpClaw.Sessions;

namespace SharpClaw.Api;

internal static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat");

        // Send a message to an agent — follows same path as Telegram
        group.MapPost("/{agentName}", async (
            string agentName,
            ChatRequest request,
            AgentSessionRegistry sessionRegistry,
            AgentInvoker invoker,
            ChannelFanOutService fanOut,
            CancellationToken ct) =>
        {
            var session = sessionRegistry.GetOrCreate(agentName);
            var channelId = $"http:{Guid.NewGuid():N}";

            // Publish inbound message
            await session.PublishAsync(new AgentMessage(
                session.SessionId,
                Guid.NewGuid().ToString(),
                MessageOrigin.Web,
                agentName,
                request.Text,
                DateTimeOffset.UtcNow), ct);

            // Fan out inbound message to other channels
            await fanOut.BroadcastAsync(agentName, $"[web] {request.Text}", channelId, ct);

            var schedulingCtx = new SchedulingContext(channelId, ScheduleChannelType.Web, agentName);
            var (switchedTo, responseText) = await invoker.InvokeAsync(session, request.Text, schedulingCtx, ct);

            // Fan out agent response to other channels
            if (!string.IsNullOrEmpty(responseText))
                await fanOut.BroadcastAsync(agentName, responseText, channelId, ct);

            return Results.Ok(new ChatResponse(responseText, switchedTo));
        });

        // Send a voice/audio message to an agent — transcribes via STT, then routes through normal flow
        group.MapPost("/{agentName}/audio", async (
            string agentName,
            HttpRequest request,
            ITranscriptionService? transcription,
            IOptions<SttOptions> sttOptions,
            AgentSessionRegistry sessionRegistry,
            AgentInvoker invoker,
            ChannelFanOutService fanOut,
            CancellationToken ct) =>
        {
            if (transcription is null || !transcription.IsAvailable)
                return Results.Problem("STT is not enabled. Set Stt:Enabled = true.", statusCode: 503);

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required with 'audio' file field" });

            var form = await request.ReadFormAsync(ct);
            var audioFile = form.Files["audio"] ?? form.Files.FirstOrDefault();
            if (audioFile is null || audioFile.Length == 0)
                return Results.BadRequest(new { error = "missing 'audio' file" });

            if (audioFile.Length > sttOptions.Value.MaxAudioBytes)
                return Results.BadRequest(new { error = $"audio exceeds MaxAudioBytes ({sttOptions.Value.MaxAudioBytes})" });

            var language = form["language"].FirstOrDefault();

            string transcript;
            try
            {
                await using var audioStream = audioFile.OpenReadStream();
                var result = await transcription.TranscribeAsync(
                    audioStream,
                    audioFile.ContentType ?? "audio/unknown",
                    language,
                    ct);
                transcript = result.Text;
            }
            catch (Exception ex)
            {
                return Results.Problem($"Transcription failed: {ex.Message}", statusCode: 500);
            }

            if (string.IsNullOrWhiteSpace(transcript))
                return Results.Ok(new AudioChatResponse(string.Empty, null, null));

            var session = sessionRegistry.GetOrCreate(agentName);
            var channelId = $"http:{Guid.NewGuid():N}";

            await session.PublishAsync(new AgentMessage(
                session.SessionId,
                Guid.NewGuid().ToString(),
                MessageOrigin.Web,
                agentName,
                transcript,
                DateTimeOffset.UtcNow), ct);

            await fanOut.BroadcastAsync(agentName, $"[web/audio] {transcript}", channelId, ct);

            var schedulingCtx = new SchedulingContext(channelId, ScheduleChannelType.Web, agentName);
            var (switchedTo, responseText) = await invoker.InvokeAsync(session, transcript, schedulingCtx, ct);

            if (!string.IsNullOrEmpty(responseText))
                await fanOut.BroadcastAsync(agentName, responseText, channelId, ct);

            return Results.Ok(new AudioChatResponse(transcript, responseText, switchedTo));
        }).DisableAntiforgery();

        // Get last N transcript entries for an agent
        group.MapGet("/{agentName}/history", (
            string agentName,
            IOptions<SharpClawOptions> options,
            int? limit) =>
        {
            var workspacePath = options.Value.WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.Ok(Array.Empty<object>());

            var sessionsDir = Path.Combine(workspacePath, agentName, "sessions");
            if (!Directory.Exists(sessionsDir))
                return Results.Ok(Array.Empty<object>());

            // Find the most recent transcript file
            var transcriptFiles = Directory.GetFiles(sessionsDir, "*.transcript.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (transcriptFiles.Count == 0)
                return Results.Ok(Array.Empty<object>());

            var maxEntries = limit ?? 10;
            var entries = new List<ChatHistoryEntry>();
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

            // Read from most recent transcript file(s) until we have enough entries
            foreach (var file in transcriptFiles)
            {
                var lines = File.ReadAllLines(file);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<TranscriptEntryDto>(line, jsonOptions);
                        if (entry is not null)
                        {
                            entries.Add(new ChatHistoryEntry(
                                entry.TurnType,
                                entry.Content,
                                entry.TimestampUtc));
                        }
                    }
                    catch { /* skip malformed lines */ }
                }

                if (entries.Count >= maxEntries)
                    break;
            }

            // Return last N entries
            var result = entries
                .OrderBy(e => e.Timestamp)
                .TakeLast(maxEntries)
                .ToList();

            return Results.Ok(result);
        });
    }

    private sealed record ChatRequest(string Text);
    private sealed record ChatResponse(string? Response, string? SwitchedTo);
    private sealed record AudioChatResponse(string Transcript, string? Response, string? SwitchedTo);
    private sealed record ChatHistoryEntry(string TurnType, string Content, DateTimeOffset Timestamp);

    private sealed record TranscriptEntryDto(
        DateTimeOffset TimestampUtc,
        string AgentId,
        string SessionId,
        string TurnType,
        string Content,
        bool? IsCommand);
}
