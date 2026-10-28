using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Tests;

/// <summary>Focused tests for small gateway building blocks that the HTTP tests only reach indirectly.</summary>
public class InfrastructureTests
{
    // ---- PageCursor ----

    [Fact]
    public void Cursor_round_trips_and_is_url_safe()
    {
        var cursor = new PageCursor(new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc).AddTicks(7), Guid.CreateVersion7());
        var encoded = cursor.Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.True(PageCursor.TryDecode(encoded, out var decoded));
        Assert.Equal(cursor, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.At.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("bm90LWEtY3Vyc29y")]                    // "not-a-cursor"
    [InlineData("LTE6MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA")] // negative ticks
    public void Cursor_rejects_garbage(string? value) => Assert.False(PageCursor.TryDecode(value, out _));

    [Fact]
    public void Cursor_rejects_overlong_values() =>
        Assert.False(PageCursor.TryDecode(new string('A', 101), out _));

    // ---- ActiveTurnBuffers ----

    private static ActiveTurnBuffers Buffers(FakeTimeProvider time) => new(time, NullLogger<ActiveTurnBuffers>.Instance);

    [Fact]
    public void Turn_buffers_track_offsets_per_turn_and_filter_snapshots_by_thread()
    {
        var buffers = Buffers(new FakeTimeProvider());
        var (threadA, threadB, turnA, turnB) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(3, buffers.Append(threadA, turnA, "abc"));
        Assert.Equal(5, buffers.Append(threadA, turnA, "de"));
        Assert.Equal(1, buffers.Append(threadB, turnB, "x"));

        Assert.Equal("abcde", Assert.Single(buffers.Snapshots(threadA)).Content);
        Assert.Equal(2, buffers.Snapshots(null).Count);
        buffers.Remove(turnA);
        Assert.Empty(buffers.Snapshots(threadA));
    }

    [Fact]
    public void Turn_buffers_stop_at_the_byte_cap_and_age_out()
    {
        var time = new FakeTimeProvider();
        var buffers = Buffers(time);
        var (thread, turn) = (Guid.NewGuid(), Guid.NewGuid());

        Assert.NotNull(buffers.Append(thread, turn, new string('a', ActiveTurnBuffers.MaxBytes)));
        Assert.Null(buffers.Append(thread, turn, "more"));
        Assert.Equal(ActiveTurnBuffers.MaxBytes, Assert.Single(buffers.Snapshots(thread)).Content.Length);

        time.Advance(TimeSpan.FromMinutes(16));
        Assert.Empty(buffers.Snapshots(thread));
    }

    // ---- KeyedSignal ----

    [Fact]
    public async Task Signals_wake_one_waiter_without_piling_up_permits()
    {
        var signal = new AgentInboxSignal();
        var key = Guid.NewGuid();

        // Signalled before anyone waits: the next wait returns at once, but only once.
        signal.Signal(key);
        signal.Signal(key);
        Assert.True(await signal.WaitAsync(key, TimeSpan.FromSeconds(1), default));
        Assert.False(await signal.WaitAsync(key, TimeSpan.FromMilliseconds(20), default));

        var waiting = signal.WaitAsync(key, TimeSpan.FromSeconds(5), default);
        await TestHelpers.EventuallyAsync(() => signal.Waiters == 1);
        signal.Signal(key);
        Assert.True(await waiting);
        Assert.Equal(0, signal.Waiters);

        signal.Remove(key);
        Assert.Equal(0, signal.Count);
    }

    // ---- ExceptionMiddleware ----

    private static async Task<(int Status, string Body)> InvokeAsync(RequestDelegate next, HttpContext? context = null)
    {
        var http = context ?? new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await new ExceptionMiddleware(next, NullLogger<ExceptionMiddleware>.Instance).Invoke(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Unhandled_exceptions_become_the_error_shape_without_details()
    {
        var (status, body) = await InvokeAsync(_ => throw new InvalidOperationException("SECRET detail"));

        Assert.Equal(500, status);
        Assert.Equal(ErrorCodes.InternalError, (string?)JsonNode.Parse(body)!["error"]!["code"]);
        Assert.DoesNotContain("SECRET", body);
    }

    [Fact]
    public async Task A_client_abort_writes_nothing()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var http = new DefaultHttpContext { RequestAborted = aborted.Token };

        var (status, body) = await InvokeAsync(_ => throw new OperationCanceledException(), http);

        Assert.Equal(200, status);
        Assert.Equal("", body);
    }

    // ---- EventRetentionService ----

    [Fact]
    public async Task Retention_prunes_old_events_and_records_the_watermark()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "old");
        fx.Time.Advance(TimeSpan.FromDays(15));
        await fx.PostUserMessageAsync(thread, "new");
        var retention = fx.App.Services.GetRequiredService<EventRetentionService>();

        await retention.PruneAsync(default);
        await retention.PruneAsync(default);

        using var db = fx.NewDb();
        var remaining = await db.ThreadEvents.AsNoTracking().Where(e => e.ThreadId == thread).ToListAsync();
        var kept = Assert.Single(remaining);
        Assert.Equal(SseEventNames.MessageCreated, kept.Type);
        Assert.Equal(kept.Id - 1, (await db.EventLogState.AsNoTracking().SingleAsync()).PrunedThrough);
    }

    // ---- ApprovalDecisions ----

    [Fact]
    public async Task Concurrent_decisions_on_one_approval_have_exactly_one_winner()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "go");
        await fx.ClaimAsync(token);
        var approvalId = await fx.RequestApprovalAsync(token, thread, corr);

        async Task<bool> DecideAsync(string status)
        {
            using var scope = fx.App.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
            var approval = await db.Approvals.AsNoTracking().SingleAsync(a => a.Id == approvalId);
            return await scope.ServiceProvider.GetRequiredService<ApprovalDecisions>()
                .TryDecideAsync(db, approval, status, ApprovalDecidedBy.User, default, "kevin");
        }

        var results = await Task.WhenAll(
            DecideAsync(ApprovalStatuses.Approved), DecideAsync(ApprovalStatuses.Denied), DecideAsync(ApprovalStatuses.Approved));

        Assert.Single(results, r => r);
        using var check = fx.NewDb();
        Assert.Single(await check.ThreadEvents.AsNoTracking()
            .Where(e => e.ThreadId == thread && e.Type == SseEventNames.ApprovalDecided).ToListAsync());
    }
}
