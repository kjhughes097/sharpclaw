using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools.Broker;

/// <summary>The broker tools this agent build knows (each external tools task adds its own).</summary>
public static class AgentBrokerTools
{
    public static IReadOnlyCollection<string> Names { get; } =
    [
        BrokerTools.HttpFetch, BrokerTools.DownloadToWorkspace, BrokerTools.WebSearch, BrokerTools.ExtractText,
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
        ];
    }
}
