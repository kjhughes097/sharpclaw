using System.Text.Json;
using Microsoft.Extensions.Options;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Workers;

namespace SharpClaw.Api;

internal static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agents").WithTags("Agents");

        group.MapGet("/", (IAgentRegistry registry) =>
            registry.GetAll().OrderBy(a => a.Name).Select(a => new
            {
                a.Name,
                a.Description,
                a.Llm,
                a.Model,
                ToolNames = a.ToolNames,
                McpNames = a.McpNames,
                SkillNames = a.SkillNames,
                SubAgentNames = a.SubAgentNames,
            }));

        group.MapGet("/{name}", (string name, IAgentRegistry registry, IOptions<SharpClawOptions> options, IHostEnvironment env) =>
        {
            var agent = registry.Get(name);
            if (agent is null) return Results.NotFound();

            var filePath = ResolveAgentFile(name, options.Value, env);
            var rawContent = File.Exists(filePath) ? File.ReadAllText(filePath) : null;

            return Results.Ok(new
            {
                agent.Name,
                agent.Description,
                agent.Llm,
                agent.Model,
                agent.ToolNames,
                agent.McpNames,
                agent.SkillNames,
                agent.SubAgentNames,
                agent.SystemPrompt,
                RawContent = rawContent,
            });
        });

        group.MapPut("/{name}", async (string name, AgentWriteRequest request, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var filePath = ResolveAgentFile(name, options.Value, env);
            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, request.Content);
            registryWorker.Reload();
            return Results.NoContent();
        });

        group.MapPost("/", async (AgentCreateRequest request, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var safeName = SanitizeName(request.Name);
            if (string.IsNullOrEmpty(safeName)) return Results.BadRequest("Invalid agent name.");

            var filePath = ResolveAgentFile(safeName, options.Value, env);
            if (File.Exists(filePath)) return Results.Conflict($"Agent '{safeName}' already exists.");

            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(filePath, request.Content);
            registryWorker.Reload();
            return Results.Created($"/api/agents/{safeName}", null);
        });

        group.MapDelete("/{name}", (string name, IOptions<SharpClawOptions> options, IHostEnvironment env, RegistryWorker registryWorker) =>
        {
            var filePath = ResolveAgentFile(name, options.Value, env);
            if (!File.Exists(filePath)) return Results.NotFound();

            File.Delete(filePath);
            registryWorker.Reload();
            return Results.NoContent();
        });

        group.MapGet("/activity", (IAgentRegistry registry, IOptions<SharpClawOptions> options) =>
        {
            var workspacePath = options.Value.WorkspacePath;
            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
                return Results.Ok(Array.Empty<object>());

            var agents = registry.GetAll().OrderBy(a => a.Name).ToList();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            var result = new List<object>();

            foreach (var agent in agents)
            {
                var sessionsDir = Path.Combine(workspacePath, agent.Name, "sessions");
                var dailyCounts = new Dictionary<string, int>();

                // Initialize all 30 days with 0
                for (var i = 29; i >= 0; i--)
                {
                    var day = DateTimeOffset.UtcNow.AddDays(-i).ToString("yyyy-MM-dd");
                    dailyCounts[day] = 0;
                }

                if (Directory.Exists(sessionsDir))
                {
                    foreach (var file in Directory.GetFiles(sessionsDir, "*.transcript.jsonl"))
                    {
                        foreach (var line in File.ReadLines(file))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                using var doc = JsonDocument.Parse(line);
                                if (doc.RootElement.TryGetProperty("timestampUtc", out var ts))
                                {
                                    var timestamp = ts.GetDateTimeOffset();
                                    if (timestamp >= cutoff)
                                    {
                                        var day = timestamp.ToString("yyyy-MM-dd");
                                        if (dailyCounts.ContainsKey(day))
                                            dailyCounts[day]++;
                                    }
                                }
                            }
                            catch { /* skip malformed lines */ }
                        }
                    }
                }

                result.Add(new
                {
                    agent.Name,
                    agent.Description,
                    agent.Llm,
                    agent.Model,
                    ToolNames = agent.ToolNames,
                    SkillNames = agent.SkillNames,
                    Activity = dailyCounts.OrderBy(kv => kv.Key).Select(kv => new { Date = kv.Key, Turns = kv.Value }).ToList(),
                });
            }

            return Results.Ok(result);
        });
    }

    private static string ResolveAgentFile(string name, SharpClawOptions options, IHostEnvironment env)
    {
        var dir = Path.IsPathRooted(options.AgentsDirectory)
            ? options.AgentsDirectory
            : Path.Combine(env.ContentRootPath, options.AgentsDirectory);
        return Path.Combine(dir, $"{name}.agent.md");
    }

    private static string? SanitizeName(string name)
    {
        var sanitized = name.Trim().ToLowerInvariant();
        if (sanitized.Length == 0) return null;
        if (sanitized.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) return null;
        if (sanitized.Contains("..") || sanitized.Contains('/') || sanitized.Contains('\\')) return null;
        return sanitized;
    }
}

internal sealed record AgentWriteRequest(string Content);
internal sealed record AgentCreateRequest(string Name, string Content);
