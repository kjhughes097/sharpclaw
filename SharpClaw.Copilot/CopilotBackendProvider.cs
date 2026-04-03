using GitHub.Copilot.SDK;
using SharpClaw.Core;

namespace SharpClaw.Copilot;

public sealed class CopilotBackendProvider : IAgentBackendProvider
{
    public const string Name = "copilot";
    public const string WorkspaceEnvVar = "SHARPCLAW_WORKSPACE";
    public const string GitHubTokenEnvVar = "GITHUB_TOKEN";
    public const string CopilotTokenEnvVar = "GITHUB_COPILOT_TOKEN";

    public string BackendName => Name;

    public IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        return new CopilotBackend(
            permissionGate,
            Environment.GetEnvironmentVariable(WorkspaceEnvVar) ?? Environment.CurrentDirectory);
    }

    public async Task<IReadOnlyList<BackendModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        await using var client = CreateCopilotClient();
        await client.StartAsync(cancellationToken);

        var response = await client.ListModelsAsync(cancellationToken);
        return response
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new BackendModelInfo(
                model.Id,
                string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name))
            .ToList();
    }

    private static CopilotClient CreateCopilotClient()
    {
        var options = new CopilotClientOptions
        {
            Cwd = Environment.GetEnvironmentVariable(WorkspaceEnvVar) ?? Environment.CurrentDirectory,
        };

        var token = Environment.GetEnvironmentVariable(GitHubTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            token = Environment.GetEnvironmentVariable(CopilotTokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            token = TryGetGhToken();

        if (!string.IsNullOrWhiteSpace(token))
            options.GitHubToken = token;

        return new CopilotClient(options);
    }

    private static string? TryGetGhToken()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 && output.StartsWith("gho_") ? output : null;
        }
        catch
        {
            return null;
        }
    }
}