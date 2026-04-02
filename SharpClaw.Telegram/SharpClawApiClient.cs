using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpClaw.Telegram;

public sealed class SharpClawApiClient(HttpClient httpClient, ILogger<SharpClawApiClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string?> GetDefaultAgentIdAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync("api/personas", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var payload = GetPayloadOrRoot(doc.RootElement);

            if (payload.ValueKind == JsonValueKind.Array &&
                payload.GetArrayLength() > 0)
            {
                var first = payload[0];
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("id", out var idEl))
                {
                    return idEl.GetString();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch agents from SharpClaw API");
        }

        return null;
    }

    public async Task<string?> CreateSessionAsync(string agentId, CancellationToken ct = default)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new { agentId }, JsonOptions);
            using var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync("api/sessions", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var payload = GetPayloadOrRoot(doc.RootElement);

            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("sessionId", out var sessionIdEl))
            {
                return sessionIdEl.GetString();
            }

            logger.LogWarning("Unexpected create-session response shape: {Body}", json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create session for agent '{AgentId}'", agentId);
        }

        return null;
    }

    public async Task<string?> SendMessageAsync(string sessionId, string message, CancellationToken ct = default)
    {
        try
        {
            var requestBody = JsonSerializer.Serialize(new { message }, JsonOptions);
            using var content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"api/sessions/{sessionId}/messages", content, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var payload = GetPayloadOrRoot(doc.RootElement);

            if (payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("messageId", out var messageIdEl))
            {
                return messageIdEl.GetString();
            }

            logger.LogWarning("Unexpected send-message response shape for session '{SessionId}': {Body}",
                sessionId, json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send message to session '{SessionId}'", sessionId);
        }

        return null;
    }

    public async Task<string> ConsumeStreamAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/sessions/{sessionId}/messages/{messageId}/stream",
            HttpCompletionOption.ResponseHeadersRead, ct);

        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? currentEventType = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEventType = line[7..].Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var data = line[6..];
                switch (currentEventType)
                {
                    case "done":
                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("content", out var contentEl))
                                return contentEl.GetString() ?? string.Empty;
                        }
                        catch (JsonException ex)
                        {
                            logger.LogWarning(ex, "Failed to parse done event payload");
                        }
                        return string.Empty;

                    case "permission_request":
                        try
                        {
                            using var doc = JsonDocument.Parse(data);
                            if (doc.RootElement.TryGetProperty("requestId", out var requestIdEl))
                            {
                                var requestId = requestIdEl.GetString();
                                if (!string.IsNullOrEmpty(requestId))
                                    await ApprovePermissionAsync(sessionId, requestId, ct);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to handle permission request in session '{SessionId}'", sessionId);
                        }
                        break;
                }
            }
            else if (line.Length == 0)
            {
                currentEventType = null;
            }
        }

        return string.Empty;
    }

    private async Task ApprovePermissionAsync(string sessionId, string requestId, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { allow = true }, JsonOptions);
            using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(
                $"api/sessions/{sessionId}/permissions/{requestId}", content, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning(
                    "Permission approval for request '{RequestId}' returned {StatusCode}",
                    requestId, response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to approve permission '{RequestId}' for session '{SessionId}'",
                requestId, sessionId);
        }
    }

    private static JsonElement GetPayloadOrRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("payload", out var payload))
        {
            return payload;
        }

        return root;
    }
}
