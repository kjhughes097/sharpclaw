using SharpClaw.Api.Models;

namespace SharpClaw.Api.Middleware;

public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/health", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.Value?.EndsWith("/stream", StringComparison.Ordinal) == true &&
            HttpMethods.IsGet(context.Request.Method))
        {
            await next(context);
            return;
        }

        var expectedApiKey = configuration["ApiKey"]
            ?? Environment.GetEnvironmentVariable("SHARPCLAW_API_KEY");

        if (string.IsNullOrEmpty(expectedApiKey))
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var providedKey) ||
            !string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ErrorResponse("Missing or invalid API key."));
            return;
        }

        await next(context);
    }
}