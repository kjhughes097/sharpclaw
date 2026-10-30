using Microsoft.Extensions.Logging;
using SharpClaw.Agent;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Broker;
using SharpClaw.Agent.Tools.Utility;
using SharpClaw.Agent.Tools.Workspace;

var config = AgentConfig.FromEnvironment();

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddJsonConsole(o => o.IncludeScopes = true);
    if (Enum.TryParse<LogLevel>(config.LogLevel, ignoreCase: true, out var level))
        builder.SetMinimumLevel(level);
});
var logger = loggerFactory.CreateLogger("SharpClaw.Agent");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var gateway = new GatewayHttpClient(http, config);
var soul = new SoulProvider(config, loggerFactory.CreateLogger<SoulProvider>());

// Tool policy is read once, from read-only config. A bad config stops the agent rather than
// silently granting (or dropping) tools.
// Every tool the agent knows; the config picks the subset this agent may offer.
var knownTools = WorkspaceTools.Names.Concat(UtilityTools.Names).Concat(AgentBrokerTools.Names).ToArray();
AgentToolConfig? toolConfig;
try
{
    toolConfig = AgentToolConfig.Load(config.ToolConfigPath, knownTools);
}
catch (AgentToolConfigException ex)
{
    logger.LogError("invalid tool config at {Path}: {Error}", config.ToolConfigPath, ex.Message);
    return 1;
}

AgentTools? tools = null;
if (toolConfig is null)
{
    logger.LogWarning("no tool config at {Path}; running without tools", config.ToolConfigPath);
}
else
{
    // Broker tools need the operator's settings from the gateway; without them, every broker tool
    // asks for approval and no host counts as allow-listed.
    var policy = toolConfig.Tools.Any(AgentBrokerTools.Names.Contains)
        ? await BrokerPolicyLoader.LoadAsync(gateway, logger, cts.Token)
        : BrokerPolicy.Conservative;
    var available = WorkspaceTools.All(config.WorkspacePath)
        .Concat(UtilityTools.All(TimeProvider.System))
        .Concat(AgentBrokerTools.All(gateway, policy, config.WorkspacePath));
    var registry = new ToolRegistry(available, toolConfig.Tools);
    var dispatcher = new ToolDispatcher(registry, toolConfig, gateway, loggerFactory.CreateLogger<ToolDispatcher>());
    tools = new AgentTools(registry, dispatcher, toolConfig.MaxToolIterations);
    logger.LogInformation(
        "tools enabled: {Tools} allowed_risk={AllowedRisk} smart_mode={SmartMode} max_tool_iterations={MaxIterations}",
        string.Join(",", toolConfig.Tools), toolConfig.AllowedRiskLevel.ToWire(), toolConfig.SmartMode, toolConfig.MaxToolIterations);
}

var loop = new AgentLoop(gateway, soul, config, loggerFactory.CreateLogger<AgentLoop>(), TimeProvider.System, tools);
return await loop.RunAsync(cts.Token);
