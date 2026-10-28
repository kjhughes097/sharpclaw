using System.Diagnostics;
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
/// Approval brokering (3.2 spec section 8; pre-3.3 spec section 9). The agent decides <i>whether</i>
/// a call needs approval; the gateway queues the question for the human and relays the answer.
/// Pending approvals wait for a human whether or not anyone is connected.
/// </summary>
public static class ApprovalEndpoints
{
    private const int MaxSummaryChars = 200;
    private const int MaxReasonChars = 200;
    private const int MaxListLimit = 200;

    public static void MapAgent(RouteGroupBuilder agent)
    {
        agent.MapPost("/approvals", Create);
        agent.MapGet("/approvals/{id}", Poll);
    }

    public static void MapClient(RouteGroupBuilder client)
    {
        var approvals = client.MapGroup("/approvals").WithTags("approvals");
        approvals.MapGet("", List).WithName("listApprovals").Produces<ApprovalListResponse>();
        approvals.MapGet("/{id}", Get).WithName("getApproval").Produces<ApprovalDetailResponse>();
        approvals.MapPost("/{id}", Decide).WithName("decideApproval").Produces<ApprovalResponse>();
    }

    private static async Task<IResult> Create(
        CreateApprovalRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IEventPublisher events, IApprovalNotifier notifier, GatewayMetrics metrics, IOptions<ApprovalsOptions> options,
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

        var userMsg = await TurnGuard.FindClaimedAsync(db, agent.Id, body.CorrelationId, body.ThreadId, ct);
        if (userMsg is null)
            return ApiResults.NotFound("Correlation message not found.");

        var now = time.GetUtcNow().UtcDateTime;
        await InboxRepository.TouchClaimAsync(db, agent.Id, body.CorrelationId, now, ct);

        // The poster declared nobody will answer: deny now rather than make the agent wait.
        var unattended = userMsg.Unattended;
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
            Status = unattended ? ApprovalStatuses.Denied : ApprovalStatuses.Pending,
            DecidedBy = unattended ? ApprovalDecidedBy.System : null,
            DecidedAt = unattended ? now : null,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(options.Value.TimeoutSeconds),
        };
        // The approval row and its approval.requested event commit together.
        await events.WriteAsync(db, batch =>
        {
            db.Approvals.Add(approval);
            if (!unattended)
                batch.Add(approval.ThreadId, SseEventNames.ApprovalRequested,
                    new ApprovalRequestedEvent(approval.Id, approval.CorrelationId, approval.ToolName,
                        approval.Summary, approval.Risk, approval.Reason, approval.ExpiresAt, approval.ThreadId));
            return Task.FromResult(true);
        }, ct);

        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Approvals");
        using (LogScopes.Turn(logger, approval.ThreadId, approval.CorrelationId))
            logger.LogInformation("approval requested: tool={Tool} risk={Risk} status={Status}",
                approval.ToolName, approval.Risk, approval.Status);

        if (unattended)
        {
            metrics.RecordApproval(agent.Name, approval.Status);
            metrics.RecordApprovalDecisionTime(agent.Name, approval.Status, 0);
        }
        else
        {
            await NotifyAsync(notifier, approval, agent.Name, logger);
        }

        return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options, statusCode: 201);
    }

    /// <summary>A failing notifier must never affect the request; only the exception type is logged.</summary>
    private static async Task NotifyAsync(IApprovalNotifier notifier, Approval a, string agentName, ILogger logger)
    {
        try
        {
            await notifier.NotifyRequestedAsync(new ApprovalNotification(a.Id, a.ThreadId, a.CorrelationId, agentName,
                a.ToolName, a.Risk, a.Summary, a.ExpiresAt), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("approval notifier failed: {Error}", ex.GetType().Name);
        }
    }

    private static async Task<IResult> Poll(
        Guid id, int? wait, HttpContext http, SharpClawDbContext db, TimeProvider time,
        ApprovalSignal signal, ApprovalDecisions decisions, IOptions<InboxOptions> inboxOptions, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var waitSeconds = Math.Clamp(wait ?? 30, 0, inboxOptions.Value.MaxWaitSeconds);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
            if (approval is null || approval.AgentId != agent.Id)
                return ApiResults.NotFound("Approval not found.");

            var now = time.GetUtcNow().UtcDateTime;
            await ExpireIfDueAsync(db, decisions, approval, now, ct);
            if (approval.Status != ApprovalStatuses.Pending)
            {
                // A decision that landed between our read and the wait may have re-created the key.
                signal.Remove(id);
                return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);
            }

            // Still waiting on the human: keep the message claimed while we do.
            await InboxRepository.TouchClaimAsync(db, agent.Id, approval.CorrelationId, now, ct);

            var remaining = TimeSpan.FromSeconds(waitSeconds) - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);

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

    private static async Task<IResult> List(
        string? status, string? agent, int? limit, SharpClawDbContext db, CancellationToken ct)
    {
        if (status is not null && status is not (ApprovalStatuses.Pending or ApprovalStatuses.Approved
                or ApprovalStatuses.Denied or ApprovalStatuses.Expired))
            return ApiResults.ValidationFailed("status must be pending, approved, denied or expired.");
        var take = Math.Clamp(limit ?? 50, 1, MaxListLimit);

        IQueryable<Approval> q = db.Approvals.AsNoTracking();
        if (status is not null)
            q = q.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(agent))
        {
            var resolved = await AgentQueries.ResolveAsync(db, agent, ct);
            if (resolved is null)
                return ApiResults.NotFound("Agent not found.");
            q = q.Where(a => a.AgentId == resolved.Id);
        }

        var rows = await q.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take).ToListAsync(ct);
        var names = await AgentNamesAsync(db, rows, ct);
        return Results.Json(new ApprovalListResponse(rows.Select(a => ToDetail(a, names)).ToList()), SharpClawJson.Options);
    }

    private static async Task<IResult> Get(Guid id, SharpClawDbContext db, CancellationToken ct)
    {
        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
            return ApiResults.NotFound("Approval not found.");
        var names = await AgentNamesAsync(db, new[] { approval }, ct);
        return Results.Json(new ApprovalDetailResponse(ToDetail(approval, names)), SharpClawJson.Options);
    }

    private static async Task<IResult> Decide(
        Guid id, DecideApprovalRequest? body, SharpClawDbContext db, TimeProvider time,
        ApprovalDecisions decisions, CancellationToken ct)
    {
        if (body?.Decision is not (ApprovalStatuses.Approved or ApprovalStatuses.Denied))
            return ApiResults.ValidationFailed("decision must be 'approved' or 'denied'.");

        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
            return ApiResults.NotFound("Approval not found.");

        await ExpireIfDueAsync(db, decisions, approval, time.GetUtcNow().UtcDateTime, ct);
        if (approval.Status != ApprovalStatuses.Pending ||
            !await decisions.TryDecideAsync(db, approval, body.Decision, ApprovalDecidedBy.User, ct))
        {
            var current = await db.Approvals.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).FirstAsync(ct);
            return ApiResults.Conflict($"Approval is already {current}.");
        }

        return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);
    }

    /// <summary>Backup for the sweeper: an overdue approval is expired when it is read.</summary>
    private static async Task ExpireIfDueAsync(
        SharpClawDbContext db, ApprovalDecisions decisions, Approval approval, DateTime now, CancellationToken ct)
    {
        if (approval.Status != ApprovalStatuses.Pending || now < approval.ExpiresAt)
            return;
        if (!await decisions.TryDecideAsync(db, approval, ApprovalStatuses.Expired, ApprovalDecidedBy.System, ct))
        {
            var fresh = await db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id, ct);
            (approval.Status, approval.DecidedBy, approval.DecidedAt) = (fresh.Status, fresh.DecidedBy, fresh.DecidedAt);
        }
    }

    private static Task<Dictionary<Guid, string>> AgentNamesAsync(
        SharpClawDbContext db, IEnumerable<Approval> approvals, CancellationToken ct)
    {
        var ids = approvals.Select(a => a.AgentId).Distinct().ToList();
        return db.Agents.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    private static ApprovalDto ToDto(Approval a) => new(a.Id, a.Status, a.DecidedBy, a.ExpiresAt);

    private static ApprovalDetailDto ToDetail(Approval a, IReadOnlyDictionary<Guid, string> names) => new(
        a.Id, names.GetValueOrDefault(a.AgentId, ""), a.ThreadId, a.CorrelationId, a.ToolName, a.Summary, a.Risk,
        a.Reason, a.Status, a.CreatedAt, a.ExpiresAt, a.DecidedBy, a.DecidedAt);
}
