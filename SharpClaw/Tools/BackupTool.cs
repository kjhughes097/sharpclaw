using SharpClaw.Abstractions;
using SharpClaw.Backup;
using SharpClaw.Models;

namespace SharpClaw.Tools;

public sealed class BackupTool(BackupService backupService) : ITool
{
    public string Name => "run_backup";

    public string Description => "Run a SharpClaw workspace backup on demand. Modes: 'backup' (default) creates a tarball + sha256, 'verify' performs a dry-run that builds and hashes a tarball into a temp dir then deletes it (used for monthly restore-readiness checks), 'retention' prunes archives older than the configured retention window.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("mode", "string", "One of 'backup', 'verify', or 'retention'. Defaults to 'backup'.", Required: false),
    ];

    public async Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var mode = context.GetString("mode");
        if (string.IsNullOrWhiteSpace(mode)) mode = "backup";

        switch (mode.Trim().ToLowerInvariant())
        {
            case "backup":
            {
                var r = await backupService.RunBackupAsync(dryRun: false, ct);
                return r.Success
                    ? $"✅ Backup complete: {Path.GetFileName(r.ArchivePath)} ({FormatBytes(r.ArchiveBytes)}, {r.FilesIncluded} files, sha256={r.Sha256?[..12]}…, took {r.Duration:c})"
                    : $"❌ Backup FAILED after {r.Duration:c}: {r.Error}";
            }
            case "verify":
            {
                var r = await backupService.RunBackupAsync(dryRun: true, ct);
                return r.Success
                    ? $"✅ Verify-mode backup OK: would produce {FormatBytes(r.ArchiveBytes)} archive ({r.FilesIncluded} files, sha256={r.Sha256?[..12]}…, took {r.Duration:c}). Temp tarball discarded."
                    : $"❌ Verify-mode backup FAILED after {r.Duration:c}: {r.Error}";
            }
            case "retention":
            {
                var r = await backupService.RunRetentionAsync(ct);
                return r.Success
                    ? $"✅ Retention complete: deleted {r.Deleted} files ({FormatBytes(r.BytesFreed)} freed)."
                    : $"❌ Retention FAILED: {r.Error}";
            }
            default:
                return $"Error: unknown mode '{mode}'. Expected 'backup', 'verify', or 'retention'.";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
