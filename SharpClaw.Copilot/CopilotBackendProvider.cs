using GitHub.Copilot.SDK;
using SharpClaw.Core;

namespace SharpClaw.Copilot;

public sealed class CopilotBackendProvider(SessionStore store) : IAgentBackendProvider
{
    public const string Name = "copilot";
    public const string GitHubTokenEnvVar = "GITHUB_TOKEN";
    public const string CopilotTokenEnvVar = "GITHUB_COPILOT_TOKEN";

    public string BackendName => Name;

    public IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        return new CopilotBackend(
            permissionGate,
            store.GetWorkspacePath());
    }

    public async Task<IReadOnlyList<BackendModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        await using var client = CreateCopilotClient(store.GetWorkspacePath());
        await client.StartAsync(cancellationToken);

        var response = await client.ListModelsAsync(cancellationToken);
        return response
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => new BackendModelInfo(
                model.Id,
                string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name))
            .ToList();
    }

    private static CopilotClient CreateCopilotClient(string workspacePath)
    {
        var options = new CopilotClientOptions
        {
            Cwd = workspacePath,
        };

        options.GitHubToken = BackendProviderUtilities.GetRequiredEnvironmentVariable(GitHubTokenEnvVar);

        return new CopilotClient(options);
    }
}