using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SharpClaw.RebuildHook.Services;

public class DockerComposeService
{
    private readonly WebhookSettings _settings;
    private readonly ILogger<DockerComposeService> _logger;

    public DockerComposeService(IOptions<WebhookSettings> options, ILogger<DockerComposeService> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    public async Task RebuildAsync(string service)
    {
        if (!await RunCommandAsync("docker", $"compose build {service}"))
            return;

        await RunCommandAsync("docker", $"compose up -d {service}");
    }

    private async Task<bool> RunCommandAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = _settings.ComposeDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
            _logger.LogDebug("stdout [{Command}]: {Output}", arguments, stdout);

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("stderr [{Command}]: {Output}", arguments, stderr);

        if (process.ExitCode != 0)
        {
            _logger.LogError("Command '{Command}' exited with code {ExitCode}. stderr: {Stderr}",
                $"{fileName} {arguments}", process.ExitCode, stderr);
            return false;
        }

        return true;
    }
}
