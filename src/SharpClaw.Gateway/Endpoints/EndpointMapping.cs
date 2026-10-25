using SharpClaw.Gateway.Endpoints;

namespace SharpClaw.Gateway;

public static partial class GatewayEndpoints
{
    static partial void MapClientEndpoints(WebApplication app, RouteGroupBuilder client)
    {
        AgentsEndpoints.Map(client);
        ThreadsEndpoints.Map(client);
        AuditEndpoints.Map(client);
    }

    static partial void MapAgentEndpoints(WebApplication app, RouteGroupBuilder agent, RouteGroupBuilder v1)
    {
        AgentEndpoints.Map(agent);
        ChatEndpoints.Map(v1);
    }
}
