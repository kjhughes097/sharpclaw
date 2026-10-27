using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// Approval brokering (3.2 spec sections 8.2 and 8.3). The agent decides <i>whether</i> a call
/// needs approval; the gateway only relays the question to the human and the answer back.
/// </summary>
public static class ApprovalEndpoints
{
    private const int MaxSummaryChars = 200;
    private const int MaxReasonChars = 200;

    public static void MapAgent(RouteGroupBuilder agent)
    {
        agent.MapPost("/approvals", Create);
        agent.MapGet("/approvals/{id}", Poll);
    }

    public static void MapClient(RouteGroupBuilder client)
    {
        client.MapPost("/approvals/{id}", Decide);
    }

    private static async Task<IResult> Create(
        CreateApprovalRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IThreadEventBus bus, IEventPublisher events, GatewayMetrics metrics, IOptions<ApprovalsOptions> options,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        if (body is null || string.IsNullOrWhiteSpace(body.ToolName) || string.IsNullOrWhiteSpace(body.ToolCallId))
            return ApiResults.ValidationFailed("toolName and toolCallId are required.");
        if (string.IsNullOrWhiteSpace(body.Summary) || body.Summary.Length > MaxSummaryChars)
            return ApiResults.ValidationFailed($"summary is required and must be at most {MaxSummaryChars} characters.");
        if (!RiskLevels.All.Contains(body.Risk))
            return ApiResults.ValidationFailed($"risk must be one of: {string.Join(", ", RiskLevels.All)}.");
        if (body.Reason is null || body.Reason.Length > MaxReasonChars)
            return ApiResults.ValidationFailed($"reason is required and must be at most {MaxReasonChars} characters.");

        var userMsg = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == body.CorrelationId, ct);
        if (userMsg is null || userMsg.AgentId != agent.Id || userMsg.ThreadId != body.ThreadId ||
            userMsg.Role != Roles.User || userMsg.Status != MessageStatus.Claimed)
            return ApiResults.NotFound("Correlation message not found.");

        var now = time.GetUtcNow().UtcDateTime;
        await InboxRepository.TouchClaimAsync(db, agent.Id, body.CorrelationId, now, ct);

        // Nobody is watching the thread, so nobody can answer: deny now rather than make the
        // agent wait for the timeout (Hermes' "unattended -> deny").
        var nobodyWatching = bus.SubscriberCount(body.ThreadId) == 0;

        var approval = new Approval
        {
            Id = Guid.CreateVersion7(),
            AgentId = agent.Id,
            ThreadId = body.ThreadId,
            CorrelationId = body.CorrelationId,
            ToolCallId = body.ToolCallId,
            ToolName = body.ToolName,
            Summary = body.Summary,
            Risk = body.Risk,
            Reason = body.Reason,
            Status = nobodyWatching ? ApprovalStatuses.Denied : ApprovalStatuses.Pending,
            DecidedBy = nobodyWatching ? ApprovalDecidedBy.System : null,
            DecidedAt = nobodyWatching ? now : null,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(options.Value.TimeoutSeconds),
        };
        db.Approvals.Add(approval);
        await db.SaveChangesAsync(ct);

        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Approvals");
        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["thread_id"] = approval.ThreadId.ToString(),
            ["correlation_id"] = approval.CorrelationId.ToString(),
        }))
            logger.LogInformation("approval requested: tool={Tool} risk={Risk} status={Status}",
                approval.ToolName, approval.Risk, approval.Status);

        if (nobodyWatching)
        {
            metrics.RecordApproval(agent.Name, approval.Status);
        }
        else
        {
            var evt = new ApprovalRequestedEvent(approval.Id, approval.CorrelationId, approval.ToolName,
                approval.Summary, approval.Risk, approval.Reason, approval.ExpiresAt);
            await events.PublishDurableAsync(approval.ThreadId, SseEventNames.ApprovalRequested, evt, ct);
        }

        return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> Poll(
        Guid id, int? wait, HttpContext http, SharpClawDbContext db, TimeProvider time,
        ApprovalSignal signal, GatewayMetrics metrics, IOptions<InboxOptions> inboxOptions, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var waitSeconds = Math.Clamp(wait ?? 30, 0, inboxOptions.Value.MaxWaitSeconds);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (approval is null || approval.AgentId != agent.Id)
                return ApiResults.NotFound("Approval not found.");

            var now = time.GetUtcNow().UtcDateTime;
            await ExpireIfDueAsync(db, approval, now, metrics, agent.Name, ct);
            if (approval.Status != ApprovalStatuses.Pending)
                return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);

            // Still waiting on the human: keep the message claimed while we do.
            await InboxRepository.TouchClaimAsync(db, agent.Id, approval.CorrelationId, now, ct);

            var remaining = TimeSpan.FromSeconds(waitSeconds) - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);

            db.ChangeTracker.Clear();
            try
            {
                await signal.WaitAsync(id, remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                return Results.Empty;
            }
        }
    }

    private static async Task<IResult> Decide(
        Guid id, DecideApprovalRequest? body, SharpClawDbContext db, TimeProvider time,
        ApprovalSignal signal, IEventPublisher events, GatewayMetrics metrics, CancellationToken ct)
    {
        if (body?.Decision is not (ApprovalStatuses.Approved or ApprovalStatuses.Denied))
            return ApiResults.ValidationFailed("decision must be 'approved' or 'denied'.");

        var approval = await db.Approvals.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
            return ApiResults.NotFound("Approval not found.");

        var agentName = await db.Agents.Where(a => a.Id == approval.AgentId).Select(a => a.Name).FirstAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        await ExpireIfDueAsync(db, approval, now, metrics, agentName, ct);
        if (approval.Status != ApprovalStatuses.Pending)
            return ApiResults.Conflict($"Approval is already {approval.Status}.");

        approval.Status = body.Decision;
        approval.DecidedBy = ApprovalDecidedBy.User;
        approval.DecidedAt = now;
        await db.SaveChangesAsync(ct);

        metrics.RecordApproval(agentName, approval.Status);
        signal.Signal(approval.Id);
        await events.PublishDurableAsync(approval.ThreadId, SseEventNames.ApprovalDecided,
            new ApprovalDecidedEvent(approval.Id, approval.CorrelationId, approval.Status, approval.DecidedBy), ct);
        return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);
    }

    /// <summary>Pending approvals past their deadline become <c>expired</c> when next read.</summary>
    private static async Task ExpireIfDueAsync(
        SharpClawDbContext db, Approval approval, DateTime now, GatewayMetrics metrics, string agentName, CancellationToken ct)
    {
        if (approval.Status != ApprovalStatuses.Pending || now < approval.ExpiresAt)
            return;
        approval.Status = ApprovalStatuses.Expired;
        approval.DecidedBy = ApprovalDecidedBy.System;
        approval.DecidedAt = now;
        await db.SaveChangesAsync(ct);
        metrics.RecordApproval(agentName, approval.Status);
    }

    private static ApprovalDto ToDto(Approval a) => new(a.Id, a.Status, a.DecidedBy, a.ExpiresAt);
}
