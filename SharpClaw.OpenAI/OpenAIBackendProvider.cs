using System.Text.Json;
using SharpClaw.Core;

namespace SharpClaw.OpenAI;

public sealed class OpenAIBackendProvider : IAgentBackendProvider
{
    public const string Name = "openai";
    public const string DefaultModel = "gpt-4o-mini";
    public const string ApiKeyEnvVar = "OPENAI_API_KEY";

    private static readonly HttpClient HttpClient = new();

    public string BackendName => Name;

    public IAgentBackend CreateBackend(AgentPersona persona, PermissionGate permissionGate)
    {
        return new OpenAIBackend(
            BackendProviderUtilities.GetRequiredEnvironmentVariable(ApiKeyEnvVar),
            string.IsNullOrWhiteSpace(persona.Model) ? DefaultModel : persona.Model);
    }

    public async Task<IReadOnlyList<BackendModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var apiKey = BackendProviderUtilities.GetRequiredEnvironmentVariable(ApiKeyEnvVar);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        using var document = await BackendProviderUtilities.SendJsonRequestAsync(
            HttpClient,
            request,
            "OpenAI model list request",
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

            if (!id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) &&
                !(id.Length > 1 && id[0] is 'o' or 'O' && char.IsDigit(id[1])))
                continue;

            models.Add(new BackendModelInfo(id, id));
        }

        models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return models;
    }
}