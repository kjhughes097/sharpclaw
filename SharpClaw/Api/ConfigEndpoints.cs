using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpClaw.Api;

internal static class ConfigEndpoints
{
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config").WithTags("Configuration");

        group.MapGet("/", (IConfiguration configuration) =>
        {
            return new
            {
                SharpClaw = new
                {
                    WorkspacePath = configuration["SharpClaw:WorkspacePath"] ?? "",
                    DefaultAgent = configuration["SharpClaw:DefaultAgent"] ?? "",
                    ChatHistoryLimit = configuration.GetValue<int>("SharpClaw:ChatHistoryLimit", 5),
                },
                Anthropic = new
                {
                    ApiKey = MaskSecret(configuration["Anthropic:ApiKey"]),
                    DefaultModel = configuration["Anthropic:DefaultModel"] ?? "claude-sonnet-4-20250514",
                    MaxTokens = configuration.GetValue<int>("Anthropic:MaxTokens", 8192),
                    IsConfigured = !string.IsNullOrEmpty(configuration["Anthropic:ApiKey"]),
                },
                Telegram = new
                {
                    BotToken = MaskSecret(configuration["Telegram:BotToken"]),
                    AllowedUsers = configuration.GetSection("Telegram:AllowedUsers").Get<string[]>() ?? [],
                    DefaultAgent = configuration["Telegram:DefaultAgent"] ?? "",
                    IsConfigured = !string.IsNullOrEmpty(configuration["Telegram:BotToken"])
                                   && configuration["Telegram:BotToken"] != "YOUR_BOT_TOKEN_HERE",
                },
                OpenTelemetry = new
                {
                    Endpoint = configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317",
                },
                AnthropicAdminMcp = new
                {
                    ApiKey = MaskSecret(configuration["AnthropicAdminMcp:ApiKey"]),
                    MonthlyBudgetUsd = configuration.GetValue<decimal>("AnthropicAdminMcp:MonthlyBudgetUsd", 0),
                    DefaultLookbackDays = configuration.GetValue<int>("AnthropicAdminMcp:DefaultLookbackDays", 7),
                    IsConfigured = !string.IsNullOrEmpty(configuration["AnthropicAdminMcp:ApiKey"]),
                },
            };
        });

        group.MapPut("/", async (JsonElement body, IHostEnvironment env) =>
        {
            var configPath = Path.Combine(env.ContentRootPath, "appsettings.Development.json");

            JsonNode? existing;
            if (File.Exists(configPath))
            {
                var existingJson = await File.ReadAllTextAsync(configPath);
                existing = JsonNode.Parse(existingJson) ?? new JsonObject();
            }
            else
            {
                existing = new JsonObject();
            }

            // Merge the incoming body into the existing config
            var incoming = JsonNode.Parse(body.GetRawText());
            if (incoming is JsonObject incomingObj)
            {
                MergeObjects((JsonObject)existing, incomingObj);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(configPath, existing.ToJsonString(options));

            return Results.NoContent();
        });
    }

    private static void MergeObjects(JsonObject target, JsonObject source)
    {
        foreach (var prop in source)
        {
            if (prop.Value is JsonObject sourceChild && target[prop.Key] is JsonObject targetChild)
            {
                MergeObjects(targetChild, sourceChild);
            }
            else
            {
                target[prop.Key] = prop.Value?.DeepClone();
            }
        }
    }

    private static string? MaskSecret(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length <= 8) return "****";
        return $"****{value[^4..]}";
    }
}
