using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Ops;

/// <summary>
/// The change set of a clone for <c>github_create_pr</c> (spec section 7.7, step 1): every file added,
/// modified or deleted compared with the base commit the gateway names (from its own clone record),
/// with mode, line counts and content. git runs with hooks and fsmonitor off and returns data only.
/// Symbolic links and submodules are reported (without content) so the gateway can refuse them.
/// Not a broker tool: only the gateway asks for it.
/// </summary>
public sealed class GitChangesOp(WorkspaceRoot workspace, IReadOnlyDictionary<string, string> childEnv) : IRunnerOp
{
    public const string OpName = "git_changes";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly string[] Safe = ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=", "-c", "core.quotePath=false"];

    public string Name => OpName;

    private sealed record Change(string Path, string Status, string Mode, int Additions, int Deletions);

    public async Task<OpResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        var (clone, error) = workspace.Resolve(OpArgs.String(args, "path"));
        if (clone is null)
            return OpResult.Error("invalid_path", error!);
        var baseSha = OpArgs.String(args, "base");
        if (baseSha is not { Length: 40 or 64 } || !baseSha.All(char.IsAsciiHexDigitLower))
            return OpResult.Error("invalid_arguments", "base must be a commit id.");
        if (!Directory.Exists(Path.Combine(clone.FullPath, ".git")) || new FileInfo(Path.Combine(clone.FullPath, ".git")).LinkTarget is not null)
            return OpResult.Error("not_a_clone", $"'{clone.Relative}' is not a git clone.");

        try
        {
            // Tracked changes against the base (the working tree, so commits made since are included).
            var raw = await Git(clone.FullPath, ["diff", "--raw", "-z", "--no-renames", "--abbrev=40", baseSha], ct);
            var numstat = await Git(clone.FullPath, ["diff", "--numstat", "-z", "--no-renames", baseSha], ct);
            var untracked = await Git(clone.FullPath, ["ls-files", "--others", "--exclude-standard", "-z"], ct);
            if (raw is null || numstat is null || untracked is null)
                return OpResult.Error("git_failed", "git could not compare the clone with its base commit.");

            var counts = ParseNumstat(numstat);
            var changes = ParseRaw(raw).Select(c => c with
            {
                Additions = counts.GetValueOrDefault(c.Path).Additions,
                Deletions = counts.GetValueOrDefault(c.Path).Deletions,
            }).ToList();
            foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                changes.Add(new Change(path, "added", ModeOf(Path.Combine(clone.FullPath, path)), 0, 0));

            if (changes.Count > ChangeSetLimits.MaxFiles)
                return OpResult.Error("too_many_files", $"More than {ChangeSetLimits.MaxFiles} files changed.");

            var files = new List<object>();
            long total = 0;
            foreach (var change in changes.OrderBy(c => c.Path, StringComparer.Ordinal))
            {
                string? content = null;
                var additions = change.Additions;
                if (change.Status != "deleted" && change.Mode is ChangeSetLimits.ModeFile or ChangeSetLimits.ModeExecutable)
                {
                    var relative = $"{clone.Relative}/{change.Path}";
                    if (new FileInfo(Path.Combine(workspace.Path, relative)).Length > ChangeSetLimits.MaxFileBytes)
                        return OpResult.Error("file_too_large", $"{change.Path} is larger than {ChangeSetLimits.MaxFileBytes / 1024} KB.");
                    var bytes = await NoFollow.ReadAllBytesAsync(workspace.Path, relative, ct);
                    total += bytes.Length;
                    if (total > ChangeSetLimits.MaxTotalBytes)
                        return OpResult.Error("change_set_too_large", $"The changes are larger than {ChangeSetLimits.MaxTotalBytes / (1024 * 1024)} MB.");
                    content = Convert.ToBase64String(bytes);
                    if (change.Status == "added" && additions == 0 && !bytes.AsSpan().Contains((byte)0))
                        additions = bytes.Count(b => b == '\n') + (bytes.Length > 0 && bytes[^1] != '\n' ? 1 : 0);
                }
                files.Add(new { path = change.Path, status = change.Status, mode = change.Mode, additions, deletions = change.Deletions, content });
            }
            return OpResult.Success(new { path = clone.Relative, @base = baseSha, files });
        }
        catch (WorkspacePathException ex)
        {
            return OpResult.Error("invalid_path", ex.Message);
        }
    }

    private async Task<string?> Git(string cwd, string[] args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(new ProcessSpec("git", [.. Safe, .. args], cwd, Timeout, 4 * 1024 * 1024, childEnv), ct);
        return result is { ExitCode: 0, TimedOut: false, Truncated: false } ? Encoding.UTF8.GetString(result.Stdout) : null;
    }

    /// <summary><c>:oldmode newmode oldsha newsha status\0path\0</c> entries.</summary>
    private static List<Change> ParseRaw(string raw)
    {
        var parts = raw.Split('\0');
        var changes = new List<Change>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            var meta = parts[i].TrimStart(':').Split(' ');
            if (meta.Length < 5)
                continue;
            var (oldMode, newMode, status) = (meta[0], meta[1], meta[4]);
            changes.Add(status switch
            {
                "A" => new Change(parts[i + 1], "added", newMode, 0, 0),
                "D" => new Change(parts[i + 1], "deleted", oldMode, 0, 0),
                _ => new Change(parts[i + 1], "modified", newMode, 0, 0),
            });
        }
        return changes;
    }

    /// <summary><c>added\tdeleted\tpath\0</c> entries (binary files show <c>-</c>).</summary>
    private static Dictionary<string, (int Additions, int Deletions)> ParseNumstat(string numstat) =>
        numstat.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 3))
            .Where(f => f.Length == 3)
            .ToDictionary(f => f[2], f => (int.TryParse(f[0], out var a) ? a : 0, int.TryParse(f[1], out var d) ? d : 0));

    /// <summary>The git mode of an untracked path: a link or a nested repository is reported as such.</summary>
    private static string ModeOf(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (info.LinkTarget is not null)
            return ChangeSetLimits.ModeSymlink;
        if (Directory.Exists(fullPath))
            return ChangeSetLimits.ModeSubmodule;
        return OperatingSystem.IsWindows() || (File.GetUnixFileMode(fullPath) & UnixFileMode.UserExecute) == 0
            ? ChangeSetLimits.ModeFile
            : ChangeSetLimits.ModeExecutable;
    }
}
