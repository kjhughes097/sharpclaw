using Microsoft.Extensions.Logging;
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
        BrokerTools.GitHubWrite, BrokerTools.GitHubCreatePr, BrokerTools.AskAgent,
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
            new GitHubCreatePrTool(gateway, policy, root),
            new AskAgentTool(gateway, policy),
        ];
    }

    /// <summary>
    /// The enabled tools, minus <c>ask_agent</c> when the gateway lets this agent ask nobody (a tool
    /// with no possible recipient would only confuse the model).
    /// </summary>
    public static IReadOnlyList<string> DropIfNoRecipients(IReadOnlyList<string> enabled, BrokerPolicy policy, ILogger logger)
    {
        if (!enabled.Contains(BrokerTools.AskAgent) || policy.AskAgents.Count > 0)
            return enabled;
        logger.LogWarning("ask_agent is enabled but the gateway allows no recipients; not offering it");
        return enabled.Where(t => t != BrokerTools.AskAgent).ToList();
    }
}
