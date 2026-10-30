using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Sandbox.Infrastructure;
using SharpClaw.Sandbox.Ops;

namespace SharpClaw.Sandbox.Tests;

/// <summary><c>sandbox_run</c> (spec section 7.5 and Q6): bash in a workspace folder, output cap and log file, timeout.</summary>
public class SandboxRunOpTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly SandboxRunOp _op;

    public SandboxRunOpTests() => _op = new SandboxRunOp(new WorkspaceRoot(_ws.Path), ChildEnvironment.Build(), TimeProvider.System);

    public void Dispose() => _ws.Dispose();

    private async Task<(JsonElement Result, string Json)> Run(object args)
    {
        var json = (await _op.RunAsync(JsonSerializer.SerializeToElement(args), default)).Json;
        return (JsonDocument.Parse(json).RootElement, json);
    }

    [Fact]
    public async Task Runs_bash_in_the_folder_and_reports_exit_code_and_output()
    {
        Directory.CreateDirectory(_ws.File("repo"));

        var (r, _) = await Run(new { command = "pwd; echo out; echo err >&2; exit 3", cwd = "repo" });

        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(3, r.GetProperty("exitCode").GetInt32());
        Assert.Equal($"{_ws.File("repo")}\nout\n", r.GetProperty("stdout").GetString());
        Assert.Equal("err\n", r.GetProperty("stderr").GetString());
        Assert.False(r.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("logPath").ValueKind);
    }

    [Fact]
    public async Task Output_beyond_1_MB_goes_to_a_log_file_in_the_workspace()
    {
        var (r, _) = await Run(new { command = "head -c 1200000 /dev/zero | tr '\\0' 'a'" });

        Assert.True(r.GetProperty("truncated").GetBoolean());
        Assert.Equal(SandboxRunOp.MaxOutputBytes, r.GetProperty("stdout").GetString()!.Length);
        var log = r.GetProperty("logPath").GetString()!;
        Assert.StartsWith(".sandbox/logs/run-", log);
        Assert.Equal(1_200_000 - SandboxRunOp.MaxOutputBytes, new FileInfo(_ws.File(log)).Length);
    }

    [Fact]
    public async Task A_timeout_is_reported_and_the_command_is_killed()
    {
        var (r, _) = await Run(new { command = "echo started; sleep 300", timeoutSeconds = 1 });

        Assert.True(r.GetProperty("timedOut").GetBoolean());
        Assert.Equal("started\n", r.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task Control_characters_are_replaced_and_text_is_not_escaped()
    {
        var (r, json) = await Run(new { command = "printf 'caf\\xc3\\xa9 \\x01\\x1b[31m'" });

        Assert.Equal("café \uFFFD\uFFFD[31m", r.GetProperty("stdout").GetString());
        Assert.Contains("café", json); // UTF-8, not \u00e9: output can't grow several-fold as JSON
    }

    [Fact]
    public async Task The_service_secret_never_reaches_the_command()
    {
        var op = new SandboxRunOp(new WorkspaceRoot(_ws.Path),
            ChildEnvironment.Build(new System.Collections.Hashtable { ["SANDBOX_SECRET"] = "s3cr3t", ["PATH"] = "/usr/bin:/bin" }),
            TimeProvider.System);

        var json = (await op.RunAsync(JsonSerializer.SerializeToElement(new { command = "env" }), default)).Json;

        Assert.DoesNotContain("s3cr3t", json);
        Assert.Contains("HOME=/tmp/home", json);
    }

    [Theory]
    [InlineData("../", "invalid_path")]
    [InlineData("/etc", "invalid_path")]
    [InlineData("missing", "not_found")]
    public async Task Cwd_must_be_a_workspace_folder(string cwd, string error) =>
        Assert.Equal(error, (await Run(new { command = "true", cwd })).Result.GetProperty("error").GetString());

    [Theory]
    [InlineData(0)]
    [InlineData(1801)]
    public async Task Timeout_is_bounded(int timeoutSeconds) =>
        Assert.Equal("invalid_arguments", (await Run(new { command = "true", timeoutSeconds })).Result.GetProperty("error").GetString());
}
