using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SharpClaw.RebuildHook.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://127.0.0.1:9876");

builder.Services.Configure<WebhookSettings>(
    builder.Configuration.GetSection("WebhookSettings"));

builder.Services.AddSingleton<DockerComposeService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/rebuild", async (
    RebuildRequest req,
    HttpContext context,
    IOptions<WebhookSettings> options,
    DockerComposeService docker,
    ILogger<Program> logger) =>
{
    var settings = options.Value;

    if (!context.Request.Headers.TryGetValue("X-Webhook-Secret", out var secret) ||
        !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret.ToString()),
            Encoding.UTF8.GetBytes(settings.Secret)))
    {
        return Results.Unauthorized();
    }

    if (!settings.AllowedServices.Contains(req.Service, StringComparer.Ordinal))
    {
        return Results.BadRequest(new { error = $"Service '{req.Service}' is not in the allowed services list." });
    }

    logger.LogInformation("Rebuild requested for service '{Service}': {Message}", req.Service, req.Message);

    _ = Task.Run(async () =>
    {
        try
        {
            await docker.RebuildAsync(req.Service);
            logger.LogInformation("Rebuild completed for service '{Service}'", req.Service);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rebuild failed for service '{Service}'", req.Service);
        }
    });

    return Results.Accepted();
});

app.Run();

record RebuildRequest(string Service, string Message);

public record WebhookSettings
{
    public string Secret { get; init; } = string.Empty;
    public string ComposeDirectory { get; init; } = string.Empty;
    public IReadOnlyList<string> AllowedServices { get; init; } = [];
}
