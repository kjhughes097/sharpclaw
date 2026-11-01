using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>Maps every gateway endpoint into its listener-separated route group.</summary>
public static class EndpointMapping
{
    public static WebApplication MapSharpClawEndpoints(this WebApplication app)
    {
        // Client listener: login, session info and user management choose their own credentials;
        // everything else accepts the admin key or a session cookie (+ CSRF for unsafe methods).
        var api = app.MapGroup("/api").RequireListener(Listener.Client).ProducesApiErrors();
        AuthEndpoints.Map(api);
        var client = api.MapGroup("").AddEndpointFilter(new ClientAuthFilter(ClientAuthMode.AdminKeyOrSession));
        AgentsEndpoints.Map(client);
        ThreadsEndpoints.Map(client);
        AuditEndpoints.Map(client);
        ApprovalEndpoints.MapClient(client);
        ApprovalGrantEndpoints.Map(client);
        SystemEndpoints.Map(client);
        UsageEndpoints.Map(client);
        ActivityEndpoints.Map(client);
        CreditsEndpoints.Map(client);

        // Agent listener: everything needs an agent token.
        var agent = app.MapGroup("/agent").RequireListener(Listener.Agent).AddEndpointFilter<AgentAuthFilter>();
        AgentEndpoints.Map(agent);
        ToolCallEndpoints.Map(agent);
        ApprovalEndpoints.MapAgent(agent);
        ToolBrokerEndpoints.Map(agent);
        SkillEndpoints.Map(agent);

        var v1 = app.MapGroup("/v1").RequireListener(Listener.Agent).AddEndpointFilter<AgentAuthFilter>();
        ChatEndpoints.Map(v1);
        return app;
    }
}
