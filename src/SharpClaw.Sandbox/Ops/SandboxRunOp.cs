using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Sandbox.Infrastructure;

namespace SharpClaw.Sandbox.Ops;

/// <summary>
/// <c>sandbox_run</c> (spec section 7.5): <c>bash -c &lt;command&gt;</c> in a workspace folder. A
/// shell adds nothing to the risk of running arbitrary code, and it is what models expect
/// (<c>npm ci &amp;&amp; npm test</c>). At most 1 MB of stdout and stderr together comes back; the
/// rest goes to <c>logPath</c> in the workspace. The whole process group dies at the timeout and
/// at the end, so nothing lingers between runs.
/// </summary>
public sealed class SandboxRunOp(WorkspaceRoot workspace, IReadOnlyDictionary<string, string> childEnv, TimeProvider time) : IRunnerOp
{
    public const int MaxOutputBytes = 1024 * 1024;
    public const long MaxLogBytes = 100L * 1024 * 1024;
    public const int MaxSeconds = 1800;

    public string Name => BrokerTools.SandboxRun;

    public async Task<OpResult> RunAsync(JsonElement args, CancellationToken ct)
    {
        if (OpArgs.String(args, "command") is not { Length: > 0 } command)
            return OpResult.Error("invalid_arguments", "command is required.");
        var (cwd, error) = workspace.Resolve(OpArgs.String(args, "cwd"), allowRoot: true);
        if (cwd is null)
            return OpResult.Error("invalid_path", $"cwd: {error}");
        if (!Directory.Exists(cwd.FullPath))
            return OpResult.Error("not_found", $"No folder at '{cwd.Relative}'.");
        if (OpArgs.Int(args, "timeoutSeconds", 600, 1, MaxSeconds) is not { } seconds)
            return OpResult.Error("invalid_arguments", $"timeoutSeconds must be 1-{MaxSeconds}.");

        // Output past the cap is kept (up to a limit) on the runner's own tmpfs until the run ends.
        var overflowPath = Path.Combine(Path.GetTempPath(), $"run-{Guid.NewGuid():N}.log");
        await using var overflow = new FileStream(overflowPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
        var sw = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(new ProcessSpec(
            "bash", ["-c", command], cwd.FullPath, TimeSpan.FromSeconds(seconds), MaxOutputBytes, childEnv,
            Overflow: new CappedStream(overflow, MaxLogBytes)), ct);

        string? logPath = null;
        if (result.Truncated && overflow.Length > 0)
        {
            logPath = $".sandbox/logs/run-{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log";
            overflow.Seek(0, SeekOrigin.Begin);
            try
            {
                await NoFollow.WriteAtomicAsync(workspace.Path, logPath, (file, token) => overflow.CopyToAsync(file, token), ct);
            }
            catch (WorkspacePathException)
            {
                logPath = null; // the log folder was tampered with: the run's result still stands
            }
        }

        return OpResult.Success(new
        {
            exitCode = result.ExitCode,
            timedOut = result.TimedOut,
            stdout = Clean(result.Stdout),
            stderr = Clean(result.Stderr),
            truncated = result.Truncated,
            logPath,
            cwd = cwd.Relative,
            durationMs = sw.ElapsedMilliseconds,
        });
    }

    /// <summary>UTF-8 text with control characters (other than tab and newlines) replaced, so it stays compact as JSON.</summary>
    internal static string Clean(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0x7F) ? c : '\uFFFD');
        return sb.ToString();
    }

    /// <summary>Writes through to the inner stream up to a limit, then silently drops the rest.</summary>
    private sealed class CappedStream(Stream inner, long limit) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var take = (int)Math.Min(buffer.Length, Math.Max(0, limit - _written));
            if (take > 0)
                await inner.WriteAsync(buffer[..take], ct);
            _written += take;
        }
    }
}
