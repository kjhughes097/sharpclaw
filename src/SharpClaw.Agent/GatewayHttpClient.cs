using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SharpClaw.Agent.Tools;
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
            .Where(m => m.Kind == MessageKinds.Message && m.Role is Roles.User or Roles.Assistant)
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();
    }

    public Task<ChatOutcome> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<IAgentTool> tools,
        Guid correlationId, Guid threadId, CancellationToken ct) =>
        SendChatAsync(BuildPayload(messages, tools), correlationId, threadId, LlmCallPurpose.Chat, ct);

    /// <summary>
    /// Builds the OpenAI-shaped request body. Dictionaries keep the snake_case wire names exactly
    /// and let absent fields be omitted. No <c>model</c> is sent: the gateway always chooses it.
    /// </summary>
    internal static Dictionary<string, object?> BuildPayload(
        IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<IAgentTool> tools)
    {
        var payload = new Dictionary<string, object?>
        {
            ["stream"] = true,
            ["messages"] = messages.Select(ToWire).ToList(),
        };
        if (tools.Count > 0)
        {
            payload["tools"] = tools.Select(t => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonDocument.Parse(t.ParametersJson).RootElement.Clone(),
                },
            }).ToList();
            payload["tool_choice"] = "auto";
        }
        return payload;
    }

    private static Dictionary<string, object?> ToWire(ChatMessage m)
    {
        var wire = new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCalls is { Count: > 0 })
            wire["tool_calls"] = m.ToolCalls.Select(c => new Dictionary<string, object?>
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?> { ["name"] = c.Name, ["arguments"] = c.Arguments },
            }).ToList();
        if (m.ToolCallId is not null)
            wire["tool_call_id"] = m.ToolCallId;
        return wire;
    }

    /// <summary>Posts a streaming chat request and accumulates the reply, tool calls, or a short fail code.</summary>
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
                // 429 is either the daily budget or the gateway's loop backstop; the code says which.
                return ChatOutcome.Fail(await ReadErrorCodeAsync(resp, ct) ?? ErrorCodes.BudgetExceeded);
            if (resp.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout)
                return ChatOutcome.Fail("provider_error");
            if (!resp.IsSuccessStatusCode)
                return ChatOutcome.Fail("agent_error");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await ReadStreamAsync(reader, ct);
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

    /// <summary>Parses OpenAI-style SSE chunks: content deltas and (possibly fragmented) tool calls.</summary>
    internal static async Task<ChatOutcome> ReadStreamAsync(TextReader reader, CancellationToken ct)
    {
        var content = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();

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
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var ch in choices.EnumerateArray())
            {
                if (!ch.TryGetProperty("delta", out var delta))
                    continue;
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content.Append(c.GetString());
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                        (string? Id, string? Name, StringBuilder Args) call =
                            calls.TryGetValue(index, out var existing) ? existing : (null, null, new StringBuilder());
                        if (tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            call.Id ??= id.GetString();
                        if (tc.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                                call.Name ??= n.GetString();
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                call.Args.Append(a.GetString());
                        }
                        calls[index] = call;
                    }
                }
            }
        }

        var toolCalls = calls.Values
            .Where(c => !string.IsNullOrEmpty(c.Id) && !string.IsNullOrEmpty(c.Name))
            .Select(c => new ChatToolCall(c.Id!, c.Name!, c.Args.Length == 0 ? "{}" : c.Args.ToString()))
            .ToList();
        return ChatOutcome.Ok(content.ToString(), toolCalls);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options, ct);
            return body?.Error.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task PostReplyAsync(
        Guid threadId, string content, Guid correlationId, IReadOnlyList<ToolExchangeDto> exchanges, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/agent/threads/{threadId}/messages",
            new AgentReplyRequest(content, correlationId, exchanges), SharpClawJson.Options, ct);
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
        var payload = BuildPayload(messages, Array.Empty<IAgentTool>());
        var outcome = await SendChatAsync(payload, correlationId, threadId, LlmCallPurpose.RiskAssessment, ct);
        return outcome.Success ? outcome.Content : null;
    }
}
