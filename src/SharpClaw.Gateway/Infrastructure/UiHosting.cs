using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Configuration;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Client-listener-only pipeline (3.3 spec section 6): security headers on every response,
/// compression (never for <c>text/event-stream</c>), and the built web UI with an SPA fallback.
/// The agent listener gets none of it, so UI paths there are plain 404s.
/// </summary>
public static class UiHosting
{
    /// <summary>Strict: no inline scripts or styles, nothing from another origin.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; manifest-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public const string ImmutableCache = "public, max-age=31536000, immutable";

    // Compressible types. text/event-stream is deliberately absent: compression would buffer the stream.
    private static readonly string[] CompressibleTypes =
    [
        "text/html", "text/css", "text/plain", "application/javascript", "text/javascript", "application/json",
        "image/svg+xml", "application/manifest+json",
    ];

    private static readonly string[] ReservedPrefixes = ["/api", "/openapi", "/healthz"];

    public static void AddUiHosting(this IServiceCollection services)
    {
        services.AddResponseCompression(o =>
        {
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = CompressibleTypes;
        });
    }

    /// <summary>The UI folder: <c>Ui:RootPath</c>, else <c>wwwroot</c> next to the gateway binaries.</summary>
    public static string ResolveRoot(UiOptions options) =>
        Path.GetFullPath(options.RootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"));

    public static void UseClientListenerPipeline(this WebApplication app)
    {
        var ports = app.Services.GetRequiredService<IOptions<ListenerOptions>>().Value;
        var root = ResolveRoot(app.Services.GetRequiredService<IOptions<UiOptions>>().Value);
        // The UI is optional (local `dotnet run`, most tests): without index.html only the headers apply.
        var hasUi = File.Exists(Path.Combine(root, "index.html"));

        app.UseWhen(http => http.Connection.LocalPort == ports.ClientPort, client =>
        {
            client.Use(AddSecurityHeaders);
            client.UseResponseCompression();
            if (!hasUi)
                return;

            var files = new PhysicalFileProvider(root);
            client.UseWhen(http => !IsReserved(http.Request.Path), ui =>
            {
                ui.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                        ctx.Context.Request.Path.StartsWithSegments("/assets") ? ImmutableCache : "no-cache",
                });
                ui.Use((http, next) => SpaFallback(http, next, files));
            });
        });
    }

    private static bool IsReserved(PathString path) =>
        ReservedPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Client-side routes (no file extension) get <c>index.html</c>; a missing path with an
    /// extension is a missing asset and falls through to a 404.
    /// </summary>
    private static async Task SpaFallback(HttpContext http, Func<Task> next, IFileProvider files)
    {
        var request = http.Request;
        if ((HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) &&
            http.GetEndpoint() is null && !Path.HasExtension(request.Path.Value))
        {
            var index = files.GetFileInfo("index.html");
            if (index.Exists)
            {
                http.Response.ContentType = "text/html; charset=utf-8";
                http.Response.Headers.CacheControl = "no-cache";
                await http.Response.SendFileAsync(index, http.RequestAborted);
                return;
            }
        }
        await next();
    }

    private static Task AddSecurityHeaders(HttpContext http, Func<Task> next)
    {
        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            // Plain HTTP on the LAN: never Strict-Transport-Security or upgrade-insecure-requests.
            if (http.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                headers.ContentSecurityPolicy = ContentSecurityPolicy;
            var path = http.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/openapi"))
                headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        return next();
    }
}
