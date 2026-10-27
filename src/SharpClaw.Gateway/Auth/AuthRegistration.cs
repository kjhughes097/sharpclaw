using SharpClaw.Gateway.Auth;

namespace SharpClaw.Gateway;

public static partial class GatewayServices
{
    static partial void RegisterAuth(WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<AgentAuthFilter>();
        builder.Services.AddScoped<SessionService>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<SessionPruningService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionPruningService>());
    }
}
