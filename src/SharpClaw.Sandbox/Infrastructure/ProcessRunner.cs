using System.Diagnostics;

namespace SharpClaw.Sandbox.Infrastructure;

/// <summary>A child process to run: an argv array (never a shell string), a working directory and limits.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaxOutputBytes,
    IReadOnlyDictionary<string, string> Environment,
    byte[]? Stdin = null,
    Stream? Overflow = null);

/// <summary>
/// What a child did. <see cref="Stdout"/> and <see cref="Stderr"/> share one byte budget; anything
/// beyond it went to the overflow stream (if any) and <see cref="Truncated"/> is set.
/// </summary>
public sealed record ProcessResult(int ExitCode, bool TimedOut, byte[] Stdout, byte[] Stderr, bool Truncated);

/// <summary>
/// Runs child processes for the sandbox operations (spec section 6). Each starts through
/// <c>setsid</c>, so it leads its own process group, and on timeout or completion the whole group
/// is killed, so nothing it started lingers between runs.
/// </summary>
public static class ProcessRunner
{
    private const int ChunkBytes = 16 * 1024;

    public static async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory,
        };
        psi.ArgumentList.Add("--wait");
        psi.ArgumentList.Add(spec.FileName);
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        psi.Environment.Clear();
        foreach (var (name, value) in spec.Environment)
            psi.Environment[name] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start");
        var group = process.Id; // setsid makes the child its own group leader
        var budget = new OutputBudget(spec.MaxOutputBytes, spec.Overflow);
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var readOut = CaptureAsync(process.StandardOutput.BaseStream, stdout, budget);
        var readErr = CaptureAsync(process.StandardError.BaseStream, stderr, budget);
        var writeIn = FeedAsync(process.StandardInput.BaseStream, spec.Stdin);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(spec.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
        }
        finally
        {
            // Always: whatever the command left running in its group dies with it.
            Native.KillGroup(group);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        await process.WaitForExitAsync(CancellationToken.None);

        // A process that escaped the group could still hold the pipes open: don't wait forever.
        try { await Task.WhenAll(readOut, readErr, writeIn).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (TimeoutException) { }
        ct.ThrowIfCancellationRequested();

        return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, stdout.ToArray(), stderr.ToArray(), budget.Truncated);
    }

    private static async Task FeedAsync(Stream stdin, byte[]? data)
    {
        try
        {
            if (data is not null)
                await stdin.WriteAsync(data);
        }
        catch (IOException)
        {
            // The child stopped reading (exited early): nothing more to feed.
        }
        finally
        {
            try { stdin.Close(); } catch (IOException) { }
        }
    }

    private static async Task CaptureAsync(Stream source, MemoryStream kept, OutputBudget budget)
    {
        var buffer = new byte[ChunkBytes];
        try
        {
            int n;
            while ((n = await source.ReadAsync(buffer)) > 0)
                await budget.TakeAsync(buffer.AsMemory(0, n), kept);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>One byte budget shared by stdout and stderr; the rest goes to the overflow stream.</summary>
    private sealed class OutputBudget(int max, Stream? overflow)
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private int _remaining = max;

        public bool Truncated { get; private set; }

        public async Task TakeAsync(ReadOnlyMemory<byte> chunk, MemoryStream kept)
        {
            await _lock.WaitAsync();
            try
            {
                var keep = Math.Min(_remaining, chunk.Length);
                kept.Write(chunk.Span[..keep]);
                _remaining -= keep;
                if (keep < chunk.Length)
                {
                    Truncated = true;
                    if (overflow is not null)
                        await overflow.WriteAsync(chunk[keep..]);
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
