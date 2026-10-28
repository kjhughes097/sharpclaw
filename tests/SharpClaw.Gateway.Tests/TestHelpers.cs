using System.Net.Http.Json;
using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Tests;

public static class TestHelpers
{
    /// <summary>Creates a thread for the agent and returns its id.</summary>
    public static async Task<Guid> CreateThreadAsync(this GatewayFixture fx, Guid agentId)
    {
        using var admin = fx.Admin();
        var resp = await admin.PostAsJsonAsync("/api/threads",
            new CreateThreadRequest(agentId.ToString(), "t"), SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CreatedThreadResponse>(SharpClawJson.Options);
        return body!.Thread.Id;
    }

    /// <summary>Posts a user message to a thread (client API).</summary>
    public static async Task<Guid> PostUserMessageAsync(this GatewayFixture fx, Guid threadId, string content, bool unattended = false)
    {
        using var admin = fx.Admin();
        var resp = await admin.PostAsJsonAsync($"/api/threads/{threadId}/messages",
            new PostMessageRequest(content, unattended), SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<QueuedMessageResponse>(SharpClawJson.Options);
        return body!.Message.Id;
    }

    /// <summary>Claims the next inbox message for the agent.</summary>
    public static async Task<InboxMessageDto?> ClaimAsync(this GatewayFixture fx, string token, int wait = 0)
    {
        using var agent = fx.AgentClient(token);
        var resp = await agent.GetAsync($"/agent/inbox?wait={wait}");
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
            return null;
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<InboxMessageResponse>(SharpClawJson.Options);
        return body!.Message;
    }

    /// <summary>
    /// Parses an SSE stream into (event, data) pairs until count reached or timeout/EOF. The
    /// <c>ready</c> framing event sent on every connect is skipped.
    /// </summary>
    public static async Task<List<(string Event, string Data)>> ReadSseEventsAsync(
        Stream stream, int count, TimeSpan timeout)
    {
        var events = await ReadSseAsync(stream, count, timeout, skipReady: true);
        return events.Select(e => (e.Event, e.Data)).ToList();
    }

    public sealed record SseEvent(string Event, string Data, long? Id);

    /// <summary>Parses an SSE stream including <c>id:</c> lines, until count reached or timeout/EOF.</summary>
    public static async Task<List<SseEvent>> ReadSseAsync(
        Stream stream, int count, TimeSpan timeout, bool skipReady = false)
    {
        var events = new List<SseEvent>();
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource(timeout);
        string? currentEvent = null;
        long? currentId = null;
        var data = new List<string>();

        try
        {
            while (events.Count < count)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line.Length == 0)
                {
                    if (currentEvent is not null && data.Count > 0 && !(skipReady && currentEvent == SseEventNames.Ready))
                        events.Add(new SseEvent(currentEvent, string.Join("\n", data), currentId));
                    currentEvent = null;
                    currentId = null;
                    data.Clear();
                    continue;
                }
                if (line.StartsWith(':')) continue;
                if (line.StartsWith("event:")) currentEvent = line[6..].Trim();
                else if (line.StartsWith("data:")) data.Add(line[5..].Trim());
                else if (line.StartsWith("id:")) currentId = long.Parse(line[3..].Trim());
            }
        }
        catch (OperationCanceledException) { /* timeout */ }

        return events;
    }

    /// <summary>Opens a client SSE stream (thread or global) with an optional <c>Last-Event-ID</c>.</summary>
    public static async Task<(HttpClient Client, Stream Stream)> OpenStreamAsync(
        this GatewayFixture fx, string path, long? lastEventId = null)
    {
        var client = fx.Admin();
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (lastEventId is { } last)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", last.ToString());
        var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        return (client, await resp.Content.ReadAsStreamAsync());
    }

    /// <summary>
    /// Waits (polling every 20 ms) until <paramref name="condition"/> holds, failing after
    /// <paramref name="timeout"/> (default 5 s). Use instead of a fixed <c>Task.Delay</c>.
    /// </summary>
    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Condition not met in time{(because is null ? "" : $": {because}")}.");
            await Task.Delay(20);
        }
    }

    /// <inheritdoc cref="EventuallyAsync(Func{bool}, TimeSpan?, string?)"/>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Condition not met in time{(because is null ? "" : $": {because}")}.");
            await Task.Delay(20);
        }
    }
}
