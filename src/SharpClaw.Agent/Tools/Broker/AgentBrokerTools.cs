using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>The broker tools this agent build knows (each external tools task adds its own).</summary>
public static class AgentBrokerTools
{
    public static IReadOnlyCollection<string> Names { get; } =
    [
        BrokerTools.HttpFetch, BrokerTools.DownloadToWorkspace, BrokerTools.WebSearch, BrokerTools.ExtractText,
        BrokerTools.GitClone, BrokerTools.SandboxRun, BrokerTools.GitHubRead,
        BrokerTools.GitHubWrite,
    ];

    public static IAgentTool[] All(IGatewayClient gateway, BrokerPolicy policy, string workspacePath)
    {
        var root = new WorkspaceRoot(workspacePath);
        return
        [
            new HttpFetchTool(gateway, policy),
            new DownloadToWorkspaceTool(gateway, policy, root),
            new WebSearchTool(gateway, policy),
            new ExtractTextTool(gateway, policy, root),
            new GitCloneTool(gateway, policy, root),
            new SandboxRunTool(gateway, policy, root),
            new GitHubReadTool(gateway, policy),
            new GitHubWriteTool(gateway, policy),
        ];
    }
}
