using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway;

/// <summary>Maps all gateway endpoints into their listener-separated route groups.</summary>
public static partial class GatewayEndpoints
{
    public static void Map(WebApplication app)
    {
        var client = app.MapGroup("/api")
            .RequireListener(Listener.Client)
            .AddEndpointFilter<AdminAuthFilter>();

        var agent = app.MapGroup("/agent")
            .RequireListener(Listener.Agent)
            .AddEndpointFilter<AgentAuthFilter>();

        var v1 = app.MapGroup("/v1")
            .RequireListener(Listener.Agent)
            .AddEndpointFilter<AgentAuthFilter>();

        MapClientEndpoints(app, client);
        MapAgentEndpoints(app, agent, v1);
    }

    static partial void MapClientEndpoints(WebApplication app, RouteGroupBuilder client);
    static partial void MapAgentEndpoints(WebApplication app, RouteGroupBuilder agent, RouteGroupBuilder v1);
}
