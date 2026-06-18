using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharpClaw.Backup;
using SharpClaw.Configuration;

namespace SharpClaw.Tests;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task RunBackupAsync_creates_tarball_with_sha256_excluding_coding()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"sc-bk-ws-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(Path.GetTempPath(), $"sc-bk-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "memory"));
        Directory.CreateDirectory(Path.Combine(workspace, "coding", "junk"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "memory", "notes.md"), "hello");
        await File.WriteAllTextAsync(Path.Combine(workspace, "coding", "junk", "huge.bin"), new string('x', 4096));

        try
        {
            var svc = new BackupService(
                Options.Create(new BackupOptions { Enabled = true, RootPath = backupRoot, RetentionDays = 1 }),
                Options.Create(new SharpClawOptions { WorkspacePath = workspace }),
                new EmptyServiceProvider(),
                NullLogger<BackupService>.Instance);

            var result = await svc.RunBackupAsync();

            Assert.True(result.Success, result.Error);
            Assert.NotNull(result.ArchivePath);
            Assert.True(File.Exists(result.ArchivePath));
            Assert.True(File.Exists(result.ArchivePath + ".sha256"));
            Assert.NotNull(result.Sha256);

            var entries = ListTarEntries(result.ArchivePath!);
            Assert.Contains("memory/notes.md", entries);
            Assert.DoesNotContain(entries, e => e.StartsWith("coding/", StringComparison.OrdinalIgnoreCase));

            var logPath = Path.Combine(backupRoot, "backup-log.jsonl");
            Assert.True(File.Exists(logPath));
            var logText = await File.ReadAllTextAsync(logPath);
            Assert.Contains("\"kind\":\"backup\"", logText);
            Assert.Contains("\"success\":true", logText);
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch { }
            try { Directory.Delete(backupRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task VerifyMode_does_not_leave_files_behind()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"sc-bk-ws-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(Path.GetTempPath(), $"sc-bk-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "1");

        try
        {
            var svc = new BackupService(
                Options.Create(new BackupOptions { Enabled = true, RootPath = backupRoot }),
                Options.Create(new SharpClawOptions { WorkspacePath = workspace }),
                new EmptyServiceProvider(),
                NullLogger<BackupService>.Instance);

            var result = await svc.RunBackupAsync(dryRun: true);

            Assert.True(result.Success, result.Error);
            // verify mode should not leave any tarballs in the live root
            if (Directory.Exists(backupRoot))
            {
                Assert.Empty(Directory.GetFiles(backupRoot, "backup-*.tar.gz"));
            }
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch { }
            try { Directory.Delete(backupRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task RunRetentionAsync_deletes_old_archives()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"sc-bk-ws-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(Path.GetTempPath(), $"sc-bk-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(backupRoot);

        var oldTar = Path.Combine(backupRoot, "backup-2020-01-01-0300.tar.gz");
        var oldSha = oldTar + ".sha256";
        var newTar = Path.Combine(backupRoot, "backup-2999-01-01-0300.tar.gz");
        await File.WriteAllTextAsync(oldTar, "old");
        await File.WriteAllTextAsync(oldSha, "deadbeef");
        await File.WriteAllTextAsync(newTar, "new");
        File.SetLastWriteTimeUtc(oldTar, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(oldSha, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(newTar, DateTime.UtcNow);

        try
        {
            var svc = new BackupService(
                Options.Create(new BackupOptions { Enabled = true, RootPath = backupRoot, RetentionDays = 7 }),
                Options.Create(new SharpClawOptions { WorkspacePath = workspace }),
                new EmptyServiceProvider(),
                NullLogger<BackupService>.Instance);

            var result = await svc.RunRetentionAsync();

            Assert.True(result.Success);
            Assert.Equal(2, result.Deleted);
            Assert.False(File.Exists(oldTar));
            Assert.False(File.Exists(oldSha));
            Assert.True(File.Exists(newTar));
        }
        finally
        {
            try { Directory.Delete(workspace, true); } catch { }
            try { Directory.Delete(backupRoot, true); } catch { }
        }
    }

    private static List<string> ListTarEntries(string tarGzPath)
    {
        var names = new List<string>();
        using var fs = File.OpenRead(tarGzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new TarReader(gz);
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
        }
        return names;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
