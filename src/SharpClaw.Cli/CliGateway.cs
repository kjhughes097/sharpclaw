using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Cli;

public sealed class CliGateway : ICliGateway, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public CliGateway(CliOptions options)
    {
        _baseUrl = options.Url;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AdminKey);
    }

    public async Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/agents",
            new CreateAgentRequest(name, model, budget), SharpClawJson.Options);
        return await ReadAsync<CreatedAgentResponse>(resp);
    }

    public async Task<AgentListResponse> ListAgentsAsync()
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/agents");
        return await ReadAsync<AgentListResponse>(resp);
    }

    public async Task<AgentDto> GetAgentAsync(string idOrName)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/agents/{idOrName}");
        return await ReadAsync<AgentDto>(resp);
    }

    public async Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled)
    {
        using var resp = await _http.PatchAsJsonAsync($"{_baseUrl}/api/agents/{idOrName}",
            new UpdateAgentRequest(model, budget, enabled), SharpClawJson.Options);
        return await ReadAsync<AgentDto>(resp);
    }

    public async Task<CreatedTokenResponse> IssueTokenAsync(string agent, DateTime? expiresAt)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/agents/{agent}/tokens",
            new CreateTokenRequest(expiresAt), SharpClawJson.Options);
        return await ReadAsync<CreatedTokenResponse>(resp);
    }

    public async Task<TokenListResponse> ListTokensAsync(string agent)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/agents/{agent}/tokens");
        return await ReadAsync<TokenListResponse>(resp);
    }

    public async Task RevokeTokenAsync(string agent, string tokenId)
    {
        using var resp = await _http.DeleteAsync($"{_baseUrl}/api/agents/{agent}/tokens/{tokenId}");
        await EnsureAsync(resp);
    }

    public async Task<ThreadDto> CreateThreadAsync(string agentId, string? title)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/threads",
            new CreateThreadRequest(agentId, title), SharpClawJson.Options);
        var body = await ReadAsync<CreatedThreadResponse>(resp);
        return body.Thread;
    }

    public async Task<IThreadStream> OpenThreadStreamAsync(Guid threadId, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"{_baseUrl}/api/threads/{threadId}/stream",
            HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(resp);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return new HttpThreadStream(resp, stream);
    }

    public async Task<Guid> PostMessageAsync(Guid threadId, string content)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/threads/{threadId}/messages",
            new PostMessageRequest(content), SharpClawJson.Options);
        var body = await ReadAsync<QueuedMessageResponse>(resp);
        return body.Message.Id;
    }

    public async Task<AuditResponse> AuditAsync(string? agent, int? limit)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent)) query.Add($"agent={Uri.EscapeDataString(agent)}");
        if (limit is { } l) query.Add($"limit={l}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        using var resp = await _http.GetAsync($"{_baseUrl}/api/audit/llm-calls{qs}");
        return await ReadAsync<AuditResponse>(resp);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp)
    {
        await EnsureAsync(resp);
        var value = await resp.Content.ReadFromJsonAsync<T>(SharpClawJson.Options);
        return value ?? throw new CliException("Empty response from gateway.");
    }

    private static async Task EnsureAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            var error = await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options);
            message = error?.Error.Message;
        }
        catch { /* non-JSON body */ }
        throw new CliException(message ?? $"Request failed with status {(int)resp.StatusCode}.");
    }

    public void Dispose() => _http.Dispose();

    private sealed class HttpThreadStream : IThreadStream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _stream;
        private readonly StreamReader _reader;

        public HttpThreadStream(HttpResponseMessage response, Stream stream)
        {
            _response = response;
            _stream = stream;
            _reader = new StreamReader(stream);
        }

        public async IAsyncEnumerable<CliSseEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            string? currentEvent = null;
            var data = new List<string>();
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) yield break;
                if (line.Length == 0)
                {
                    if (currentEvent is not null && data.Count > 0)
                        yield return new CliSseEvent(currentEvent, string.Join("\n", data));
                    currentEvent = null;
                    data.Clear();
                    continue;
                }
                if (line.StartsWith(':')) continue;
                if (line.StartsWith("event:", StringComparison.Ordinal)) currentEvent = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].Trim());
            }
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _stream.Dispose();
            _response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
