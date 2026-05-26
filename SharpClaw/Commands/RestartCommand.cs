using System.Diagnostics;
using SharpClaw.Sessions;
using SharpClaw.Workers;

namespace SharpClaw.Commands;

public sealed class RestartCommand(
    AgentSessionRegistry sessionRegistry,
    ServiceRunner serviceRunner,
    IHostEnvironment env,
    ILogger<RestartCommand> logger) : ICommand
{
    public bool CanHandle(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith(".restartf", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith(".restart", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var parts = context.RawText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0];
        var force = command.Equals(".restartf", StringComparison.OrdinalIgnoreCase);
        var target = parts
            .Skip(1)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(target) || target.Equals("sharpclaw", StringComparison.OrdinalIgnoreCase))
        {
            return await RestartSharpClawAsync(force, ct);
        }

        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var scResult = await RestartSharpClawAsync(force, ct);
            if (!scResult.Handled || scResult.ResponseText?.Contains("aborted", StringComparison.OrdinalIgnoreCase) == true)
                return scResult;

            var serviceResults = await RestartAllServicesAsync(ct);
            return new CommandResult(true, $"{scResult.ResponseText}\n\n{serviceResults}");
        }

        return await RestartManagedServiceAsync(target, ct);
    }

    private async Task<CommandResult> RestartSharpClawAsync(bool force, CancellationToken ct)
    {
        // Check for in-flight sessions
        if (!force)
        {
            var activeSessions = sessionRegistry.GetActiveSessions();
            if (activeSessions.Count > 0)
            {
                var sessionList = string.Join(", ", activeSessions.Select(s => s.AgentId));
                return new CommandResult(true,
                    $"⚠️ **{activeSessions.Count} active session(s)**: {sessionList}\n\n" +
                    "Use `.restartf` to restart anyway, or wait for conversations to complete.");
            }
        }

        // Build first
        var repoRoot = GetRepoRoot();
        var projectPath = Path.Combine("SharpClaw", "SharpClaw.csproj");
        var buildResult = await RunBuildAsync(repoRoot, projectPath, ct);

        if (!buildResult.Success)
        {
            logger.LogWarning("Restart aborted — build failed: {Output}", buildResult.Output);
            return new CommandResult(true,
                $"❌ **Build failed** — restart aborted.\n\n```\n{buildResult.Output}\n```");
        }

        // Write restart signal file
        var signalFile = Path.Combine(repoRoot, ".sharpclaw.restart");
        await File.WriteAllTextAsync(signalFile, DateTime.UtcNow.ToString("O"), ct);
        logger.LogInformation("Restart signal written to {SignalFile}", signalFile);

        return new CommandResult(true,
            "✅ **Build succeeded** — restart signal sent. SharpClaw will restart momentarily.");
    }

    private async Task<CommandResult> RestartManagedServiceAsync(string name, CancellationToken ct)
    {
        try
        {
            await serviceRunner.RestartServiceAsync(name, ct);
            return new CommandResult(true, $"✅ Service **{name}** restarted.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to restart service {Name}", name);
            return new CommandResult(true, $"❌ Failed to restart **{name}**: {ex.Message}");
        }
    }

    private async Task<string> RestartAllServicesAsync(CancellationToken ct)
    {
        var services = serviceRunner.GetRunningServices();
        if (services.Count == 0)
            return "No managed services running.";

        var results = new List<string>();
        foreach (var (name, _) in services)
        {
            try
            {
                await serviceRunner.RestartServiceAsync(name, ct);
                results.Add($"✅ {name}");
            }
            catch (Exception ex)
            {
                results.Add($"❌ {name}: {ex.Message}");
            }
        }

        return "**Managed services:**\n" + string.Join("\n", results);
    }

    private string GetRepoRoot()
    {
        // Walk up from ContentRootPath to find the repo root (contains sharpclaw.sh)
        var dir = new DirectoryInfo(env.ContentRootPath);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sharpclaw.sh")))
                return dir.FullName;
            dir = dir.Parent;
        }

        // Fallback: assume one level up from ContentRootPath
        return Path.GetFullPath(Path.Combine(env.ContentRootPath, ".."));
    }

    private static async Task<BuildResult> RunBuildAsync(string workingDir, string projectPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{projectPath}\" --nologo",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            return new BuildResult(false, "Failed to start dotnet build process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var output = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout}\n{stderr}";

        // Trim to last 20 lines max for display
        var lines = output.Split('\n');
        if (lines.Length > 20)
            output = string.Join('\n', lines[^20..]);

        return new BuildResult(process.ExitCode == 0, output.Trim());
    }

    private sealed record BuildResult(bool Success, string Output);
}
