using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Sandbox.Infrastructure;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox.Tests;

/// <summary>The change set for <c>github_create_pr</c> (spec section 7.7, step 1), from a real local repository.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public class GitChangesOpTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly string _repo;
    private readonly string _base;

    public GitChangesOpTests()
    {
        _repo = _ws.File("repos/repo");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "keep.txt"), "a\nb\nc\n");
        File.WriteAllText(Path.Combine(_repo, "gone.txt"), "x\ny\n");
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "bin/\n");
        Git("init", "-q");
        Git("add", "-A");
        Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "base");
        _base = Git("rev-parse", "HEAD").Trim();
    }

    public void Dispose() => _ws.Dispose();

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
        return output;
    }

    private async Task<JsonElement> Changes(string? baseSha = null, string path = "repos/repo") =>
        JsonDocument.Parse((await new GitChangesOp(new WorkspaceRoot(_ws.Path), ChildEnvironment.Build())
            .RunAsync(JsonSerializer.SerializeToElement(new { path, @base = baseSha ?? _base }), default)).Json).RootElement;

    private static Dictionary<string, JsonElement> ByPath(JsonElement r) =>
        r.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);

    [Fact]
    public async Task Reports_added_modified_and_deleted_files_with_counts_modes_and_content()
    {
        File.WriteAllText(Path.Combine(_repo, "keep.txt"), "a\nB\nc\nd\n");
        File.Delete(Path.Combine(_repo, "gone.txt"));
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src/new.cs"), "one\ntwo\n");
        File.WriteAllText(Path.Combine(_repo, "run.sh"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(_repo, "run.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(_repo, "bin"));
        File.WriteAllText(Path.Combine(_repo, "bin/ignored.dll"), "x");

        var r = await Changes();

        Assert.True(r.GetProperty("ok").GetBoolean(), r.GetRawText());
        var files = ByPath(r);
        Assert.Equal(new[] { "gone.txt", "keep.txt", "run.sh", "src/new.cs" }, files.Keys);
        Assert.Equal(("modified", "100644", 2, 1), (files["keep.txt"].GetProperty("status").GetString(), files["keep.txt"].GetProperty("mode").GetString(),
            files["keep.txt"].GetProperty("additions").GetInt32(), files["keep.txt"].GetProperty("deletions").GetInt32()));
        Assert.Equal("a\nB\nc\nd\n", Encoding.UTF8.GetString(Convert.FromBase64String(files["keep.txt"].GetProperty("content").GetString()!)));
        Assert.Equal(("deleted", 2, JsonValueKind.Null), (files["gone.txt"].GetProperty("status").GetString(),
            files["gone.txt"].GetProperty("deletions").GetInt32(), files["gone.txt"].GetProperty("content").ValueKind));
        Assert.Equal(("added", 2), (files["src/new.cs"].GetProperty("status").GetString(), files["src/new.cs"].GetProperty("additions").GetInt32()));
        Assert.Equal("100755", files["run.sh"].GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Committed_changes_count_too()
    {
        File.WriteAllText(Path.Combine(_repo, "committed.txt"), "c\n");
        Git("add", "-A");
        Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "local");

        Assert.Contains("committed.txt", ByPath(await Changes()).Keys);
    }

    [Fact]
    public async Task Links_and_nested_repositories_are_reported_without_content()
    {
        File.CreateSymbolicLink(Path.Combine(_repo, "link"), "/etc/passwd");
        var nested = Path.Combine(_repo, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "x.txt"), "x");
        using (var init = Process.Start(new ProcessStartInfo("git", "init -q") { WorkingDirectory = nested })!)
            init.WaitForExit();

        var files = ByPath(await Changes());

        Assert.Equal("120000", files["link"].GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, files["link"].GetProperty("content").ValueKind);
        Assert.Equal("160000", files.Single(f => f.Key.StartsWith("nested")).Value.GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("not-a-sha", "repos/repo", "invalid_arguments")]
    [InlineData(null, "../x", "invalid_path")]
    [InlineData(null, "repos", "not_a_clone")]
    public async Task Refusals(string? baseSha, string path, string error) =>
        Assert.Equal(error, (await Changes(baseSha ?? _base, path)).GetProperty("error").GetString());

    [Fact]
    public async Task Too_many_files_are_refused()
    {
        for (var i = 0; i < 101; i++)
            File.WriteAllText(Path.Combine(_repo, $"f{i}.txt"), "x");

        Assert.Equal("too_many_files", (await Changes()).GetProperty("error").GetString());
    }
}
