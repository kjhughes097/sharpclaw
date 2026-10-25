using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SharpClaw.Gateway.Tests;

public static class ChatTestHelpers
{
    public static async Task<HttpResponseMessage> ChatAsync(
        this GatewayFixture fx, string token, Guid correlationId, Guid threadId,
        string userText = "hello", bool stream = true, bool includeTools = false,
        string? model = "ignored")
    {
        var c = fx.AgentClient(token);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = stream,
            ["messages"] = new[] { new { role = "user", content = userText } },
        };
        if (includeTools)
            payload["tools"] = Array.Empty<object>();

        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        req.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString());
        req.Headers.TryAddWithoutValidation("X-Thread-Id", threadId.ToString());

        var resp = await c.SendAsync(req);
        c.Dispose();
        return resp;
    }

    /// <summary>Parses chat SSE output: reconstructed content, whether a usage chunk was present, model.</summary>
    public static (string Content, bool HasUsage, bool HasDone, string? Model, bool HasErrorChunk) ParseChat(string body)
    {
        var content = new System.Text.StringBuilder();
        bool hasUsage = false, hasDone = false, hasError = false;
        string? model = null;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") { hasDone = true; continue; }

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) { hasError = true; continue; }
            if (root.TryGetProperty("model", out var m)) model = m.GetString();
            if (root.TryGetProperty("usage", out _)) hasUsage = true;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var ch in choices.EnumerateArray())
                    if (ch.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                        content.Append(cEl.GetString());
        }
        return (content.ToString(), hasUsage, hasDone, model, hasError);
    }

    public static string WriteScript(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-script-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
