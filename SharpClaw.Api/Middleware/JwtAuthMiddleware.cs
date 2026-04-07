using SharpClaw.Api.Models;
using SharpClaw.Api.Services;

namespace SharpClaw.Api.Middleware;

public sealed class JwtAuthMiddleware(RequestDelegate next)
{
    private const string CookieName = "sharpclaw_auth";

    public async Task InvokeAsync(HttpContext context, AuthService authService, JwtTokenService jwtTokenService)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/health", StringComparison.Ordinal) ||
            context.Request.Path.StartsWithSegments("/api/auth/status", StringComparison.Ordinal) ||
            context.Request.Path.StartsWithSegments("/api/auth/setup", StringComparison.Ordinal) ||
            context.Request.Path.StartsWithSegments("/api/auth/login", StringComparison.Ordinal) ||
            context.Request.Path.StartsWithSegments("/api/auth/logout", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (!authService.IsConfigured())
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Login is not configured. Complete setup first."));
            return;
        }

        var token = ReadToken(context);
        if (string.IsNullOrWhiteSpace(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Authentication required."));
            return;
        }

        var principal = jwtTokenService.Validate(token);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Invalid or expired authentication token."));
            return;
        }

        context.User = principal;
        await next(context);
    }

    private static string? ReadToken(HttpContext context)
    {
        if (context.Request.Headers.Authorization.Count > 0)
        {
            var header = context.Request.Headers.Authorization.ToString();
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return header[prefix.Length..].Trim();
        }

        return context.Request.Cookies.TryGetValue(CookieName, out var cookieValue)
            ? cookieValue
            : null;
    }
}
