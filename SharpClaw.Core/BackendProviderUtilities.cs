using System.Text.Json;

namespace SharpClaw.Core;

public static class BackendProviderUtilities
{
    public static string GetRequiredEnvironmentVariable(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{variableName} is not set.");

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