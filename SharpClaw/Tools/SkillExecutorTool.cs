using System.Diagnostics;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Tools;

public sealed class SkillExecutorTool(ISkillRegistry skillRegistry, ILogger<SkillExecutorTool> logger) : ITool
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);

    public string Name => "execute_skill";
    public string Description => "Execute a skill's script/command and return the output.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("skill_name", "string", "The name of the skill to execute.", Required: true),
        new("input", "string", "Input to pass to the skill script via stdin.", Required: false),
    ];

    public async Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var skillName = context.GetString("skill_name");
        var input = context.GetString("input");

        var skill = skillRegistry.Get(skillName);
        if (skill is null)
            return $"Error: skill '{skillName}' not found.";

        if (string.IsNullOrEmpty(skill.Command))
            return $"Error: skill '{skillName}' has no executable command configured.";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = skill.Command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = !string.IsNullOrEmpty(input),
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (skill.Args is not null)
            {
                foreach (var arg in skill.Args)
                    psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
                return "Error: failed to start skill process.";

            if (!string.IsNullOrEmpty(input))
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ExecutionTimeout);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                logger.LogWarning("Skill {Name} exited with code {Code}: {Stderr}", skillName, process.ExitCode, stderr);
                return $"Exit code {process.ExitCode}:\n{stderr}";
            }

            return stdout;
        }
        catch (OperationCanceledException)
        {
            return $"Error: skill '{skillName}' execution timed out.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Skill execution failed: {Name}", skillName);
            return $"Error: {ex.Message}";
        }
    }
}
