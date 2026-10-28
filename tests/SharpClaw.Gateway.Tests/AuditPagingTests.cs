using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

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
