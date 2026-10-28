using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// The two audit tables, newest first with the same filters and keyset paging: pass the
/// response's <c>nextCursor</c> as <c>before</c> to get the next page.
/// </summary>
public static class AuditEndpoints
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    public static void Map(RouteGroupBuilder client)
    {
        var audit = client.MapGroup("/audit").WithTags("audit");
        audit.MapGet("/llm-calls", ListCalls).WithName("listLlmCalls").Produces<AuditResponse>();
        audit.MapGet("/tool-calls", ListToolCalls).WithName("listToolCalls").Produces<ToolCallAuditResponse>();
    }

    private static async Task<IResult> ListCalls(
        string? agent, Guid? threadId, string? status, DateTime? since, int? limit, string? before,
        SharpClawDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        IQueryable<LlmCall> q = db.LlmCalls.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(agent))
        {
            var resolved = await AgentQueries.ResolveAsync(db, agent, ct);
            if (resolved is null)
                return ApiResults.NotFound("Agent not found.");
            q = q.Where(c => c.AgentId == resolved.Id);
        }
        if (threadId is { } tid)
            q = q.Where(c => c.ThreadId == tid);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(c => c.Status == status);
        if (since is { } s)
        {
            var sinceUtc = s.ToUniversalTime();
            q = q.Where(c => c.CreatedAt >= sinceUtc);
        }
        if (before is not null)
        {
            if (!PageCursor.TryDecode(before, out var cur))
                return ApiResults.ValidationFailed("before is not a valid cursor.");
            q = q.Where(c => c.CreatedAt < cur.At || (c.CreatedAt == cur.At && c.Id.CompareTo(cur.Id) < 0));
        }

        var calls = await q.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(take + 1)
            .Select(c => new LlmCallDto(
                c.Id, c.AgentId, c.ThreadId, c.CorrelationId, c.Model, c.Status,
                c.PromptTokens, c.CompletionTokens, c.CostUsd, c.UsageKnown,
                c.LatencyMs, c.TtftMs, c.ErrorCode, c.ErrorDetail, c.CreatedAt, c.EstimatedPromptTokens, c.Purpose))
            .ToListAsync(ct);

        var next = NextCursor(calls, take, c => new PageCursor(c.CreatedAt, c.Id));
        return Results.Json(new AuditResponse(calls, next), SharpClawJson.Options);
    }

    private static async Task<IResult> ListToolCalls(
        string? agent, Guid? threadId, string? status, DateTime? since, int? limit, string? before,
        SharpClawDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        IQueryable<ToolCallRecord> q = db.ToolCalls.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(agent))
        {
            var resolved = await AgentQueries.ResolveAsync(db, agent, ct);
            if (resolved is null)
                return ApiResults.NotFound("Agent not found.");
            q = q.Where(c => c.AgentId == resolved.Id);
        }
        if (threadId is { } tid)
            q = q.Where(c => c.ThreadId == tid);
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(c => c.Status == status);
        if (since is { } s)
        {
            var sinceUtc = s.ToUniversalTime();
            q = q.Where(c => c.CreatedAt >= sinceUtc);
        }
        if (before is not null)
        {
            if (!PageCursor.TryDecode(before, out var cur))
                return ApiResults.ValidationFailed("before is not a valid cursor.");
            q = q.Where(c => c.CreatedAt < cur.At || (c.CreatedAt == cur.At && c.Id.CompareTo(cur.Id) < 0));
        }

        var calls = await q.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Take(take + 1)
            .Select(c => new ToolCallDto(
                c.Id, c.AgentId, c.ThreadId, c.CorrelationId, c.LlmCallId, c.ToolCallId, c.ToolName, c.Arguments,
                c.Status, c.Decision, c.Risk, c.Tainted, c.ApprovalId, c.ErrorCode, c.DurationMs,
                c.CreatedAt, c.ReportedAt))
            .ToListAsync(ct);

        var next = NextCursor(calls, take, c => new PageCursor(c.CreatedAt, c.Id));
        return Results.Json(new ToolCallAuditResponse(calls, next), SharpClawJson.Options);
    }

    /// <summary>Queries fetch one extra row to learn whether another page exists; this drops it.</summary>
    private static string? NextCursor<T>(List<T> rows, int take, Func<T, PageCursor> cursorOf)
    {
        if (rows.Count <= take)
            return null;
        rows.RemoveAt(rows.Count - 1);
        return cursorOf(rows[^1]).Encode();
    }
}
