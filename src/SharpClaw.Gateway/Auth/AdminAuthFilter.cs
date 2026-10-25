using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Auth;

/// <summary>Validates the admin key on the client listener.</summary>
public sealed class AdminAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sp = http.RequestServices;
        var adminKey = sp.GetRequiredService<IOptions<AuthOptions>>().Value.AdminKey;
        var metrics = sp.GetRequiredService<GatewayMetrics>();

        var presented = BearerToken.Read(http.Request);
        if (presented is null || !Hashing.FixedTimeEquals(presented, adminKey))
        {
            metrics.RecordAuthFailure("client");
            return ApiResults.Unauthorized();
        }

        return await next(context);
    }
}

public static class BearerToken
{
    public static string? Read(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
