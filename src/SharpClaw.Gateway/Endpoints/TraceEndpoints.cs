using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// The per-thread trace (3.3 spec section 5.3): one entry per user turn joining LLM calls, tool
/// calls and approvals by correlation id. Metadata only — the projections below deliberately never
/// select message content, tool arguments, results or provider error details.
/// </summary>
public static class TraceEndpoints
{
    public const int MaxTurns = 100;

    public static async Task<IResult> GetTrace(Guid id, SharpClawDbContext db, CancellationToken ct)
    {
        if (!await db.Threads.AsNoTracking().AnyAsync(t => t.Id == id, ct))
            return ApiResults.NotFound("Thread not found.");

        var users = await db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == id && m.Role == Roles.User && m.Kind == MessageKinds.Message)
            .OrderByDescending(m => m.Seq).Take(MaxTurns)
            .Select(m => new { m.Id, m.Status, m.FailReason, m.CreatedAt, m.ClaimedAt, m.CompletedAt, m.Seq })
            .ToListAsync(ct);
        users.Reverse();
        var ids = users.Select(u => u.Id).ToList();

        var llm = await db.LlmCalls.AsNoTracking()
            .Where(c => c.ThreadId == id && ids.Contains(c.CorrelationId))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new { c.CorrelationId, Dto = new TraceLlmCallDto(c.Id, c.Model, c.Status, c.Purpose,
                c.PromptTokens, c.CompletionTokens, c.EstimatedPromptTokens, c.CostUsd, c.UsageKnown, c.LatencyMs,
                c.TtftMs, c.ErrorCode, c.CreatedAt) })
            .ToListAsync(ct);
        var tools = await db.ToolCalls.AsNoTracking()
            .Where(c => c.ThreadId == id && ids.Contains(c.CorrelationId))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new { c.CorrelationId, Dto = new TraceToolCallDto(c.Id, c.ToolCallId, c.ToolName, c.Risk,
                c.Decision, c.Status, c.ApprovalId, c.DurationMs, c.CreatedAt, c.ReportedAt) })
            .ToListAsync(ct);
        var approvals = await db.Approvals.AsNoTracking()
            .Where(a => a.ThreadId == id && ids.Contains(a.CorrelationId))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new { a.CorrelationId, Dto = new TraceApprovalDto(a.Id, a.ToolCallId, a.ToolName, a.Risk,
                a.Status, a.CreatedAt, a.DecidedAt, a.DecidedBy) })
            .ToListAsync(ct);

        var turns = users.Select(u =>
        {
            var turnLlm = llm.Where(c => c.CorrelationId == u.Id).Select(c => c.Dto).ToList();
            var turnTools = tools.Where(c => c.CorrelationId == u.Id).Select(c => c.Dto).ToList();
            var totals = new TraceTotalsDto(
                turnLlm.Sum(c => c.CostUsd), turnLlm.Sum(c => c.PromptTokens), turnLlm.Sum(c => c.CompletionTokens),
                turnLlm.Count, turnTools.Count);
            return new TraceTurnDto(u.Id, u.Id, u.Status, u.FailReason, u.CreatedAt, u.ClaimedAt, u.CompletedAt,
                turnLlm, turnTools, approvals.Where(a => a.CorrelationId == u.Id).Select(a => a.Dto).ToList(), totals);
        }).ToList();

        return Results.Json(new TraceResponse(turns), SharpClawJson.Options);
    }
}
