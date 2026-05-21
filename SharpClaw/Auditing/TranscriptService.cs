using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Auditing;

public sealed class TranscriptService(IOptions<SharpClawOptions> options, ILogger<TranscriptService> logger)
{
    private readonly string _workspaceRoot = options.Value.WorkspacePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lock _writeLock = new();

    public Task LogAsync(
        string agentName,
        string sessionId,
        string turnType,
        string content,
        TranscriptMetadata? metadata = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
            return Task.CompletedTask;

        var safeAgentName = SanitizePathSegment(agentName);
        var safeSessionId = SanitizePathSegment(sessionId);
        var transcriptPath = Path.Combine(_workspaceRoot, safeAgentName, "sessions", $"{safeSessionId}.transcript.jsonl");

        var entry = new TranscriptEntry(
            TimestampUtc: DateTimeOffset.UtcNow,
            AgentId: agentName,
            SessionId: sessionId,
            TurnType: turnType,
            Content: content,
            Source: metadata?.Source,
            LlmProvider: metadata?.LlmProvider,
            Model: metadata?.Model,
            ToolCount: metadata?.ToolCount,
            McpCount: metadata?.McpCount,
            Success: metadata?.Success,
            Error: metadata?.Error,
            DurationMs: metadata?.DurationMs,
            IsCommand: metadata?.IsCommand,
            InputTokens: metadata?.InputTokens,
            OutputTokens: metadata?.OutputTokens);

        var jsonLine = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;

        lock (_writeLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
            File.AppendAllText(transcriptPath, jsonLine);
        }

        logger.LogDebug(
            "Transcript [{TurnType}] for {Agent} session {SessionId}: {Length} chars",
            turnType,
            agentName,
            sessionId,
            content.Length);

        return Task.CompletedTask;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value;
    }
}

public sealed record TranscriptMetadata(
    string? Source = null,
    string? LlmProvider = null,
    string? Model = null,
    int? ToolCount = null,
    int? McpCount = null,
    bool? Success = null,
    string? Error = null,
    double? DurationMs = null,
    bool? IsCommand = null,
    int? InputTokens = null,
    int? OutputTokens = null);

internal sealed record TranscriptEntry(
    DateTimeOffset TimestampUtc,
    string AgentId,
    string SessionId,
    string TurnType,
    string Content,
    string? Source,
    string? LlmProvider,
    string? Model,
    int? ToolCount,
    int? McpCount,
    bool? Success,
    string? Error,
    double? DurationMs,
    bool? IsCommand,
    int? InputTokens,
    int? OutputTokens);