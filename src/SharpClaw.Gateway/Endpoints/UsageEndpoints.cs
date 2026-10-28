using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// <c>GET /api/usage?range=today|7d|30d&amp;agent=</c> (3.3.1 spec section 3.1): token and cost
/// buckets for the Overview and agent tabs. Rows are aggregated in memory (see the tech-debt register).
/// </summary>
public static class UsageEndpoints
{
    public static void Map(RouteGroupBuilder client)
    {
        client.MapGet("/usage", Get).WithName("getUsage").WithTags("usage").Produces<UsageResponse>();
    }

    private static async Task<IResult> Get(
        string? range, string? agent, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        range ??= UsageRanges.Today;
        var window = UsageReport.WindowFor(range, time.GetUtcNow().UtcDateTime);
        if (window is null)
            return ApiResults.ValidationFailed($"range must be one of: {string.Join(", ", UsageRanges.All)}.");

        IQueryable<LlmCall> calls = db.LlmCalls.AsNoTracking().Where(c => c.CreatedAt >= window.From);
        IQueryable<Agent> agents = db.Agents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(agent))
        {
            var resolved = await AgentQueries.ResolveAsync(db, agent, ct);
            if (resolved is null)
                return ApiResults.NotFound("Agent not found.");
            calls = calls.Where(c => c.AgentId == resolved.Id);
            agents = agents.Where(a => a.Id == resolved.Id);
        }

        var rows = await calls
            .Select(c => new UsageReport.CallRow(
                c.AgentId, c.Model, c.CreatedAt, c.PromptTokens, c.CompletionTokens, c.CostUsd, c.UsageKnown))
            .ToListAsync(ct);
        var agentList = (await agents.Select(a => new { a.Id, a.Name }).ToListAsync(ct))
            .Select(a => (a.Id, a.Name)).ToList();

        return Results.Json(UsageReport.Build(range, window, rows, agentList), SharpClawJson.Options);
    }
}
