using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Egress;
using SharpClaw.Gateway.Endpoints;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway;

public static class GatewayBuilder
{
    /// <summary>Builds the fully-configured gateway app without starting it.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddSharpClawOptions();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.ConfigureHttpJsonOptions(o => SharpClawJson.Apply(o.SerializerOptions));

        builder.Services.AddSingleton<SqlitePragmaInterceptor>();
        builder.Services.AddSharpClawOpenApi();
        builder.Services.AddUiHosting();

        // Test overrides (config sources and service replacements) apply here, before we read
        // provider/listener/database settings and register providers, Kestrel and the DbContext.
        configure?.Invoke(builder);

        builder.AddSharpClawAuth()
            .AddSharpClawProviders()
            .AddSharpClawServices()
            .AddSharpClawTelemetry();

        var listeners = builder.Configuration.GetSection(ListenerOptions.Section).Get<ListenerOptions>() ?? new ListenerOptions();
        var dbOptions = builder.Configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var egress = builder.Configuration.GetSection(EgressOptions.Section).Get<EgressOptions>() ?? new EgressOptions();

        builder.Services.AddDbContext<SharpClawDbContext>((sp, opt) =>
        {
            opt.UseSqlite($"Data Source={dbOptions.Path}");
            opt.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(listeners.ClientPort);
            k.ListenAnyIP(listeners.AgentPort);
            // The egress proxy: a raw connection handler, so no HTTP pipeline, routes or auth
            // handlers run on this port. Bound only when a sandbox credential is configured.
            if (egress.Enabled)
                k.ListenAnyIP(egress.Port, l => l.UseConnectionHandler<EgressConnectionHandler>());
        });

        var app = builder.Build();

        // Fail fast on bad configuration, before migrations or listening.
        try
        {
            foreach (var validator in app.Services.GetServices<IStartupCheck>())
                validator.Validate();
        }
        catch
        {
            ((IDisposable)app).Dispose();
            throw;
        }
        var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;

        // Forwarded headers only from configured proxies, and only when enabled; never trusted otherwise.
        if (auth.ForwardedHeaders.Enabled)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var proxy in auth.ForwardedHeaders.KnownProxies)
                forwarded.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
            app.UseForwardedHeaders(forwarded);
        }

        app.UseMiddleware<ExceptionMiddleware>();
        app.UseClientListenerPipeline();

        // Health check: unauthenticated, available on both listeners.
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }, SharpClawJson.Options)).ExcludeFromDescription();

        app.MapSharpClawEndpoints();
        app.MapSharpClawOpenApi();

        return app;
    }

    /// <summary>Applies EF migrations. Call once at startup before serving.</summary>
    public static void Migrate(WebApplication app)
    {
        var dbOptions = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dbOptions.Path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        db.Database.Migrate();
    }
}
