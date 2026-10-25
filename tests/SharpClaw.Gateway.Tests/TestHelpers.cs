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
    public static async Task<Guid> PostUserMessageAsync(this GatewayFixture fx, Guid threadId, string content)
    {
        using var admin = fx.Admin();
        var resp = await admin.PostAsJsonAsync($"/api/threads/{threadId}/messages",
            new PostMessageRequest(content), SharpClawJson.Options);
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

    /// <summary>Parses an SSE stream into (event, data) pairs until count reached or timeout/EOF.</summary>
    public static async Task<List<(string Event, string Data)>> ReadSseEventsAsync(
        Stream stream, int count, TimeSpan timeout)
    {
        var events = new List<(string, string)>();
        using var reader = new StreamReader(stream);
        using var cts = new CancellationTokenSource(timeout);
        string? currentEvent = null;
        var data = new List<string>();

        try
        {
            while (events.Count < count)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (line.Length == 0)
                {
                    if (currentEvent is not null && data.Count > 0)
                        events.Add((currentEvent, string.Join("\n", data)));
                    currentEvent = null;
                    data.Clear();
                    continue;
                }
                if (line.StartsWith(':')) continue;
                if (line.StartsWith("event:")) currentEvent = line[6..].Trim();
                else if (line.StartsWith("data:")) data.Add(line[5..].Trim());
            }
        }
        catch (OperationCanceledException) { /* timeout */ }

        return events;
    }
}
