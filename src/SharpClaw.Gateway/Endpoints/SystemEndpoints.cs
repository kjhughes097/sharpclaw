using System.Reflection;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// <c>GET /api/system</c> (3.3 spec section 5.5): lets the UI show the build and, prominently, when
/// the gateway runs the fake provider; <c>utcNow</c> gives the UI its server clock offset.
/// </summary>
public static class SystemEndpoints
{
    public const string FakeProvider = "Fake";
    public const string OpenRouterProvider = "OpenRouter";

    /// <summary>The informational version (the git short sha in Docker builds, <c>dev</c> locally).</summary>
    public static readonly string Version = ReadVersion();

    public static void Map(RouteGroupBuilder client)
    {
        client.MapGet("/system", Get).WithName("getSystemInfo").WithTags("system").Produces<SystemInfoResponse>();
    }

    private static IResult Get(TimeProvider time, IOptions<ProviderOptions> provider)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var nextMidnight = now.Date.AddDays(1);
        var name = string.Equals(provider.Value.Type, FakeProvider, StringComparison.OrdinalIgnoreCase)
            ? FakeProvider
            : OpenRouterProvider;
        return Results.Json(new SystemInfoResponse(Version, name, now, DateTime.SpecifyKind(nextMidnight, DateTimeKind.Utc)),
            SharpClawJson.Options);
    }

    private static string ReadVersion()
    {
        var raw = typeof(SystemEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        // Drop any "+<source revision>" suffix the SDK may append.
        var version = raw?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "dev" : version;
    }
}
