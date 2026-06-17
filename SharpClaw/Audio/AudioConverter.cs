using System.Diagnostics;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Audio;

public sealed class AudioConverter(IOptions<SttOptions> options, ILogger<AudioConverter> logger)
{
    private readonly string _ffmpegPath = options.Value.FfmpegPath;

    public async Task ConvertToWhisperWavAsync(
        Stream input,
        string inputFormatHint,
        Stream output,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("pipe:0");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add("16000");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("wav");
        psi.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start ffmpeg at '{_ffmpegPath}'. Ensure ffmpeg is installed and on PATH.",
                ex);
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        var copyInputTask = Task.Run(async () =>
        {
            try
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, ct);
            }
            finally
            {
                process.StandardInput.Close();
            }
        }, ct);

        var copyOutputTask = process.StandardOutput.BaseStream.CopyToAsync(output, ct);

        await Task.WhenAll(copyInputTask, copyOutputTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            logger.LogWarning(
                "ffmpeg failed with exit code {Code} (input format hint: {Hint}). stderr: {Stderr}",
                process.ExitCode, inputFormatHint, stderr);
            throw new InvalidOperationException(
                $"Audio conversion failed (ffmpeg exit {process.ExitCode}): {stderr}");
        }
    }

    public async Task<bool> IsFfmpegAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(_ffmpegPath, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
