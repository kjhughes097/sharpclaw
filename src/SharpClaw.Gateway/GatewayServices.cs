namespace SharpClaw.Gateway;

/// <summary>Central place to register gateway services (providers, auth, app services).</summary>
public static partial class GatewayServices
{
    public static void Register(WebApplicationBuilder builder)
    {
        RegisterAuth(builder);
        RegisterProviders(builder);
        RegisterServices(builder);
        RegisterTelemetry(builder);
    }

    static partial void RegisterAuth(WebApplicationBuilder builder);
    static partial void RegisterProviders(WebApplicationBuilder builder);
    static partial void RegisterServices(WebApplicationBuilder builder);
    static partial void RegisterTelemetry(WebApplicationBuilder builder);
}
