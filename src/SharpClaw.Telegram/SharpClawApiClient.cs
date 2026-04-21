using System.Text;
using System.Text.Json;

namespace SharpClaw.Telegram;

/// <summary>
/// HTTP client that calls the SharpClaw API and parses SSE responses.
/// </summary>
public sealed class SharpClawApiClient
{
    private readonly HttpClient _http;

    public SharpClawApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Configures the base URL and optional API key.
    /// </summary>
    public void Configure(string baseUrl, string apiKey)
    {
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
    }

    /// <summary>
    /// Sends a chat message and yields assembled response text via SSE streaming.
    /// Calls the callback with incremental token chunks for live "typing" feedback.
    /// Returns the full response text, agent slug, and the chat slug assigned by the API.
    /// </summary>
    public async Task<(string FullText, string Agent, string? ChatSlug, int InputTokens, int OutputTokens)> SendMessageAsync(
        string message,
        string projectSlug,
        string? chatSlug,
        string? agentSlug,
        Func<string, Task>? onChunk,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            message,
            projectSlug,
            chatSlug,
            agentSlug,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fullText = new StringBuilder();
        var agent = "unknown";
        var eventType = "";
        string? chatSlugResult = null;
        var inputTokens = 0;
        var outputTokens = 0;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventType = line[7..].Trim();
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventType.Length > 0)
            {
                var json = line[6..];
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    switch (eventType)
                    {
                        case "chat_info":
                            chatSlugResult = root.GetProperty("chatSlug").GetString();
                            break;

                        case "token":
                            var text = root.GetProperty("text").GetString() ?? "";
                            fullText.Append(text);
                            if (onChunk is not null)
                                await onChunk(text);
                            break;

                        case "done":
                            agent = root.GetProperty("agent").GetString() ?? "unknown";
                            if (root.TryGetProperty("input_tokens", out var it))
                                inputTokens = it.GetInt32();
                            if (root.TryGetProperty("output_tokens", out var ot))
                                outputTokens = ot.GetInt32();
                            break;

                        case "error":
                            var errMsg = root.GetProperty("message").GetString() ?? "Unknown error";
                            fullText.Append($"\n⚠️ Error: {errMsg}");
                            break;
                    }
                }
                catch (JsonException)
                {
                    // skip malformed events
                }

                eventType = "";
            }
        }

        return (fullText.ToString(), agent, chatSlugResult, inputTokens, outputTokens);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Lists all projects from the API.
    /// </summary>
    public async Task<List<ProjectSummary>> GetProjectsAsync(CancellationToken ct)
    {
        var response = await _http.GetAsync("/api/projects", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ProjectSummary>>(json, JsonOptions) ?? [];
    }

    /// <summary>
    /// Lists all chats in a project from the API.
    /// </summary>
    public async Task<List<ChatSummary>> GetChatsAsync(string projectSlug, CancellationToken ct)
    {
        var response = await _http.GetAsync($"/api/projects/{Uri.EscapeDataString(projectSlug)}/chats", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ChatSummary>>(json, JsonOptions) ?? [];
    }

    /// <summary>
    /// Deletes (archives) a project via the API.
    /// </summary>
    public async Task<bool> DeleteProjectAsync(string projectSlug, CancellationToken ct)
    {
        var response = await _http.DeleteAsync($"/api/projects/{Uri.EscapeDataString(projectSlug)}", ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Gets messages for a chat from the API.
    /// </summary>
    public async Task<List<ChatMessageDto>> GetMessagesAsync(string projectSlug, string chatSlug, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"/api/projects/{Uri.EscapeDataString(projectSlug)}/chats/{Uri.EscapeDataString(chatSlug)}/messages", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ChatMessageDto>>(json, JsonOptions) ?? [];
    }

    public record ProjectSummary(string Slug, string Name, int TotalInputTokens, int TotalOutputTokens);
    public record ChatSummary(string Slug, string Title, string? LastAgent, int TotalInputTokens, int TotalOutputTokens);
    public record ChatMessageDto(string Role, string Content, string? AgentSlug);
}
