using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox.Tests;

/// <summary>
/// <c>git_clone</c> (spec section 7.4). No network: a fake <c>git</c> first on PATH records its argv
/// and, for a clone, builds a real one-commit repository at the target (real git does the rest).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class GitCloneOpTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly string _bin = Path.Combine(Path.GetTempPath(), $"fakegit-{Guid.NewGuid():N}");
    private readonly string _log;

    public GitCloneOpTests()
    {
        Directory.CreateDirectory(_bin);
        _log = Path.Combine(_bin, "argv.log");
        var realGit = File.Exists("/usr/bin/git") ? "/usr/bin/git" : "/bin/git";
        File.WriteAllText(Path.Combine(_bin, "git"), $$"""
            #!/bin/bash
            printf '%s\n' "$*" >> "{{_log}}"
            if [ "$7" = "clone" ]; then
              target="${@: -1}"
              mkdir -p "$target" && cd "$target" && {{realGit}} init -q && echo hello > README.md
              [ -n "$FAKE_BIG" ] && head -c "$FAKE_BIG" /dev/zero > big.bin
              {{realGit}} add -A && {{realGit}} -c user.email=t@t -c user.name=t commit -qm init
              exit ${FAKE_EXIT:-0}
            fi
            exec {{realGit}} "$@"
            """.Replace("\r", ""));
        File.SetUnixFileMode(Path.Combine(_bin, "git"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        _ws.Dispose();
        Directory.Delete(_bin, recursive: true);
    }

    private GitCloneOp Op(Dictionary<string, string>? extra = null, long max = GitCloneOp.MaxBytes)
    {
        var env = new Dictionary<string, string> { ["PATH"] = $"{_bin}:/usr/bin:/bin", ["HOME"] = _ws.Path, ["GIT_TERMINAL_PROMPT"] = "0" };
        foreach (var kv in extra ?? new())
            env[kv.Key] = kv.Value;
        return new GitCloneOp(new WorkspaceRoot(_ws.Path), env, max);
    }

    private static async Task<JsonElement> Run(GitCloneOp op, object args) =>
        JsonDocument.Parse((await op.RunAsync(JsonSerializer.SerializeToElement(args), default)).Json).RootElement;

    [Fact]
    public async Task Clones_with_the_fixed_invocation_and_reports_the_base_commit()
    {
        var r = await Run(Op(), new { url = "https://github.com/owner/repo.git", path = "repos/repo" });

        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText());
        Assert.Equal(("repos/repo", "github.com", "owner/repo"),
            (r.GetProperty("path").GetString(), r.GetProperty("host").GetString(), r.GetProperty("repo").GetString()));
        Assert.Matches("^[0-9a-f]{40}$", r.GetProperty("commit").GetString());
        Assert.True(File.Exists(_ws.File("repos/repo/README.md")));
        Assert.Equal(
            "-c core.hooksPath=/dev/null -c protocol.allow=never -c protocol.https.allow=always clone --depth 1 --no-recurse-submodules --single-branch -- https://github.com/owner/repo repos/repo",
            File.ReadLines(_log).First());
        Assert.Contains("-c core.hooksPath=/dev/null -c core.fsmonitor= rev-parse HEAD", File.ReadLines(_log).Skip(1).First());
    }

    [Fact]
    public async Task A_failed_clone_leaves_nothing_and_hides_gits_output()
    {
        var r = await Run(Op(new() { ["FAKE_EXIT"] = "128" }), new { url = "https://github.com/owner/private", path = "p" });

        Assert.Equal("clone_failed", r.GetProperty("error").GetString());
        Assert.False(Directory.Exists(_ws.File("p")));
    }

    [Fact]
    public async Task A_clone_over_the_size_cap_is_deleted()
    {
        var r = await Run(Op(new() { ["FAKE_BIG"] = "20000" }, max: 10_000), new { url = "https://github.com/owner/big", path = "big" });

        Assert.Equal("too_large", r.GetProperty("error").GetString());
        Assert.False(Directory.Exists(_ws.File("big")));
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("git://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo")]
    [InlineData("https://user:token@github.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo?x=1")]
    [InlineData("https://github.com:8443/owner/repo")]
    [InlineData("https://github.com/owner/../etc")]
    public async Task Only_plain_https_repository_urls(string url)
    {
        Assert.Equal("invalid_arguments", (await Run(Op(), new { url, path = "r" })).GetProperty("error").GetString());
        Assert.False(File.Exists(_log));
    }

    [Theory]
    [InlineData("../r", "invalid_path")]
    [InlineData("/tmp/r", "invalid_path")]
    [InlineData("existing", "exists")]
    public async Task Path_rules(string path, string error)
    {
        Directory.CreateDirectory(_ws.File("existing"));

        Assert.Equal(error, (await Run(Op(), new { url = "https://github.com/o/r", path })).GetProperty("error").GetString());
        Assert.False(File.Exists(_log));
    }
}
