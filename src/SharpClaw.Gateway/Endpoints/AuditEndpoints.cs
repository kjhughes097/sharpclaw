using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

public static class AuditEndpoints
{
    public static void Map(RouteGroupBuilder client)
    {
        client.MapGet("/audit/llm-calls", ListCalls);
    }

    private static async Task<IResult> ListCalls(
        string? agent, Guid? threadId, DateTime? since, int? limit,
        SharpClawDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 100, 1, 500);
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
        if (since is { } s)
        {
            var sinceUtc = s.ToUniversalTime();
            q = q.Where(c => c.CreatedAt >= sinceUtc);
        }

        var calls = await q.OrderByDescending(c => c.CreatedAt).Take(take)
            .Select(c => new LlmCallDto(
                c.Id, c.AgentId, c.ThreadId, c.CorrelationId, c.Model, c.Status,
                c.PromptTokens, c.CompletionTokens, c.CostUsd, c.UsageKnown,
                c.LatencyMs, c.TtftMs, c.ErrorCode, c.ErrorDetail, c.CreatedAt))
            .ToListAsync(ct);

        return Results.Json(new AuditResponse(calls), SharpClawJson.Options);
    }
}
