using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Models;
using SharpClaw.Workers;

namespace SharpClaw.Api;

internal static class McpEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mcps").WithTags("MCPs");

        group.MapGet("/", (IMcpRegistry registry) =>
            registry.GetAll().Select(kvp => new
            {
                Name = kvp.Key,
                kvp.Value.Transport,
                kvp.Value.Command,
                kvp.Value.Url,
            }).OrderBy(m => m.Name));

        group.MapGet("/{name}", (string name, IOptions<SharpClawOptions> options, IHostEnvironment env) =>
        {
            var filePath = ResolveMcpFile(name, options.Value, env);
            if (!File.Exists(filePath)) return Results.NotFound();

            var json = File.ReadAllText(filePath);
            return Results.Content(json, "application/json");
        });

        group.MapPut("/{name}", async (string name, JsonElement body, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var filePath = ResolveMcpFile(name, options.Value, env);
            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, body.GetRawText());
            registryWorker.Reload();
            return Results.NoContent();
        });

        group.MapPost("/", async (McpCreateRequest request, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var safeName = SanitizeName(request.Name);
            if (string.IsNullOrEmpty(safeName)) return Results.BadRequest("Invalid MCP name.");

            var filePath = ResolveMcpFile(safeName, options.Value, env);
            if (File.Exists(filePath)) return Results.Conflict($"MCP '{safeName}' already exists.");

            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(request.Config, JsonOptions));
            registryWorker.Reload();
            return Results.Created($"/api/mcps/{safeName}", null);
        });

        group.MapDelete("/{name}", (string name, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var filePath = ResolveMcpFile(name, options.Value, env);
            if (!File.Exists(filePath)) return Results.NotFound();

            File.Delete(filePath);
            registryWorker.Reload();
            return Results.NoContent();
        });
    }

    private static string ResolveMcpFile(string name, SharpClawOptions options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.McpsDirectory)
            ? options.McpsDirectory
            : Path.Combine(env.ContentRootPath, options.McpsDirectory);
        return Path.Combine(dir, $"{name}.json");
    }

    private static string? SanitizeName(string name)
    {
        var sanitized = name.Trim().ToLowerInvariant();
        if (sanitized.Length == 0) return null;
        if (sanitized.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) return null;
        return sanitized;
    }
}

internal sealed record McpCreateRequest(string Name, McpServerDefinition Config);
