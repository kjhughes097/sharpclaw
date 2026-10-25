using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Agent;

public sealed class GatewayHttpClient : IGatewayClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public GatewayHttpClient(HttpClient http, AgentConfig config)
    {
        _http = http;
        _baseUrl = config.GatewayUrl.TrimEnd('/');
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AgentToken);
    }

    public async Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_baseUrl}/agent/inbox?wait={waitSeconds}", ct);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var body = await resp.Content.ReadFromJsonAsync<InboxMessageResponse>(SharpClawJson.Options, ct);
                    return body?.Message is { } m ? InboxResult.Of(m) : InboxResult.Empty;
                case HttpStatusCode.NoContent:
                    return InboxResult.Empty;
                case HttpStatusCode.Unauthorized:
                    return InboxResult.Unauthorized;
                case HttpStatusCode.Forbidden:
                    return InboxResult.Disabled;
                default:
                    return InboxResult.Transient;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return InboxResult.Transient;
        }
    }

    public async Task<IReadOnlyList<ChatMessage>> GetThreadHistoryAsync(Guid threadId, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/agent/threads/{threadId}/messages", ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<MessageListResponse>(SharpClawJson.Options, ct);
        return (body?.Messages ?? Array.Empty<MessageDto>())
            .Where(m => m.Role is Roles.User or Roles.Assistant)
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();
    }

    public Task<ChatOutcome> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct)
    {
        var payload = new
        {
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        return SendChatAsync(payload, correlationId, threadId, LlmCallPurpose.Chat, ct);
    }

    /// <summary>Posts a streaming chat request and accumulates the reply (or a short fail code).</summary>
    private async Task<ChatOutcome> SendChatAsync(
        object payload, Guid correlationId, Guid threadId, string purpose, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload, options: SharpClawJson.Options),
        };
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString());
        request.Headers.TryAddWithoutValidation("X-Thread-Id", threadId.ToString());
        request.Headers.TryAddWithoutValidation("X-SharpClaw-Purpose", purpose);

        try
        {
            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return ChatOutcome.Fail("budget_exceeded");
            if (resp.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                return ChatOutcome.Fail("provider_error");
            if (!resp.IsSuccessStatusCode)
                return ChatOutcome.Fail("agent_error");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = new StringBuilder();

            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0 || line.StartsWith(':')) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out _))
                    return ChatOutcome.Fail("provider_error");
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                    foreach (var ch in choices.EnumerateArray())
                        if (ch.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            content.Append(c.GetString());
            }

            return ChatOutcome.Ok(content.ToString());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ChatOutcome.Fail("agent_error");
        }
    }

    public async Task PostReplyAsync(Guid threadId, string content, Guid correlationId, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/agent/threads/{threadId}/messages",
            new AgentReplyRequest(content, correlationId), SharpClawJson.Options, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task FailAsync(Guid messageId, string reason, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/agent/messages/{messageId}/fail",
            new FailMessageRequest(reason), SharpClawJson.Options, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<ApprovalDto> RequestApprovalAsync(CreateApprovalRequest request, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/agent/approvals", request, SharpClawJson.Options, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options, ct);
        return body?.Approval ?? throw new InvalidOperationException("empty approval response");
    }

    public async Task<ApprovalDto> PollApprovalAsync(Guid approvalId, int waitSeconds, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/agent/approvals/{approvalId}?wait={waitSeconds}", ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options, ct);
        return body?.Approval ?? throw new InvalidOperationException("empty approval response");
    }

    public async Task ReportToolCallAsync(ToolCallReportRequest report, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/agent/tool-calls", report, SharpClawJson.Options, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string?> ClassifyRiskAsync(
        IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct)
    {
        var payload = new
        {
            stream = true,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        var outcome = await SendChatAsync(payload, correlationId, threadId, LlmCallPurpose.RiskAssessment, ct);
        return outcome.Success ? outcome.Content : null;
    }
}
