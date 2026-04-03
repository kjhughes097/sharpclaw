using System.Text.Json;
using SharpClaw.Core;

namespace SharpClaw.OpenRouter;

public sealed class OpenRouterBackendProvider : IAgentBackendProvider
{
    public const string Name = "openrouter";
    public const string DefaultModel = "openai/gpt-4o-mini";
    public const string ApiKeyEnvVar = "OPENROUTER_API_KEY";

    private static readonly HttpClient HttpClient = new();

    public string BackendName => Name;

    public IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        return new OpenRouterBackend(
            BackendProviderUtilities.GetRequiredEnvironmentVariable(ApiKeyEnvVar),
            string.IsNullOrWhiteSpace(persona.Model) ? DefaultModel : persona.Model);
    }

    public async Task<IReadOnlyList<BackendModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var apiKey = BackendProviderUtilities.GetRequiredEnvironmentVariable(ApiKeyEnvVar);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/models");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var document = await BackendProviderUtilities.SendJsonRequestAsync(
            HttpClient,
            request,
            "OpenRouter model list request",
            cancellationToken);
        if (!BackendProviderUtilities.TryGetDataArray(document, out var data))
            return [];

        var models = new List<BackendModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement))
                continue;

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var displayName = item.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            models.Add(new BackendModelInfo(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName));
        }

        models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return models;
    }
}