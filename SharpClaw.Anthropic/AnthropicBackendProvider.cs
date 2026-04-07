using System.Text.Json;
using SharpClaw.Core;
using global::Anthropic;
using global::Anthropic.Core;

namespace SharpClaw.Anthropic;

public sealed class AnthropicBackendProvider : IAgentBackendProvider
{
    public const string Name = "anthropic";
    public const string DefaultModel = "claude-haiku-4-5-20251001";
    public const string ApiKeyEnvVar = "ANTHROPIC_API_KEY";

    private static readonly HttpClient HttpClient = new();

    public string BackendName => Name;

    public IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        return CreateBackend(string.IsNullOrWhiteSpace(persona.Model) ? DefaultModel : persona.Model);
    }

    public async Task<IReadOnlyList<BackendModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var apiKey = BackendProviderUtilities.GetRequiredEnvironmentVariable(ApiKeyEnvVar);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        using var document = await BackendProviderUtilities.SendJsonRequestAsync(
            HttpClient,
            request,
            "Anthropic model list request",
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

            var displayName = item.TryGetProperty("display_name", out var nameElement)
                ? nameElement.GetString()
                : null;

            models.Add(new BackendModelInfo(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName));
        }

        return models;
    }

    public static IAgentBackend CreateBackend(string model = DefaultModel)
    {
        var apiKey = BackendProviderUtilities.GetRequiredEnvironmentVariable(ApiKeyEnvVar);

        return new AnthropicBackend(new AnthropicClient(new ClientOptions
        {
            ApiKey = apiKey,
        }), model);
    }
}