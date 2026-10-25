using SharpClaw.Gateway.Auth;

namespace SharpClaw.Gateway;

public static partial class GatewayServices
{
    static partial void RegisterAuth(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<AdminAuthFilter>();
        builder.Services.AddScoped<AgentAuthFilter>();
    }
}
