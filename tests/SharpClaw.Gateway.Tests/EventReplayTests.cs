using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>Durable events and <c>Last-Event-ID</c> replay (pre-3.3 spec section 8).</summary>
public class EventReplayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed record Setup(GatewayFixture Fx, AgentDto Agent, string Token, Guid Thread);

    private static async Task<Setup> SetupAsync(IDictionary<string, string?>? config = null)
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: config);
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        return new Setup(fx, agent, token, thread);
    }

    /// <summary>Posts a message, claims it, and replies: message.created + message.completed.</summary>
    private static async Task<Guid> CompleteTurnAsync(Setup s, string text)
    {
        var corr = await s.Fx.PostUserMessageAsync(s.Thread, text);
        await s.Fx.ClaimAsync(s.Token);
        using var agent = s.Fx.AgentClient(s.Token);
        (await agent.PostAsJsonAsync($"/agent/threads/{s.Thread}/messages",
            new AgentReplyRequest($"reply to {text}", corr), SharpClawJson.Options)).EnsureSuccessStatusCode();
        return corr;
    }

    private static T Data<T>(TestHelpers.SseEvent e) => JsonSerializer.Deserialize<T>(e.Data, SharpClawJson.Options)!;

    [Fact]
    public async Task Durable_events_are_stored_with_increasing_ids_and_deltas_are_not()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (client, stream) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream");
        using var __ = client;
        // ready, message.created, 3 deltas ("Fake reply to: hello" in 8-char chunks), message.completed.
        var read = TestHelpers.ReadSseAsync(stream, 6, Wait);

        var corr = await s.Fx.PostUserMessageAsync(s.Thread, "hello");
        await s.Fx.ClaimAsync(s.Token);
        await (await s.Fx.ChatAsync(s.Token, corr, s.Thread, "hello")).Content.ReadAsStringAsync();
        using (var agent = s.Fx.AgentClient(s.Token))
            await agent.PostAsJsonAsync($"/agent/threads/{s.Thread}/messages",
                new AgentReplyRequest("done", corr), SharpClawJson.Options);

        var events = await read;
        Assert.Equal(SseEventNames.Ready, events[0].Event);
        Assert.Contains(events, e => e.Event == SseEventNames.Delta && e.Id is null);
        var durable = events.Where(e => e.Id is not null && e.Event != SseEventNames.Ready).ToList();
        Assert.Equal(new[] { SseEventNames.MessageCreated, SseEventNames.MessageCompleted }, durable.Select(e => e.Event));
        Assert.True(durable[0].Id < durable[1].Id);

        using var db = s.Fx.NewDb();
        var stored = await db.ThreadEvents.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(durable.Select(e => e.Id!.Value), stored.Select(e => e.Id));
        Assert.DoesNotContain(stored, e => e.Type == SseEventNames.Delta);
        Assert.Equal(corr, Data<MessageCreatedEvent>(durable[0]).MessageId);
    }

    [Fact]
    public async Task Fresh_connect_replays_nothing_and_sends_ready_with_the_baseline()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        await CompleteTurnAsync(s, "one");

        var (client, stream) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream");
        using var __ = client;
        var events = await TestHelpers.ReadSseAsync(stream, 2, TimeSpan.FromMilliseconds(800));

        var ready = Assert.Single(events);
        Assert.Equal(SseEventNames.Ready, ready.Event);
        using var db = s.Fx.NewDb();
        Assert.Equal(await db.ThreadEvents.MaxAsync(e => e.Id), ready.Id);
        Assert.Equal(s.Thread, Data<ReadyEvent>(ready).ThreadId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconnect_replays_exactly_the_missed_events_in_order(bool useHeader)
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        await CompleteTurnAsync(s, "one");
        long seen;
        using (var db = s.Fx.NewDb())
            seen = await db.ThreadEvents.MaxAsync(e => e.Id);
        var missed = await CompleteTurnAsync(s, "two");

        using var client = s.Fx.Admin();
        var path = $"/api/threads/{s.Thread}/stream" + (useHeader ? "" : $"?lastEventId={seen}");
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (useHeader)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", seen.ToString());
        var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var events = await TestHelpers.ReadSseAsync(await resp.Content.ReadAsStreamAsync(), 3, Wait);

        Assert.Equal(new[] { SseEventNames.MessageCreated, SseEventNames.MessageCompleted, SseEventNames.Ready },
            events.Select(e => e.Event));
        Assert.Equal(missed, Data<MessageCompletedEvent>(events[1]).CorrelationId);
        Assert.True(events[0].Id > seen && events[1].Id > events[0].Id);
        Assert.Equal(events[1].Id, events[2].Id);
    }

    [Fact]
    public async Task Events_during_replay_arrive_once_with_no_gap()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        const int messages = 15;

        // Publish while the client connects with lastEventId=0, so replay and live overlap.
        var posting = Task.Run(async () =>
        {
            for (var i = 0; i < messages; i++)
                await s.Fx.PostUserMessageAsync(s.Thread, $"m{i}");
        });
        await Task.Delay(30);
        var (client, stream) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream", lastEventId: 0);
        using var __ = client;
        var events = await TestHelpers.ReadSseAsync(stream, messages + 1, TimeSpan.FromSeconds(10), skipReady: true);
        await posting;

        var ids = events.Select(e => e.Id!.Value).ToList();
        Assert.Equal(messages, ids.Count);
        Assert.Equal(ids.OrderBy(i => i), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        using var db = s.Fx.NewDb();
        Assert.Equal(await db.ThreadEvents.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(), ids);
    }

    [Fact]
    public async Task Reconnect_mid_stream_gets_a_snapshot_then_the_remaining_deltas()
    {
        var script = ChatTestHelpers.WriteScript("""
            { "rules": [ { "match": "slow", "response": "abcdefghijkl", "chunkSize": 2, "delayMs": 250 } ] }
            """);
        var s = await SetupAsync(new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        await using var _ = s.Fx;
        var corr = await s.Fx.PostUserMessageAsync(s.Thread, "slow");
        await s.Fx.ClaimAsync(s.Token);

        var chat = Task.Run(async () => await (await s.Fx.ChatAsync(s.Token, corr, s.Thread, "slow")).Content.ReadAsStringAsync());
        await Task.Delay(700);
        var (client, stream) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream");
        using var __ = client;
        var events = await TestHelpers.ReadSseAsync(stream, 10, TimeSpan.FromSeconds(4), skipReady: true);
        await chat;

        var snapshot = Data<SnapshotEvent>(events[0]);
        Assert.Equal(SseEventNames.Snapshot, events[0].Event);
        Assert.Equal(corr, snapshot.CorrelationId);
        Assert.NotEmpty(snapshot.Content);
        var rest = string.Concat(events.Skip(1).Where(e => e.Event == SseEventNames.Delta)
            .Select(e => Data<DeltaEvent>(e).Content));
        Assert.Equal("abcdefghijkl", snapshot.Content + rest);
    }

    [Fact]
    public async Task Last_id_below_the_pruned_point_yields_reset_and_retention_uses_the_clock()
    {
        var s = await SetupAsync(new Dictionary<string, string?> { ["Events:RetentionDays"] = "14" });
        await using var _ = s.Fx;
        await CompleteTurnAsync(s, "old");
        s.Fx.Time.Advance(TimeSpan.FromDays(10));
        await CompleteTurnAsync(s, "recent");
        s.Fx.Time.Advance(TimeSpan.FromDays(5));

        await s.Fx.App.Services.GetRequiredService<EventRetentionService>().PruneAsync(CancellationToken.None);

        using (var db = s.Fx.NewDb())
        {
            Assert.Equal(2, await db.ThreadEvents.CountAsync());
            Assert.Equal(2, await db.EventLogState.Select(x => x.PrunedThrough).SingleAsync());
        }

        var (client, stream) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream", lastEventId: 1);
        using var __ = client;
        var events = await TestHelpers.ReadSseAsync(stream, 2, Wait);
        Assert.Equal(new[] { SseEventNames.Reset, SseEventNames.Ready }, events.Select(e => e.Event));
        Assert.Equal(ResetReasons.HistoryPruned, Data<ResetEvent>(events[0]).Reason);

        // A last id at the pruned point is still fine.
        var (client2, stream2) = await s.Fx.OpenStreamAsync($"/api/threads/{s.Thread}/stream", lastEventId: 2);
        using var ___ = client2;
        var replay = await TestHelpers.ReadSseAsync(stream2, 3, Wait);
        Assert.Equal(new[] { SseEventNames.MessageCreated, SseEventNames.MessageCompleted, SseEventNames.Ready },
            replay.Select(e => e.Event));
    }

    [Fact]
    public async Task Global_stream_spans_threads_and_replays_the_same_way()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var otherThread = await s.Fx.CreateThreadAsync(s.Agent.Id);

        var (client, stream) = await s.Fx.OpenStreamAsync("/api/events");
        using var __ = client;
        var live = TestHelpers.ReadSseAsync(stream, 2, Wait, skipReady: true);
        await s.Fx.PostUserMessageAsync(s.Thread, "a");
        await s.Fx.PostUserMessageAsync(otherThread, "b");
        var events = await live;
        Assert.Equal(new[] { s.Thread, otherThread }, events.Select(e => Data<MessageCreatedEvent>(e).ThreadId));

        var (client2, stream2) = await s.Fx.OpenStreamAsync("/api/events", lastEventId: events[0].Id);
        using var ___ = client2;
        var replay = await TestHelpers.ReadSseAsync(stream2, 2, Wait);
        Assert.Equal(events[1].Id, replay[0].Id);
        Assert.Equal(SseEventNames.Ready, replay[1].Event);
        Assert.Null(Data<ReadyEvent>(replay[1]).ThreadId);
    }

    [Fact]
    public async Task Global_stream_requires_the_client_credential()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var raw = fx.ClientRaw();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await raw.GetAsync("/api/events")).StatusCode);
    }
}
