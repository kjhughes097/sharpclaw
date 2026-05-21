using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Workers;

namespace SharpClaw.Api;

internal static class SkillEndpoints
{
    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills").WithTags("Skills");

        group.MapGet("/", (ISkillRegistry registry) =>
            registry.GetAll().OrderBy(s => s.Name).Select(s => new
            {
                s.Name,
                s.Description,
            }));

        group.MapGet("/{name}", (string name, ISkillRegistry registry, IOptions<SharpClawOptions> options, IHostEnvironment env) =>
        {
            var skill = registry.Get(name);
            if (skill is null) return Results.NotFound();

            var filePath = ResolveSkillFile(name, options.Value, env);
            var rawContent = File.Exists(filePath) ? File.ReadAllText(filePath) : null;

            return Results.Ok(new
            {
                skill.Name,
                skill.Description,
                skill.PromptText,
                skill.Command,
                skill.Args,
                RawContent = rawContent,
            });
        });

        group.MapPut("/{name}", async (string name, SkillWriteRequest request, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var filePath = ResolveSkillFile(name, options.Value, env);
            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, request.Content);
            registryWorker.Reload();
            return Results.NoContent();
        });

        group.MapPost("/", async (SkillCreateRequest request, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var safeName = SanitizeName(request.Name);
            if (string.IsNullOrEmpty(safeName)) return Results.BadRequest("Invalid skill name.");

            var filePath = ResolveSkillFile(safeName, options.Value, env);
            if (File.Exists(filePath)) return Results.Conflict($"Skill '{safeName}' already exists.");

            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, request.Content);
            registryWorker.Reload();
            return Results.Created($"/api/skills/{safeName}", null);
        });

        group.MapDelete("/{name}", (string name, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var filePath = ResolveSkillFile(name, options.Value, env);
            if (!File.Exists(filePath)) return Results.NotFound();

            File.Delete(filePath);
            registryWorker.Reload();
            return Results.NoContent();
        });
    }

    private static string ResolveSkillFile(string name, SharpClawOptions options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.SkillsDirectory)
            ? options.SkillsDirectory
            : Path.Combine(env.ContentRootPath, options.SkillsDirectory);
        return Path.Combine(dir, $"{name}.skill.md");
    }

    private static string? SanitizeName(string name)
    {
        var sanitized = name.Trim().ToLowerInvariant();
        if (sanitized.Length == 0) return null;
        if (sanitized.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) return null;
        return sanitized;
    }
}

internal sealed record SkillWriteRequest(string Content);
internal sealed record SkillCreateRequest(string Name, string Content);
