using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using Telegram.Bot;

namespace SharpClaw.Backup;

public sealed record BackupResult(
    bool Success,
    string? ArchivePath,
    long ArchiveBytes,
    string? Sha256,
    TimeSpan Duration,
    string? Error,
    int FilesIncluded);

public sealed record RetentionResult(
    bool Success,
    int Deleted,
    long BytesFreed,
    IReadOnlyList<string> DeletedFiles,
    string? Error);

public sealed class BackupService(
    IOptions<BackupOptions> backupOptions,
    IOptions<SharpClawOptions> sharpClawOptions,
    IServiceProvider services,
    ILogger<BackupService> logger)
{
    private readonly BackupOptions _options = backupOptions.Value;
    private readonly SharpClawOptions _sharpClaw = sharpClawOptions.Value;
    private readonly IServiceProvider _services = services;
    private readonly ILogger<BackupService> _logger = logger;

    public async Task<BackupResult> RunBackupAsync(bool dryRun = false, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? archivePath = null;
        try
        {
            var workspace = ResolveWorkspace();
            var rootPath = _options.RootPath;
            Directory.CreateDirectory(rootPath);

            var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HHmm");
            var fileName = $"backup-{stamp}.tar.gz";
            var targetDir = dryRun
                ? Path.Combine(Path.GetTempPath(), $"sharpclaw-backup-verify-{Guid.NewGuid():N}")
                : rootPath;
            if (dryRun) Directory.CreateDirectory(targetDir);
            archivePath = Path.Combine(targetDir, fileName);

            var snapshotDir = Path.Combine(Path.GetTempPath(), $"sharpclaw-db-snap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(snapshotDir);

            var dbSnapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var rel in _options.SqliteRelativePaths)
                {
                    var src = Path.Combine(workspace, rel);
                    if (!File.Exists(src)) continue;
                    var snap = Path.Combine(snapshotDir, Path.GetFileName(rel));
                    SnapshotSqlite(src, snap);
                    dbSnapshots[NormalizeRel(rel)] = snap;
                    _logger.LogInformation("Backup: snapshotted SQLite {Source} -> {Target}", src, snap);
                }

                var filesIncluded = await WriteArchiveAsync(workspace, archivePath, dbSnapshots, ct);

                var archiveBytes = new FileInfo(archivePath).Length;
                var sha = ComputeSha256(archivePath);
                var sidecarPath = archivePath + ".sha256";
                await File.WriteAllTextAsync(sidecarPath, $"{sha}  {Path.GetFileName(archivePath)}\n", ct);

                sw.Stop();
                var result = new BackupResult(true, archivePath, archiveBytes, sha, sw.Elapsed, null, filesIncluded);
                AppendLog(result, dryRun);

                if (dryRun)
                {
                    try { File.Delete(archivePath); } catch { }
                    try { File.Delete(sidecarPath); } catch { }
                    try { Directory.Delete(targetDir, recursive: true); } catch { }
                }

                _logger.LogInformation("Backup: success archive={Archive} bytes={Bytes} sha256={Sha} duration={Duration} files={Files} dryRun={DryRun}",
                    archivePath, archiveBytes, sha, sw.Elapsed, filesIncluded, dryRun);
                return result;
            }
            finally
            {
                try { Directory.Delete(snapshotDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Backup failed after {Duration}", sw.Elapsed);
            var result = new BackupResult(false, archivePath, 0, null, sw.Elapsed, ex.Message, 0);
            AppendLog(result, dryRun);
            await NotifyAsync($"❌ SharpClaw backup FAILED ({(dryRun ? "verify" : "live")})\nError: {ex.Message}\nDuration: {sw.Elapsed:c}", ct);
            return result;
        }
    }

    public async Task<RetentionResult> RunRetentionAsync(CancellationToken ct = default)
    {
        try
        {
            var rootPath = _options.RootPath;
            if (!Directory.Exists(rootPath))
                return new RetentionResult(true, 0, 0, Array.Empty<string>(), null);

            var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.RetentionDays));
            var deleted = new List<string>();
            long bytesFreed = 0;

            foreach (var pattern in new[] { "backup-*.tar.gz", "backup-*.tar.gz.sha256" })
            {
                foreach (var file in Directory.EnumerateFiles(rootPath, pattern))
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        bytesFreed += info.Length;
                        info.Delete();
                        deleted.Add(info.Name);
                        _logger.LogInformation("Retention: deleted {File} (age {Age:c})", info.Name, DateTime.UtcNow - info.LastWriteTimeUtc);
                    }
                }
            }

            var result = new RetentionResult(true, deleted.Count, bytesFreed, deleted, null);
            AppendRetentionLog(result);
            _logger.LogInformation("Retention: deleted {Count} files, freed {Bytes} bytes", deleted.Count, bytesFreed);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention failed");
            var result = new RetentionResult(false, 0, 0, Array.Empty<string>(), ex.Message);
            AppendRetentionLog(result);
            await NotifyAsync($"❌ SharpClaw backup retention FAILED\nError: {ex.Message}", ct);
            return result;
        }
    }

    private string ResolveWorkspace()
    {
        var ws = _sharpClaw.WorkspacePath;
        if (string.IsNullOrWhiteSpace(ws))
            throw new InvalidOperationException("SharpClaw:WorkspacePath is not configured; cannot back up.");
        if (!Directory.Exists(ws))
            throw new DirectoryNotFoundException($"Workspace path does not exist: {ws}");
        return Path.GetFullPath(ws);
    }

    private static void SnapshotSqlite(string sourceDbPath, string targetDbPath)
    {
        using var src = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly");
        src.Open();
        using var dst = new SqliteConnection($"Data Source={targetDbPath}");
        dst.Open();
        src.BackupDatabase(dst);
    }

    private async Task<int> WriteArchiveAsync(string workspace, string archivePath, IDictionary<string, string> dbSnapshots, CancellationToken ct)
    {
        var excludes = _options.ExcludeRelativePaths
            .Select(NormalizeRel)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var snapshotKeys = dbSnapshots.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filesIncluded = 0;

        await using (var fs = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
        await using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var entry in EnumerateWorkspace(workspace, excludes))
            {
                ct.ThrowIfCancellationRequested();
                var rel = NormalizeRel(Path.GetRelativePath(workspace, entry));

                if (rel.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) ||
                    rel.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase) ||
                    rel.EndsWith(".db-journal", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (snapshotKeys.Contains(rel))
                    continue;

                await tar.WriteEntryAsync(entry, rel, ct);
                filesIncluded++;
            }

            foreach (var (rel, snapPath) in dbSnapshots)
            {
                ct.ThrowIfCancellationRequested();
                await tar.WriteEntryAsync(snapPath, rel, ct);
                filesIncluded++;
            }
        }

        return filesIncluded;
    }

    private static IEnumerable<string> EnumerateWorkspace(string workspace, HashSet<string> excludes)
    {
        var stack = new Stack<string>();
        stack.Push(workspace);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var sub in subDirs)
            {
                var rel = NormalizeRel(Path.GetRelativePath(workspace, sub));
                if (IsExcluded(rel, excludes)) continue;
                stack.Push(sub);
            }
            foreach (var f in files)
            {
                var rel = NormalizeRel(Path.GetRelativePath(workspace, f));
                if (IsExcluded(rel, excludes)) continue;
                yield return f;
            }
        }
    }

    private static bool IsExcluded(string rel, HashSet<string> excludes)
    {
        foreach (var ex in excludes)
        {
            if (rel.Equals(ex, StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.StartsWith(ex + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string NormalizeRel(string rel) => rel.Replace('\\', '/').TrimStart('/');

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private void AppendLog(BackupResult result, bool dryRun)
    {
        try
        {
            Directory.CreateDirectory(_options.RootPath);
            var logPath = Path.Combine(_options.RootPath, "backup-log.jsonl");
            var entry = new
            {
                ts = DateTime.UtcNow,
                kind = "backup",
                dryRun,
                success = result.Success,
                archive = result.ArchivePath is null ? null : Path.GetFileName(result.ArchivePath),
                bytes = result.ArchiveBytes,
                sha256 = result.Sha256,
                durationMs = (long)result.Duration.TotalMilliseconds,
                files = result.FilesIncluded,
                error = result.Error,
            };
            File.AppendAllText(logPath, JsonSerializer.Serialize(entry) + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append backup log");
        }
    }

    private void AppendRetentionLog(RetentionResult result)
    {
        try
        {
            Directory.CreateDirectory(_options.RootPath);
            var logPath = Path.Combine(_options.RootPath, "backup-log.jsonl");
            var entry = new
            {
                ts = DateTime.UtcNow,
                kind = "retention",
                success = result.Success,
                deleted = result.Deleted,
                bytesFreed = result.BytesFreed,
                files = result.DeletedFiles,
                error = result.Error,
            };
            File.AppendAllText(logPath, JsonSerializer.Serialize(entry) + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append retention log");
        }
    }

    private async Task NotifyAsync(string message, CancellationToken ct)
    {
        var chatId = _options.NotifyTelegramChatId;
        if (chatId is null) return;
        var telegram = _services.GetService<ITelegramBotClient>();
        if (telegram is null) return;
        try
        {
            await telegram.SendMessage(chatId.Value, message, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send backup failure notification to {ChatId}", chatId);
        }
    }
}
