using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Ops;

/// <summary>
/// <c>git_clone</c> (spec section 7.4): a public repository over HTTPS, with the spec's fixed
/// invocation (no hooks, HTTPS only, shallow, no submodules) and no credentials anywhere. A clone
/// over the size cap is deleted. The answer carries the base commit, which the gateway records.
/// </summary>
public sealed class GitCloneOp(
    WorkspaceRoot workspace, IReadOnlyDictionary<string, string> childEnv, long maxBytes = GitCloneOp.MaxBytes) : IRunnerOp
{
    public const long MaxBytes = 500L * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public string Name => BrokerTools.GitClone;

    /// <summary>The exact argv (spec section 7.4).</summary>
    public static string[] CloneArgs(string url, string path) =>
    [
        "-c", "core.hooksPath=/dev/null", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
        "clone", "--depth", "1", "--no-recurse-submodules", "--single-branch", "--", url, path,
    ];

    public async Task<OpResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        var (host, repo, error) = ToolUrls.ParseGitRepo(OpArgs.String(args, "url"));
        if (host is null)
            return OpResult.Error("invalid_arguments", error!);
        var (path, pathError) = workspace.Resolve(OpArgs.String(args, "path"));
        if (path is null)
            return OpResult.Error("invalid_path", pathError!);
        if (File.Exists(path.FullPath) || Directory.Exists(path.FullPath))
            return OpResult.Error("exists", $"'{path.Relative}' already exists; choose a new folder.");

        try
        {
            if (Path.GetDirectoryName(path.Relative)?.Replace('\\', '/') is { Length: > 0 } parent)
                NoFollow.CreateDirectory(workspace.Path, parent);

            // Rebuilt from the parsed parts, so only https://host/owner/repo ever reaches git.
            var clone = await ProcessRunner.RunAsync(new ProcessSpec(
                "git", CloneArgs($"https://{host}/{repo}", path.Relative), workspace.Path, Timeout, 64 * 1024, childEnv), ct);
            if (clone.TimedOut || clone.ExitCode != 0)
            {
                Remove(path.FullPath);
                // git's output can carry server-chosen text: it never reaches the model.
                return clone.TimedOut
                    ? OpResult.Error("timeout", $"The clone took longer than {Timeout.TotalMinutes:0} minutes.")
                    : OpResult.Error("clone_failed", $"git could not clone {host}/{repo} (exit {clone.ExitCode}); it may not exist, or may be private.");
            }

            workspace.Require(path.Relative);
            var bytes = Size(path.FullPath);
            if (bytes > maxBytes)
            {
                Remove(path.FullPath);
                return OpResult.Error("too_large", $"The repository is larger than {maxBytes / (1024 * 1024)} MB; the clone was deleted.");
            }

            var head = await ProcessRunner.RunAsync(new ProcessSpec(
                "git", ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=", "rev-parse", "HEAD"],
                path.FullPath, TimeSpan.FromSeconds(30), 1024, childEnv), ct);
            var commit = Encoding.ASCII.GetString(head.Stdout).Trim();
            if (head.ExitCode != 0 || commit.Length is not (40 or 64) || !commit.All(char.IsAsciiHexDigitLower))
            {
                Remove(path.FullPath);
                return OpResult.Error("clone_failed", "The clone has no readable HEAD commit.");
            }

            return OpResult.Success(new { path = path.Relative, host, repo, commit, bytes });
        }
        catch (WorkspacePathException ex)
        {
            return OpResult.Error("invalid_path", ex.Message);
        }
    }

    /// <summary>Total size of the regular files under <paramref name="dir"/>; links are not followed.</summary>
    private static long Size(string dir) =>
        new DirectoryInfo(dir)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
            .Sum(f => f.Length);

    /// <summary>Deletes a failed or oversized clone (a recursive delete removes links, never their targets).</summary>
    private static void Remove(string fullPath)
    {
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }
}
