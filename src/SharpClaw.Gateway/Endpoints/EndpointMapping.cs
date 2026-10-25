using SharpClaw.Gateway.Endpoints;

namespace SharpClaw.Gateway;

public static partial class GatewayEndpoints
{
    static partial void MapClientEndpoints(WebApplication app, RouteGroupBuilder client)
    {
        AgentsEndpoints.Map(client);
    }

    static partial void MapAgentEndpoints(WebApplication app, RouteGroupBuilder agent, RouteGroupBuilder v1)
    {
        // Agent-listener endpoints (inbox, threads, reply/fail, chat) are added in later tasks.
    }
}
