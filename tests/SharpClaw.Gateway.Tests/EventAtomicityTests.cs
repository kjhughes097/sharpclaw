using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Durable events commit with the state change they describe, reach the bus in id order, and
/// failed writes publish nothing.
/// </summary>
public class EventAtomicityTests
{
    [Fact]
    public async Task Concurrent_writes_publish_in_increasing_id_order_and_match_the_stored_rows()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var threads = new[] { await fx.CreateThreadAsync(agent.Id), await fx.CreateThreadAsync(agent.Id) };
        var bus = fx.App.Services.GetRequiredService<IThreadEventBus>();
        using var sub = bus.Subscribe(null, out var reader);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => fx.PostUserMessageAsync(threads[i % 2], $"m{i}")));

        var live = new List<long>();
        while (reader.TryRead(out var e))
            if (e.Event == SseEventNames.MessageCreated)
                live.Add(e.Id!.Value);
        Assert.Equal(20, live.Count);
        Assert.Equal(live.OrderBy(x => x), live);

        using var db = fx.NewDb();
        var stored = await db.ThreadEvents.AsNoTracking().Where(e => e.Type == SseEventNames.MessageCreated)
            .OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
        Assert.Equal(stored, live);
    }

    [Fact]
    public async Task A_write_that_fails_to_commit_stores_and_publishes_nothing()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var bus = fx.App.Services.GetRequiredService<IThreadEventBus>();
        using var sub = bus.Subscribe(thread, out var reader);
        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => events.WriteAsync<bool>(db, batch =>
        {
            batch.Add(thread, SseEventNames.ThreadUpdated, new ThreadUpdatedEvent(thread, "x", false));
            throw new InvalidOperationException("boom");
        }, default));

        Assert.False(reader.TryRead(out _));
        using var check = fx.NewDb();
        Assert.False(await check.ThreadEvents.AnyAsync(e => e.Type == SseEventNames.ThreadUpdated));
    }

    [Fact]
    public async Task Nested_event_writes_are_rejected_rather_than_deadlocking()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var scope = fx.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        var events = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => events.WriteAsync(db,
            _ => events.WriteAsync(db, __ => Task.FromResult(1), default), default));
        Assert.Contains("nested", ex.Message);
    }

    [Fact]
    public async Task Failing_a_turn_emits_an_error_with_readable_text_after_its_expired_approvals()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "x");
        await fx.ClaimAsync(token);
        await fx.RequestApprovalAsync(token, thread, corr);

        using (var c = fx.AgentClient(token))
            (await c.PostAsJsonAsync($"/agent/messages/{corr}/fail",
                new FailMessageRequest(ErrorCodes.BudgetExceeded), SharpClawJson.Options)).EnsureSuccessStatusCode();

        using var db = fx.NewDb();
        var rows = await db.ThreadEvents.AsNoTracking().Where(e => e.ThreadId == thread).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new[] { SseEventNames.ThreadCreated, SseEventNames.MessageCreated, SseEventNames.ApprovalRequested,
            SseEventNames.ApprovalDecided, SseEventNames.Error }, rows.Select(r => r.Type));
        var error = JsonNode.Parse(rows[^1].Data)!;
        Assert.Equal(ErrorCodes.BudgetExceeded, (string?)error["code"]);
        Assert.Equal(FailReasons.Describe(ErrorCodes.BudgetExceeded), (string?)error["message"]);
    }

    [Theory]
    [InlineData(ErrorCodes.BudgetExceeded, "budget")]
    [InlineData(ErrorCodes.ContextTooLarge, "context window")]
    [InlineData(FailReasons.ToolLoopLimit, "tools")]
    [InlineData(FailReasons.AgentError, "internal error")]
    [InlineData("weird_code", "(weird_code)")]
    public void Fail_reasons_have_readable_text(string code, string expected) =>
        Assert.Contains(expected, FailReasons.Describe(code));
}
