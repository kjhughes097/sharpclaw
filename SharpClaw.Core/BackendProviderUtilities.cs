using System.Text.Json;
using System.Collections.Concurrent;

namespace SharpClaw.Core;

public static class BackendProviderUtilities
{
    private static readonly ConcurrentDictionary<string, string> ConfiguredValues =
        new(StringComparer.OrdinalIgnoreCase);

    public static void SetConfiguredValue(string key, string value)
    {
        ConfiguredValues[key] = value;
    }

    public static string GetRequiredEnvironmentVariable(string variableName)
    {
        if (!ConfiguredValues.TryGetValue(variableName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{variableName} is not configured. Configure the backend API key in Configure > Backends.");
        }

        return value;
    }

    public static async Task<JsonDocument> SendJsonRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        string requestDescription,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{requestDescription} failed with {(int)response.StatusCode}: {responseBody}");
        }

        return JsonDocument.Parse(responseBody);
    }

    public static bool TryGetDataArray(JsonDocument document, out JsonElement data)
    {
        if (document.RootElement.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array)
            return true;

        data = default;
        return false;
    }
}