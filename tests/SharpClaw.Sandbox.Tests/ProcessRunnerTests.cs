using System.Collections;
using System.Text;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Tests;

/// <summary>Child processes (spec section 6): argv only, own process group killed on timeout and completion, output cap.</summary>
public class ProcessRunnerTests
{
    private static readonly IReadOnlyDictionary<string, string> Env = ChildEnvironment.Build();

    private static Task<ProcessResult> Run(string file, string[] args, TimeSpan? timeout = null, int max = 1 << 20, Stream? overflow = null, byte[]? stdin = null) =>
        ProcessRunner.RunAsync(new ProcessSpec(file, args, Path.GetTempPath(), timeout ?? TimeSpan.FromSeconds(30), max, Env, stdin, overflow), default);

    [Fact]
    public async Task Arguments_are_passed_as_argv_never_through_a_shell()
    {
        var result = await Run("printf", ["%s|%s", "a; echo injected", "$(id) `id` *"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a; echo injected|$(id) `id` *", Encoding.UTF8.GetString(result.Stdout));
    }

    [Fact]
    public async Task Stdin_is_fed_and_stderr_captured()
    {
        var result = await Run("bash", ["-c", "cat; echo err >&2; exit 3"], stdin: "hello"u8.ToArray());

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("hello", Encoding.UTF8.GetString(result.Stdout));
        Assert.Equal("err\n", Encoding.UTF8.GetString(result.Stderr));
    }

    [Fact]
    public async Task A_timeout_kills_the_whole_process_group()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"pid-{Guid.NewGuid():N}");
        var result = await Run("bash", ["-c", $"sleep 300 & echo $! > {pidFile}; sleep 300"], TimeSpan.FromSeconds(1));

        Assert.True(result.TimedOut);
        var child = int.Parse(File.ReadAllText(pidFile).Trim());
        File.Delete(pidFile);
        Assert.False(Directory.Exists($"/proc/{child}"), "the background child survived the timeout");
    }

    [Fact]
    public async Task Background_processes_left_behind_are_killed_when_the_command_finishes()
    {
        var result = await Run("bash", ["-c", "sleep 300 > /dev/null 2>&1 & echo $!"]);

        Assert.Equal(0, result.ExitCode);
        var child = int.Parse(Encoding.UTF8.GetString(result.Stdout).Trim());
        Assert.False(Directory.Exists($"/proc/{child}"), "the background child outlived the command");
    }

    [Fact]
    public async Task Output_beyond_the_cap_goes_to_the_overflow_stream()
    {
        using var overflow = new MemoryStream();
        var result = await Run("bash", ["-c", "head -c 3000 /dev/zero | tr '\\0' 'a'"], max: 1000, overflow: overflow);

        Assert.True(result.Truncated);
        Assert.Equal(1000, result.Stdout.Length + result.Stderr.Length);
        Assert.Equal(2000, overflow.Length);
    }

    [Fact]
    public void The_child_environment_never_carries_the_service_secret()
    {
        var env = ChildEnvironment.Build(new Hashtable
        {
            ["SANDBOX_SECRET"] = "service-secret",
            ["MY_API_SECRET"] = "x",
            ["AWS_ACCESS_KEY_ID"] = "x",
            ["HTTPS_PROXY"] = "http://sbx-cody:proxy@gateway:8082",
            ["PATH"] = "/usr/bin",
            ["NUGET_PACKAGES"] = "/cache/nuget",
            ["DOTNET_NOLOGO"] = "1",
        });

        Assert.DoesNotContain(env, kv => kv.Value.Contains("service-secret") || kv.Key.Contains("SECRET") || kv.Key.StartsWith("AWS"));
        Assert.Equal("http://sbx-cody:proxy@gateway:8082", env["https_proxy"]);
        Assert.Equal("/tmp/home", env["HOME"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("/cache/nuget", env["NUGET_PACKAGES"]);
    }

    [Fact]
    public async Task Children_see_only_the_built_environment()
    {
        var result = await ProcessRunner.RunAsync(new ProcessSpec("env", [], Path.GetTempPath(), TimeSpan.FromSeconds(10), 1 << 16,
            new Dictionary<string, string> { ["PATH"] = Env["PATH"], ["ONLY"] = "this" }), default);

        var lines = Encoding.UTF8.GetString(result.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "ONLY=this", $"PATH={Env["PATH"]}" }, lines.Order());
    }
}
