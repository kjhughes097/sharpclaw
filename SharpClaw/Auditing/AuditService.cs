using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Auditing;

public sealed class AuditService(IOptions<SharpClawOptions> options, ILogger<AuditService> logger)
{
    private readonly string _workspaceRoot = options.Value.WorkspacePath;
    private readonly Lock _writeLock = new();

    public Task LogAsync(string agentName, AuditEntryType type, string content, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_workspaceRoot))
            return Task.CompletedTask;

        var entry = FormatEntry(type, content);
        var auditPath = Path.Combine(_workspaceRoot, agentName, "audit.md");

        lock (_writeLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(auditPath)!);
            File.AppendAllText(auditPath, entry);
        }

        logger.LogDebug("Audit [{Type}] for {Agent}: {Length} chars", type, agentName, content.Length);
        return Task.CompletedTask;
    }

    private static string FormatEntry(AuditEntryType type, string content)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        return $"""

            ### {timestamp}
            - **Type**: {type}
            - **Content**: {Truncate(content, 500)}

            """;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";
}
