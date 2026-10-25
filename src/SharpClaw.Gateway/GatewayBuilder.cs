using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway;

public static class GatewayBuilder
{
    /// <summary>Builds the fully-configured gateway app without starting it.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ListenerOptions>(builder.Configuration.GetSection(ListenerOptions.Section));
        builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.Section));
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
        builder.Services.Configure<ProviderOptions>(builder.Configuration.GetSection(ProviderOptions.Section));
        builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection(OpenRouterOptions.Section));
        builder.Services.Configure<FakeOptions>(builder.Configuration.GetSection(FakeOptions.Section));
        builder.Services.Configure<InboxOptions>(builder.Configuration.GetSection(InboxOptions.Section));
        builder.Services.Configure<MessagesOptions>(builder.Configuration.GetSection(MessagesOptions.Section));
        builder.Services.Configure<ApprovalsOptions>(builder.Configuration.GetSection(ApprovalsOptions.Section));

        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        });

        builder.Services.AddSingleton<SqlitePragmaInterceptor>();

        // Test overrides (config sources and service replacements) apply here, before we read
        // provider/listener/database settings and register providers, Kestrel and the DbContext.
        configure?.Invoke(builder);

        GatewayServices.Register(builder);

        var listeners = builder.Configuration.GetSection(ListenerOptions.Section).Get<ListenerOptions>() ?? new ListenerOptions();
        var dbOptions = builder.Configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();

        builder.Services.AddDbContext<SharpClawDbContext>((sp, opt) =>
        {
            opt.UseSqlite($"Data Source={dbOptions.Path}");
            opt.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });

        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(listeners.ClientPort);
            k.ListenAnyIP(listeners.AgentPort);
        });

        var app = builder.Build();

        var auth = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        if (string.IsNullOrWhiteSpace(auth.AdminKey))
            throw new InvalidOperationException("Auth:AdminKey is required (set Auth__AdminKey).");

        foreach (var validator in app.Services.GetServices<IStartupValidator>())
            validator.Validate();

        app.UseMiddleware<ExceptionMiddleware>();

        // Health check: unauthenticated, available on both listeners.
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }, SharpClawJson.Options));

        GatewayEndpoints.Map(app);

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
