using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Thread list, detail and rename/archive (3.3 spec section 5.1).</summary>
public class ThreadsApiTests
{
    [Fact]
    public async Task List_is_newest_activity_first_with_cursor_paging()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await fx.CreateThreadAsync(agent.Id));
            fx.Time.Advance(TimeSpan.FromSeconds(1));
        }
        // Activity moves the oldest thread to the top.
        await fx.PostUserMessageAsync(ids[0], "bump");

        using var admin = fx.Admin();
        var page1 = (await admin.GetFromJsonAsync<ThreadListResponse>("/api/threads?limit=2", SharpClawJson.Options))!;
        Assert.Equal(new[] { ids[0], ids[4] }, page1.Threads.Select(t => t.Id));
        Assert.NotNull(page1.NextCursor);

        var page2 = (await admin.GetFromJsonAsync<ThreadListResponse>(
            $"/api/threads?limit=2&cursor={page1.NextCursor}", SharpClawJson.Options))!;
        Assert.Equal(new[] { ids[3], ids[2] }, page2.Threads.Select(t => t.Id));
        var page3 = (await admin.GetFromJsonAsync<ThreadListResponse>(
            $"/api/threads?limit=2&cursor={page2.NextCursor}", SharpClawJson.Options))!;
        Assert.Equal(new[] { ids[1] }, page3.Threads.Select(t => t.Id));
        Assert.Null(page3.NextCursor);

        var bad = await admin.GetAsync("/api/threads?cursor=not-a-cursor");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(bad));
    }

    [Fact]
    public async Task List_rows_carry_grouped_counts_preview_and_auto_title()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        using var admin = fx.Admin();
        var created = await admin.PostAsJsonAsync("/api/threads", new CreateThreadRequest("assistant", null), SharpClawJson.Options);
        var busy = (await created.Content.ReadFromJsonAsync<CreatedThreadResponse>(SharpClawJson.Options))!.Thread.Id;
        var idle = await fx.CreateThreadAsync(agent.Id);

        var first = await fx.PostUserMessageAsync(busy, "  Plan   my\n\ttrip to " + new string('x', 80));
        await fx.PostUserMessageAsync(busy, "second");
        await fx.PostUserMessageAsync(busy, "third");
        await fx.ClaimAsync(token);
        await fx.RequestApprovalAsync(token, busy, first);

        var list = (await admin.GetFromJsonAsync<ThreadListResponse>("/api/threads", SharpClawJson.Options))!;
        var row = Assert.Single(list.Threads, t => t.Id == busy);
        Assert.Equal("assistant", row.AgentName);
        Assert.True(row.InFlight);
        Assert.Equal(2, row.Queued);
        Assert.Equal(1, row.PendingApprovals);
        Assert.Equal("third", row.Preview);
        // Auto-title: the first user message, whitespace collapsed, cut to 60 characters.
        Assert.Equal(("Plan my trip to " + new string('x', 80))[..60], row.Title);

        var other = Assert.Single(list.Threads, t => t.Id == idle);
        Assert.False(other.InFlight);
        Assert.Equal(0, other.Queued);
        Assert.Equal(0, other.PendingApprovals);
        Assert.Equal("t", other.Title); // explicit titles are never replaced
        Assert.Null(other.Preview);

        // The final reply becomes the preview.
        using (var db = fx.NewDb())
            Assert.Equal(MessageStatus.Claimed, (await db.Messages.SingleAsync(m => m.Id == first)).Status);
        using var agentClient = fx.AgentClient(token);
        (await agentClient.PostAsJsonAsync($"/agent/threads/{busy}/messages",
            new AgentReplyRequest("Here is the   plan", first), SharpClawJson.Options)).EnsureSuccessStatusCode();
        list = (await admin.GetFromJsonAsync<ThreadListResponse>("/api/threads", SharpClawJson.Options))!;
        Assert.Equal("Here is the plan", Assert.Single(list.Threads, t => t.Id == busy).Preview);
    }

    [Fact]
    public async Task Preview_is_at_most_120_characters()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, new string('a', 500));
        using var db = fx.NewDb();
        Assert.Equal(120, (await db.Threads.SingleAsync(t => t.Id == thread)).LastPreview!.Length);
    }

    [Fact]
    public async Task Archived_threads_are_filtered_and_patch_validates_and_publishes()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var keep = await fx.CreateThreadAsync(agent.Id);
        var archive = await fx.CreateThreadAsync(agent.Id);
        using var admin = fx.Admin();

        var (client, stream) = await fx.OpenStreamAsync("/api/events");
        using var _ = client;
        var read = TestHelpers.ReadSseEventsAsync(stream, 1, TimeSpan.FromSeconds(5));

        var patched = await admin.PatchAsJsonAsync($"/api/threads/{archive}",
            new UpdateThreadRequest("  Renamed  ", true), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var dto = (await patched.Content.ReadFromJsonAsync<ThreadResponse>(SharpClawJson.Options))!.Thread;
        Assert.Equal("Renamed", dto.Title);
        Assert.True(dto.Archived);

        var evt = Assert.Single(await read);
        Assert.Equal(SseEventNames.ThreadUpdated, evt.Event);
        Assert.Equal(new ThreadUpdatedEvent(archive, "Renamed", true), ClientApiHelpers.Read<ThreadUpdatedEvent>(evt.Data));

        var active = (await admin.GetFromJsonAsync<ThreadListResponse>("/api/threads", SharpClawJson.Options))!;
        Assert.Equal(new[] { keep }, active.Threads.Select(t => t.Id));
        var archived = (await admin.GetFromJsonAsync<ThreadListResponse>("/api/threads?archived=true", SharpClawJson.Options))!;
        Assert.Equal(new[] { archive }, archived.Threads.Select(t => t.Id));

        foreach (var body in new object[] { new { }, new { title = "   " }, new { title = new string('t', 121) } })
        {
            var resp = await admin.PatchAsJsonAsync($"/api/threads/{keep}", body, SharpClawJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PatchAsJsonAsync($"/api/threads/{Guid.NewGuid()}", new UpdateThreadRequest("x", null), SharpClawJson.Options)).StatusCode);
    }

    [Fact]
    public async Task Creating_a_thread_publishes_thread_created()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var (client, stream) = await fx.OpenStreamAsync("/api/events");
        using var _ = client;
        var read = TestHelpers.ReadSseAsync(stream, 1, TimeSpan.FromSeconds(5), skipReady: true);

        var thread = await fx.CreateThreadAsync(agent.Id);

        var evt = Assert.Single(await read);
        Assert.Equal(SseEventNames.ThreadCreated, evt.Event);
        Assert.NotNull(evt.Id);
        Assert.Equal(new ThreadCreatedEvent(thread, agent.Id), ClientApiHelpers.Read<ThreadCreatedEvent>(evt.Data));
    }

    [Fact]
    public async Task Detail_has_agent_in_flight_queue_and_pending_approvals()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync(budget: 2.5);
        var thread = await fx.CreateThreadAsync(agent.Id);
        using var admin = fx.Admin();

        var idle = (await admin.GetFromJsonAsync<ThreadDetailResponse>($"/api/threads/{thread}", SharpClawJson.Options))!;
        Assert.Null(idle.InFlight);
        Assert.Equal(0, idle.Queued);
        Assert.Empty(idle.PendingApprovals);
        Assert.Equal("assistant", idle.Agent.Name);
        Assert.Equal(2.5, idle.Agent.RemainingUsd);
        Assert.Equal(ContextDefaults.DefaultWindowTokens, idle.Thread.Context!.WindowTokens);

        var first = await fx.PostUserMessageAsync(thread, "one");
        await fx.PostUserMessageAsync(thread, "two");
        await fx.ClaimAsync(token);
        var approval = await fx.RequestApprovalAsync(token, thread, first, RiskLevels.Critical);

        var busy = (await admin.GetFromJsonAsync<ThreadDetailResponse>($"/api/threads/{thread}", SharpClawJson.Options))!;
        Assert.Equal(first, busy.InFlight!.CorrelationId);
        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, busy.InFlight.ClaimedAt);
        Assert.Equal(1, busy.Queued);
        var pending = Assert.Single(busy.PendingApprovals);
        Assert.Equal(approval, pending.Id);
        Assert.Equal(RiskLevels.Critical, pending.Risk);
        Assert.Equal("Delete notes/a.md", pending.Summary);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/threads/{Guid.NewGuid()}")).StatusCode);
    }
}
