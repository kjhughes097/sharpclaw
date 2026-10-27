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

    public async Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget, int? contextWindow = null)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/agents",
            new CreateAgentRequest(name, model, budget, contextWindow), SharpClawJson.Options);
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

    public async Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled, int? contextWindow = null)
    {
        using var resp = await _http.PatchAsJsonAsync($"{_baseUrl}/api/agents/{idOrName}",
            new UpdateAgentRequest(model, budget, enabled, contextWindow), SharpClawJson.Options);
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

    public async Task<IThreadStream> OpenThreadStreamAsync(Guid threadId, long? lastEventId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/threads/{threadId}/stream");
        if (lastEventId is { } last)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", last.ToString());
        var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(resp);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return new HttpThreadStream(resp, stream);
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid threadId)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/threads/{threadId}/messages");
        return (await ReadAsync<MessageListResponse>(resp)).Messages;
    }

    public async Task<Guid> PostMessageAsync(Guid threadId, string content, bool unattended = false)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/threads/{threadId}/messages",
            new PostMessageRequest(content, unattended), SharpClawJson.Options);
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

    public async Task<ToolCallAuditResponse> AuditToolCallsAsync(string? agent, int? limit)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(agent)) query.Add($"agent={Uri.EscapeDataString(agent)}");
        if (limit is { } l) query.Add($"limit={l}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        using var resp = await _http.GetAsync($"{_baseUrl}/api/audit/tool-calls{qs}");
        return await ReadAsync<ToolCallAuditResponse>(resp);
    }

    public async Task<ApprovalDto> DecideApprovalAsync(Guid approvalId, string decision)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/approvals/{approvalId}",
            new DecideApprovalRequest(decision), SharpClawJson.Options);
        return (await ReadAsync<ApprovalResponse>(resp)).Approval;
    }

    public async Task<ApprovalListResponse> ListApprovalsAsync(string? status, string? agent)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (!string.IsNullOrWhiteSpace(agent)) query.Add($"agent={Uri.EscapeDataString(agent)}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        using var resp = await _http.GetAsync($"{_baseUrl}/api/approvals{qs}");
        return await ReadAsync<ApprovalListResponse>(resp);
    }

    public async Task<UserAdminDto> CreateUserAsync(string name, string password)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/users", new CreateUserRequest(name, password), SharpClawJson.Options);
        return (await ReadAsync<CreatedUserResponse>(resp)).User;
    }

    public async Task<UserListResponse> ListUsersAsync()
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/api/users");
        return await ReadAsync<UserListResponse>(resp);
    }

    public async Task SetUserPasswordAsync(string name, string password)
    {
        using var resp = await _http.PutAsJsonAsync($"{_baseUrl}/api/users/{Uri.EscapeDataString(name)}/password",
            new SetPasswordRequest(password), SharpClawJson.Options);
        await EnsureAsync(resp);
    }

    public async Task<UserAdminDto> SetUserDisabledAsync(string name, bool disabled)
    {
        using var resp = await _http.PatchAsJsonAsync($"{_baseUrl}/api/users/{Uri.EscapeDataString(name)}",
            new UpdateUserRequest(disabled), SharpClawJson.Options);
        return await ReadAsync<UserAdminDto>(resp);
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
            long? currentId = null;
            var data = new List<string>();
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) yield break;
                if (line.Length == 0)
                {
                    if (currentEvent is not null && data.Count > 0)
                        yield return new CliSseEvent(currentEvent, string.Join("\n", data), currentId);
                    currentEvent = null;
                    currentId = null;
                    data.Clear();
                    continue;
                }
                if (line.StartsWith(':')) continue;
                if (line.StartsWith("event:", StringComparison.Ordinal)) currentEvent = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Add(line[5..].Trim());
                else if (line.StartsWith("id:", StringComparison.Ordinal) && long.TryParse(line[3..].Trim(), out var id)) currentId = id;
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
