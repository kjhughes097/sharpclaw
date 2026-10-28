using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Shared helpers for the 3.3 client-API tests.</summary>
public static class ClientApiHelpers
{
    /// <summary>The repository root (where <c>SharpClaw.slnx</c> lives), for snapshot files.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SharpClaw.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>Claims the next message and replies to it.</summary>
    public static async Task ReplyAsync(this GatewayFixture fx, string token, Guid threadId, Guid correlationId, string content)
    {
        var claimed = await fx.ClaimAsync(token);
        Assert.Equal(correlationId, claimed?.Id);
        using var agent = fx.AgentClient(token);
        (await agent.PostAsJsonAsync($"/agent/threads/{threadId}/messages",
            new AgentReplyRequest(content, correlationId), SharpClawJson.Options)).EnsureSuccessStatusCode();
    }

    /// <summary>The agent asks for approval on its claimed message; returns the approval id.</summary>
    public static async Task<Guid> RequestApprovalAsync(
        this GatewayFixture fx, string token, Guid threadId, Guid correlationId, string risk = RiskLevels.High)
    {
        using var agent = fx.AgentClient(token);
        var resp = await agent.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(
            correlationId, threadId, "call_1", "workspace_delete", "Delete notes/a.md", risk, "Deletes a file"),
            SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval.Id;
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options))!.Error.Code;

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, SharpClawJson.Options)!;
}

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
        await Task.Delay(200);
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
        await Task.Delay(200);
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

/// <summary>Message paging (3.3 spec section 5.2).</summary>
public class MessagePaginationTests
{
    private static async Task<(GatewayFixture Fx, Guid Thread, string Token)> SetupAsync(int turns)
    {
        var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        for (var i = 0; i < turns; i++)
        {
            var corr = await fx.PostUserMessageAsync(thread, $"q{i}");
            await fx.ReplyAsync(token, thread, corr, $"a{i}");
        }
        return (fx, thread, token);
    }

    private static async Task<MessageListResponse> GetAsync(GatewayFixture fx, Guid thread, string query)
    {
        using var admin = fx.Admin();
        return (await admin.GetFromJsonAsync<MessageListResponse>($"/api/threads/{thread}/messages?{query}", SharpClawJson.Options))!;
    }

    [Fact]
    public async Task Latest_page_then_before_and_after_seq()
    {
        var (fx, thread, _) = await SetupAsync(5); // seq 1..10
        await using var _ = fx;

        var latest = await GetAsync(fx, thread, "limit=4");
        Assert.Equal(new[] { 7, 8, 9, 10 }, latest.Messages.Select(m => m.Seq));
        Assert.True(latest.HasMoreBefore);

        var earlier = await GetAsync(fx, thread, "limit=4&beforeSeq=7");
        Assert.Equal(new[] { 3, 4, 5, 6 }, earlier.Messages.Select(m => m.Seq));
        Assert.True(earlier.HasMoreBefore);

        var first = await GetAsync(fx, thread, "limit=4&beforeSeq=3");
        Assert.Equal(new[] { 1, 2 }, first.Messages.Select(m => m.Seq));
        Assert.False(first.HasMoreBefore);

        var after = await GetAsync(fx, thread, "limit=3&afterSeq=4");
        Assert.Equal(new[] { 5, 6, 7 }, after.Messages.Select(m => m.Seq));
        Assert.True(after.HasMoreBefore);

        var all = await GetAsync(fx, thread, "");
        Assert.Equal(10, all.Messages.Count);
        Assert.False(all.HasMoreBefore);

        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/threads/{thread}/messages?beforeSeq=3&afterSeq=1")).StatusCode);
    }

    [Fact]
    public async Task Existing_after_id_still_works_and_limit_is_capped()
    {
        var (fx, thread, _) = await SetupAsync(3);
        await using var _ = fx;
        var all = await GetAsync(fx, thread, "");
        var anchor = all.Messages[1];

        var after = await GetAsync(fx, thread, $"after={anchor.Id}");
        Assert.Equal(all.Messages.Skip(2).Select(m => m.Id), after.Messages.Select(m => m.Id));

        // limit above the maximum is clamped rather than rejected.
        var capped = await GetAsync(fx, thread, "limit=100000");
        Assert.Equal(6, capped.Messages.Count);
    }

    [Fact]
    public async Task Include_tools_counts_every_kind_in_the_limit()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var corr = await fx.PostUserMessageAsync(thread, "read it");
        await fx.ClaimAsync(token);
        using (var agentClient = fx.AgentClient(token))
        {
            var exchange = new ToolExchangeDto(null,
                new[] { new ChatToolCall("call_1", "workspace_read", "{\"path\":\"a.md\"}") },
                new[] { new ToolResultDto("call_1", "{\"ok\":true}", true, false) });
            (await agentClient.PostAsJsonAsync($"/agent/threads/{thread}/messages",
                new AgentReplyRequest("done", corr, new[] { exchange }), SharpClawJson.Options)).EnsureSuccessStatusCode();
        }

        var withTools = await GetAsync(fx, thread, "includeTools=true&limit=3");
        Assert.Equal(new[] { MessageKinds.ToolRequest, MessageKinds.ToolResult, MessageKinds.Message },
            withTools.Messages.Select(m => m.Kind));
        Assert.True(withTools.HasMoreBefore);

        var plain = await GetAsync(fx, thread, "limit=3");
        Assert.Equal(2, plain.Messages.Count);
        Assert.All(plain.Messages, m => Assert.Equal(MessageKinds.Message, m.Kind));
        Assert.False(plain.HasMoreBefore);
    }
}

/// <summary>The per-thread trace (3.3 spec section 5.3).</summary>
public class TraceTests
{
    [Fact]
    public async Task Trace_joins_calls_tools_and_approvals_per_turn_without_content()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        const string secret = "SECRET-PROMPT-CONTENT";

        var corr = await fx.PostUserMessageAsync(thread, secret);
        await fx.ClaimAsync(token);
        await (await fx.ChatAsync(token, corr, thread, secret)).Content.ReadAsStringAsync();
        await fx.RequestApprovalAsync(token, thread, corr);
        var llmId = Guid.CreateVersion7();
        var now = fx.Time.GetUtcNow().UtcDateTime;
        using (var db = fx.NewDb())
        {
            db.ToolCalls.Add(new ToolCallRecord
            {
                Id = Guid.CreateVersion7(), AgentId = agent.Id, ThreadId = thread, CorrelationId = corr, LlmCallId = llmId,
                ToolCallId = "call_1", ToolName = "workspace_delete", Arguments = "{\"path\":\"SECRET-ARGUMENT\"}",
                Status = ToolCallStatuses.Succeeded, Decision = ToolDecisions.Approved, Risk = RiskLevels.High,
                DurationMs = 12, CreatedAt = now, ReportedAt = now,
            });
            await db.SaveChangesAsync();
        }
        using (var agentClient = fx.AgentClient(token))
            (await agentClient.PostAsJsonAsync($"/agent/threads/{thread}/messages",
                new AgentReplyRequest("SECRET-REPLY", corr), SharpClawJson.Options)).EnsureSuccessStatusCode();
        var second = await fx.PostUserMessageAsync(thread, "queued");

        using var admin = fx.Admin();
        var raw = await admin.GetStringAsync($"/api/threads/{thread}/trace");
        Assert.DoesNotContain("SECRET", raw);
        var trace = ClientApiHelpers.Read<TraceResponse>(raw);

        Assert.Equal(new[] { corr, second }, trace.Turns.Select(t => t.CorrelationId));
        var turn = trace.Turns[0];
        Assert.Equal(MessageStatus.Done, turn.Status);
        Assert.NotNull(turn.ClaimedAt);
        Assert.NotNull(turn.CompletedAt);
        var call = Assert.Single(turn.LlmCalls);
        Assert.Equal(LlmCallStatus.Ok, call.Status);
        var tool = Assert.Single(turn.ToolCalls);
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal(ToolCallStatuses.Succeeded, tool.Outcome);
        Assert.Equal(ToolDecisions.Approved, tool.Decision);
        var approval = Assert.Single(turn.Approvals);
        Assert.Equal(ApprovalStatuses.Expired, approval.Status); // the reply ended the turn
        Assert.Equal(new TraceTotalsDto(call.CostUsd, call.PromptTokens, call.CompletionTokens, 1, 1), turn.Totals);

        Assert.Equal(MessageStatus.Pending, trace.Turns[1].Status);
        Assert.Empty(trace.Turns[1].LlmCalls);
    }

    [Fact]
    public async Task Trace_is_capped_at_the_latest_100_turns()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        var ids = new List<Guid>();
        for (var i = 0; i < 105; i++)
            ids.Add(await fx.PostUserMessageAsync(thread, $"m{i}"));

        using var admin = fx.Admin();
        var trace = (await admin.GetFromJsonAsync<TraceResponse>($"/api/threads/{thread}/trace", SharpClawJson.Options))!;
        Assert.Equal(ids.Skip(5), trace.Turns.Select(t => t.CorrelationId));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/threads/{Guid.NewGuid()}/trace")).StatusCode);
    }
}

/// <summary>Audit filters and cursor paging (3.3 spec section 5.4).</summary>
public class AuditPagingTests
{
    [Fact]
    public async Task Llm_and_tool_audit_page_by_cursor_and_filter()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        var (other, _) = await fx.CreateAgentAsync("other");
        var thread = await fx.CreateThreadAsync(agent.Id);
        var start = fx.Time.GetUtcNow().UtcDateTime;
        using (var db = fx.NewDb())
        {
            for (var i = 0; i < 7; i++)
            {
                // Two rows share each timestamp so the id tiebreaker is exercised.
                var at = start.AddMinutes(i / 2);
                db.LlmCalls.Add(new LlmCall
                {
                    Id = Guid.CreateVersion7(), AgentId = agent.Id, ThreadId = thread, CorrelationId = Guid.NewGuid(),
                    Model = "m", Status = i == 3 ? LlmCallStatus.Error : LlmCallStatus.Ok, CreatedAt = at,
                    EstimatedPromptTokens = 40,
                });
                db.ToolCalls.Add(new ToolCallRecord
                {
                    Id = Guid.CreateVersion7(), AgentId = i == 6 ? other.Id : agent.Id, ThreadId = thread,
                    CorrelationId = Guid.NewGuid(), LlmCallId = Guid.NewGuid(), ToolCallId = $"call_{i}",
                    ToolName = "workspace_read", Arguments = "{}", CreatedAt = at,
                    Status = i == 0 ? ToolCallStatuses.Requested : ToolCallStatuses.Succeeded,
                });
            }
            await db.SaveChangesAsync();
        }

        using var admin = fx.Admin();
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await admin.GetFromJsonAsync<AuditResponse>(
                "/api/audit/llm-calls?limit=3" + (cursor is null ? "" : $"&before={cursor}"), SharpClawJson.Options))!;
            Assert.True(page.Calls.Count <= 3);
            seen.AddRange(page.Calls.Select(c => c.Id));
            cursor = page.NextCursor;
        } while (cursor is not null);
        using (var db = fx.NewDb())
        {
            var expected = (await db.LlmCalls.AsNoTracking().ToListAsync())
                .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Select(c => c.Id);
            Assert.Equal(expected, seen);
        }

        var errors = (await admin.GetFromJsonAsync<AuditResponse>("/api/audit/llm-calls?status=error", SharpClawJson.Options))!;
        var error = Assert.Single(errors.Calls);
        Assert.Equal(40, error.EstimatedPromptTokens);

        var tools = (await admin.GetFromJsonAsync<ToolCallAuditResponse>(
            "/api/audit/tool-calls?agent=assistant&limit=4", SharpClawJson.Options))!;
        Assert.Equal(4, tools.Calls.Count);
        var rest = (await admin.GetFromJsonAsync<ToolCallAuditResponse>(
            $"/api/audit/tool-calls?agent=assistant&limit=4&before={tools.NextCursor}", SharpClawJson.Options))!;
        Assert.Equal(2, rest.Calls.Count);
        Assert.Null(rest.NextCursor);

        var requested = (await admin.GetFromJsonAsync<ToolCallAuditResponse>(
            "/api/audit/tool-calls?status=requested", SharpClawJson.Options))!;
        Assert.Equal("call_0", Assert.Single(requested.Calls).ToolCallId);

        var since = Uri.EscapeDataString(start.AddMinutes(3).ToString("O"));
        var recent = (await admin.GetFromJsonAsync<ToolCallAuditResponse>($"/api/audit/tool-calls?since={since}", SharpClawJson.Options))!;
        Assert.Equal("call_6", Assert.Single(recent.Calls).ToolCallId);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/audit/tool-calls?before=%%%")).StatusCode);
    }
}

/// <summary>System info and agent last-seen (3.3 spec sections 5.5 and 5.6).</summary>
public class SystemAndLastSeenTests
{
    [Fact]
    public async Task System_reports_version_provider_and_next_utc_midnight()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T21:30:00Z"));
        await using var fx = await GatewayFixture.CreateAsync(time);
        using var admin = fx.Admin();
        var info = (await admin.GetFromJsonAsync<SystemInfoResponse>("/api/system", SharpClawJson.Options))!;
        Assert.Equal("Fake", info.Provider);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.DoesNotContain("+", info.Version);
        Assert.Equal(DateTime.Parse("2026-10-25T21:30:00Z").ToUniversalTime(), info.UtcNow);
        Assert.Equal(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc), info.BudgetResetAtUtc);
        Assert.Equal(DateTimeKind.Utc, info.BudgetResetAtUtc.Kind);
    }

    [Fact]
    public async Task Last_seen_is_null_until_the_agent_calls_and_updates_at_most_once_a_minute()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        using var admin = fx.Admin();
        Assert.Null((await admin.GetFromJsonAsync<AgentDto>("/api/agents/assistant", SharpClawJson.Options))!.LastSeenAt);

        var t0 = fx.Time.GetUtcNow().UtcDateTime;
        await fx.ClaimAsync(token);
        Assert.Equal(t0, (await admin.GetFromJsonAsync<AgentDto>("/api/agents/assistant", SharpClawJson.Options))!.LastSeenAt);

        fx.Time.Advance(TimeSpan.FromSeconds(30));
        await fx.ClaimAsync(token);
        Assert.Equal(t0, (await admin.GetFromJsonAsync<AgentDto>("/api/agents/assistant", SharpClawJson.Options))!.LastSeenAt);

        fx.Time.Advance(TimeSpan.FromSeconds(31));
        await fx.ClaimAsync(token);
        var list = (await admin.GetFromJsonAsync<AgentListResponse>("/api/agents", SharpClawJson.Options))!;
        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, Assert.Single(list.Agents, a => a.Id == agent.Id).LastSeenAt);
    }
}
