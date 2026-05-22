using System.Text.Json;
using Microsoft.Extensions.Options;
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
    private sealed record ChatHistoryEntry(string TurnType, string Content, DateTimeOffset Timestamp);

    private sealed record TranscriptEntryDto(
        DateTimeOffset TimestampUtc,
        string AgentId,
        string SessionId,
        string TurnType,
        string Content,
        bool? IsCommand);
}
