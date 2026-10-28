using SharpClaw.Gateway.Auth;

namespace SharpClaw.Gateway;

public static class AuthRegistration
{
    public static WebApplicationBuilder AddSharpClawAuth(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<AgentAuthFilter>();
        builder.Services.AddScoped<SessionService>();
        builder.Services.AddSingleton<LoginRateLimiter>();
        builder.Services.AddSingleton<SessionPruningService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionPruningService>());
        return builder;
    }
}
