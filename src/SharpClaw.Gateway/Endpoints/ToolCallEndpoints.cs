using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>Agent reports of tool-call outcomes (3.2 spec section 8.5).</summary>
public static class ToolCallEndpoints
{
    public static void Map(RouteGroupBuilder agent)
    {
        agent.MapPost("/tool-calls", Report);
    }

    private static async Task<IResult> Report(
        ToolCallReportRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        GatewayMetrics metrics, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        if (body is null || string.IsNullOrEmpty(body.ToolCallId))
            return ApiResults.ValidationFailed("toolCallId is required.");
        if (!ToolCallStatuses.Reportable.Contains(body.Status))
            return ApiResults.ValidationFailed($"status must be one of: {string.Join(", ", ToolCallStatuses.Reportable)}.");
        if (body.Decision is not null && !ToolDecisions.All.Contains(body.Decision))
            return ApiResults.ValidationFailed($"decision must be one of: {string.Join(", ", ToolDecisions.All)}.");
        if (body.Risk is not null && !RiskLevels.All.Contains(body.Risk))
            return ApiResults.ValidationFailed($"risk must be one of: {string.Join(", ", RiskLevels.All)}.");
        if (body.ErrorCode is { Length: > 100 })
            return ApiResults.ValidationFailed("errorCode must be at most 100 characters.");

        // The row only exists if the model actually requested this call through the gateway,
        // so an agent cannot report (or invent) calls the model never asked for.
        var row = await db.ToolCalls.FirstOrDefaultAsync(
            t => t.CorrelationId == body.CorrelationId && t.ToolCallId == body.ToolCallId, ct);
        if (row is null || row.AgentId != agent.Id)
            return ApiResults.NotFound("Tool call not found.");
        if (row.ReportedAt is not null)
            return ApiResults.Conflict("This tool call has already been reported.");

        row.Status = body.Status;
        row.Decision = body.Decision;
        row.Risk = body.Risk;
        row.Tainted = body.Tainted;
        row.ApprovalId = body.ApprovalId;
        row.ErrorCode = body.ErrorCode;
        row.DurationMs = body.DurationMs;
        row.ReportedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);

        metrics.RecordToolCall(agent.Name, row.ToolName, row.Status);
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Tools");
        using (LogScopes.Turn(logger, row.ThreadId, row.CorrelationId))
            logger.LogInformation(
                "tool call reported: tool={Tool} status={Status} decision={Decision} risk={Risk} tainted={Tainted} duration_ms={DurationMs}",
                row.ToolName, row.Status, row.Decision, row.Risk, row.Tainted, row.DurationMs);

        return Results.NoContent();
    }
}
