namespace SharpClaw.Core;

public interface IAgentBackendProvider
{
    string BackendName { get; }

    IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate);

    Task<IReadOnlyList<BackendModelInfo>> ListModelsAsync(CancellationToken cancellationToken);
}