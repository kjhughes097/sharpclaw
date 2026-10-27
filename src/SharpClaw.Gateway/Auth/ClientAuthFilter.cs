using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Auth;

/// <summary>Which client credentials a route accepts.</summary>
public enum ClientAuthMode
{
    /// <summary>Admin-key bearer or session cookie (most of <c>/api</c>).</summary>
    AdminKeyOrSession,

    /// <summary>Admin-key bearer only (<c>/api/users*</c>): a stolen session can't manage users.</summary>
    AdminKeyOnly,

    /// <summary>Session cookie only (logout, password change).</summary>
    SessionOnly,
}

/// <summary>Who is calling a client route. <c>Session</c> is set for cookie authentication.</summary>
public sealed record ClientPrincipal(string Method, Session? Session);

public static class ClientAuthMethods
{
    public const string AdminKey = "admin_key";
    public const string Session = "session";
}

/// <summary>
/// Authenticates the client listener (pre-3.3 spec sections 4.1 and 4.5). Bearer requests carry
/// no ambient credential and are exempt from CSRF checks; cookie-authenticated unsafe methods
/// need a same-origin <c>Origin</c>/<c>Referer</c> and the session's <c>X-CSRF-Token</c>.
/// </summary>
public sealed class ClientAuthFilter(ClientAuthMode mode) : IEndpointFilter
{
    private const string Key = "sharpclaw.client";

    public static ClientPrincipal? Principal(HttpContext http) => http.Items[Key] as ClientPrincipal;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sp = http.RequestServices;
        var auth = sp.GetRequiredService<IOptions<AuthOptions>>().Value;
        var metrics = sp.GetRequiredService<GatewayMetrics>();

        var bearer = BearerToken.Read(http.Request);
        if (bearer is not null || mode == ClientAuthMode.AdminKeyOnly)
        {
            if (mode == ClientAuthMode.SessionOnly || bearer is null || !Hashing.FixedTimeEquals(bearer, auth.AdminKey))
            {
                metrics.RecordAuthFailure("client", ClientAuthMethods.AdminKey);
                return ApiResults.Unauthorized(mode == ClientAuthMode.AdminKeyOnly ? "Admin key required." : "Missing or invalid credentials.");
            }
            http.Items[Key] = new ClientPrincipal(ClientAuthMethods.AdminKey, null);
            return await next(context);
        }

        var session = await sp.GetRequiredService<SessionService>().ResolveAsync(http.Request, http.RequestAborted);
        if (session is null)
        {
            metrics.RecordAuthFailure("client", ClientAuthMethods.Session);
            return ApiResults.Unauthorized();
        }

        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) &&
            !HttpMethods.IsOptions(http.Request.Method))
        {
            if (!OriginCheck.IsAllowed(http.Request, auth.AllowedOrigins))
            {
                metrics.RecordAuthFailure("client", "origin");
                return CsrfFailed();
            }
            var presented = http.Request.Headers["X-CSRF-Token"].ToString();
            if (string.IsNullOrEmpty(presented) || !Hashing.FixedTimeEquals(presented, session.CsrfToken))
            {
                metrics.RecordAuthFailure("client", "csrf");
                return CsrfFailed();
            }
        }

        http.Items[Key] = new ClientPrincipal(ClientAuthMethods.Session, session);
        return await next(context);
    }

    public static IResult CsrfFailed() => ApiResults.Error(403, ErrorCodes.CsrfFailed, "Cross-site request rejected.");
}

/// <summary>
/// The request's <c>Origin</c> (else <c>Referer</c>) must be the request's own origin or one of
/// <c>Auth:AllowedOrigins</c>. Both missing is a rejection.
/// </summary>
public static class OriginCheck
{
    public static bool IsAllowed(HttpRequest request, IReadOnlyCollection<string> allowedOrigins)
    {
        string? origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            var referer = request.Headers.Referer.ToString();
            origin = Uri.TryCreate(referer, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
        }
        if (string.IsNullOrEmpty(origin) || origin == "null")
            return false;

        var own = $"{request.Scheme}://{request.Host.Value}";
        return Same(origin, own) || allowedOrigins.Any(a => Same(origin, a));
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
