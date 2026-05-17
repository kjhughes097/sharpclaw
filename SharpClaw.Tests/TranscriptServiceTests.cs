using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpClaw.Auditing;
using SharpClaw.Configuration;

namespace SharpClaw.Tests;

public sealed class TranscriptServiceTests
{
    [Fact]
    public async Task LogAsync_writes_jsonl_entry_to_agent_session_file()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), $"sharpclaw-transcript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var options = Options.Create(new SharpClawOptions { WorkspacePath = workspacePath });
            var service = new TranscriptService(options, NullLogger<TranscriptService>.Instance);

            await service.LogAsync(
                "fin",
                "session-1",
                "request",
                "hello",
                new TranscriptMetadata(Source: "Telegram", LlmProvider: "copilot", Model: "claude-sonnet-4.5"));

            var transcriptPath = Path.Combine(workspacePath, "fin", "sessions", "session-1.transcript.jsonl");
            Assert.True(File.Exists(transcriptPath));

            var lines = await File.ReadAllLinesAsync(transcriptPath);
            Assert.Single(lines);
            Assert.Contains("\"agentId\":\"fin\"", lines[0]);
            Assert.Contains("\"sessionId\":\"session-1\"", lines[0]);
            Assert.Contains("\"turnType\":\"request\"", lines[0]);
            Assert.Contains("\"content\":\"hello\"", lines[0]);
            Assert.Contains("\"source\":\"Telegram\"", lines[0]);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task LogAsync_appends_multiple_entries()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), $"sharpclaw-transcript-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var options = Options.Create(new SharpClawOptions { WorkspacePath = workspacePath });
            var service = new TranscriptService(options, NullLogger<TranscriptService>.Instance);

            await service.LogAsync("fin", "session-2", "request", "first prompt");
            await service.LogAsync("fin", "session-2", "response", "first response");

            var transcriptPath = Path.Combine(workspacePath, "fin", "sessions", "session-2.transcript.jsonl");
            var lines = await File.ReadAllLinesAsync(transcriptPath);

            Assert.Equal(2, lines.Length);
            Assert.Contains("\"turnType\":\"request\"", lines[0]);
            Assert.Contains("\"turnType\":\"response\"", lines[1]);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }
}
