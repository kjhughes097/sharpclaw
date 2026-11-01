using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Services.Broker;
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
    private const int MaxBatchIds = 100;

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
        approvals.MapPost("/batch", DecideBatch).WithName("decideApprovals").Produces<BatchDecideApprovalsResponse>();
    }

    private static async Task<IResult> Create(
        CreateApprovalRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        IEventPublisher events, IApprovalNotifier notifier, GatewayMetrics metrics, IOptions<ApprovalsOptions> options,
        ApprovalDetails details, ILoggerFactory loggerFactory, CancellationToken ct)
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
        if (body.ArgsHash is not null && !ToolArgsHash.IsValid(body.ArgsHash))
            return ApiResults.ValidationFailed("argsHash must be a lowercase hex SHA-256.");

        var userMsg = await TurnGuard.FindClaimedAsync(db, agent.Id, body.CorrelationId, body.ThreadId, ct);
        if (userMsg is null)
            return ApiResults.NotFound("Correlation message not found.");

        // For broker calls: the gateway's own hash of the arguments, and the detail for the human.
        var prepared = await details.PrepareAsync(agent, body.ToolName, body.ArgsHash, body.Arguments, ct);
        if (prepared.Error is not null)
            return prepared.ErrorCode is { } code
                ? ApiResults.Error(prepared.ErrorStatus ?? 400, code, prepared.Error)
                : ApiResults.ValidationFailed(prepared.Error);

        var now = time.GetUtcNow().UtcDateTime;
        await InboxRepository.TouchClaimAsync(db, agent.Id, body.CorrelationId, now, ct);

        // Autonomy or a standing grant approves at once (3.6 spec section 6); otherwise, when the
        // poster declared nobody will answer, deny now rather than make the agent wait.
        var auto = await ApprovalPolicy.DecideAsync(db, agent.Id, body.ToolName, body.ThreadId, now, ct);
        var unattended = auto is null && userMsg.Unattended;
        var decidedNow = auto is not null || unattended;
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
            ArgsHash = prepared.ArgsHash,
            Detail = prepared.Detail,
            Status = auto is not null ? ApprovalStatuses.Approved : unattended ? ApprovalStatuses.Denied : ApprovalStatuses.Pending,
            DecidedBy = auto?.DecidedBy ?? (unattended ? ApprovalDecidedBy.System : null),
            DecidedAt = decidedNow ? now : null,
            GrantId = auto?.GrantId,
            CreatedAt = now,
            ExpiresAt = now.AddSeconds(options.Value.TimeoutSeconds),
        };
        // The approval row and its event commit together. An automatic approval publishes only
        // approval.decided, so no card flashes up for the human.
        await events.WriteAsync(db, batch =>
        {
            db.Approvals.Add(approval);
            if (auto is not null)
                batch.Add(approval.ThreadId, SseEventNames.ApprovalDecided,
                    new ApprovalDecidedEvent(approval.Id, approval.CorrelationId, approval.Status, approval.DecidedBy!, approval.ThreadId));
            else if (!unattended)
                batch.Add(approval.ThreadId, SseEventNames.ApprovalRequested,
                    new ApprovalRequestedEvent(approval.Id, approval.CorrelationId, approval.ToolName,
                        approval.Summary, approval.Risk, approval.Reason, approval.ExpiresAt, approval.ThreadId));
            return Task.FromResult(true);
        }, ct);

        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Approvals");
        using (LogScopes.Turn(logger, approval.ThreadId, approval.CorrelationId))
            logger.LogInformation("approval requested: tool={Tool} risk={Risk} status={Status} decided_by={DecidedBy}",
                approval.ToolName, approval.Risk, approval.Status, approval.DecidedBy);

        if (decidedNow)
        {
            metrics.RecordApproval(agent.Name, approval.Status, approval.DecidedBy!);
            metrics.RecordApprovalDecisionTime(agent.Name, approval.Status, approval.DecidedBy!, 0);
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
        return Results.Json(new ApprovalDetailResponse(ToDetail(approval, names, includeDetail: true)), SharpClawJson.Options);
    }

    private static async Task<IResult> Decide(
        Guid id, DecideApprovalRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        ApprovalDecisions decisions, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (body?.Decision is not (ApprovalStatuses.Approved or ApprovalStatuses.Denied))
            return ApiResults.ValidationFailed("decision must be 'approved' or 'denied'.");
        if (body.Remember is not null && body.Decision != ApprovalStatuses.Approved)
            return ApiResults.ValidationFailed("remember is only valid with 'approved'.");

        var approval = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null)
            return ApiResults.NotFound("Approval not found.");
        if (body.Remember is not null && ApprovalGrantEndpoints.Validate(body.Remember, approval.ToolName) is { } rememberError)
            return ApiResults.ValidationFailed(rememberError);

        var now = time.GetUtcNow().UtcDateTime;
        var actor = ClientAuthFilter.ActorName(http);
        await ExpireIfDueAsync(db, decisions, approval, now, ct);
        if (approval.Status != ApprovalStatuses.Pending ||
            !await decisions.TryDecideAsync(db, approval, body.Decision, ApprovalDecidedBy.User, ct, actor))
        {
            var current = await db.Approvals.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).FirstAsync(ct);
            return ApiResults.Conflict($"Approval is already {current}.");
        }

        // Only a decision that won creates the grant, so a 409 never leaves one behind.
        if (body.Remember is not null)
        {
            db.ApprovalGrants.Add(ApprovalGrantEndpoints.NewGrant(approval, body.Remember, actor, now));
            await db.SaveChangesAsync(ct);
            loggerFactory.CreateLogger("SharpClaw.Gateway.Approvals").LogInformation(
                "approval grant created: tool={Tool} scope={Scope} ttl_hours={TtlHours} by={Actor}",
                approval.ToolName, body.Remember.Scope, body.Remember.TtlHours, actor);
        }

        return Results.Json(new ApprovalResponse(ToDto(approval)), SharpClawJson.Options);
    }

    /// <summary>Decides every pending approval in a list of ids or a thread (3.6 spec section 7).</summary>
    private static async Task<IResult> DecideBatch(
        BatchDecideApprovalsRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        ApprovalDecisions decisions, CancellationToken ct)
    {
        if (body?.Decision is not (ApprovalStatuses.Approved or ApprovalStatuses.Denied))
            return ApiResults.ValidationFailed("decision must be 'approved' or 'denied'.");
        if ((body.Ids is null) == (body.ThreadId is null))
            return ApiResults.ValidationFailed("Give exactly one of ids or threadId.");
        if (body.Ids is { } given && given.Count is < 1 or > MaxBatchIds)
            return ApiResults.ValidationFailed($"ids must hold 1-{MaxBatchIds} approval ids.");

        List<Approval> candidates;
        var requested = 0;
        if (body.Ids is { } ids)
        {
            var distinct = ids.Distinct().ToList();
            requested = distinct.Count;
            candidates = await db.Approvals.AsNoTracking().Where(a => distinct.Contains(a.Id)).ToListAsync(ct);
        }
        else
        {
            if (!await db.Threads.AsNoTracking().AnyAsync(t => t.Id == body.ThreadId, ct))
                return ApiResults.NotFound("Thread not found.");
            candidates = await db.Approvals.AsNoTracking()
                .Where(a => a.ThreadId == body.ThreadId && a.Status == ApprovalStatuses.Pending).ToListAsync(ct);
            requested = candidates.Count;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var actor = ClientAuthFilter.ActorName(http);
        var decided = new List<ApprovalDto>();
        foreach (var approval in candidates.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id))
        {
            await ExpireIfDueAsync(db, decisions, approval, now, ct);
            if (approval.Status == ApprovalStatuses.Pending &&
                await decisions.TryDecideAsync(db, approval, body.Decision, ApprovalDecidedBy.User, ct, actor))
                decided.Add(ToDto(approval));
        }

        return Results.Json(new BatchDecideApprovalsResponse(decided, requested - decided.Count), SharpClawJson.Options);
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
            (approval.Status, approval.DecidedBy, approval.DecidedByUser, approval.DecidedAt) =
                (fresh.Status, fresh.DecidedBy, fresh.DecidedByUser, fresh.DecidedAt);
        }
    }

    private static Task<Dictionary<Guid, string>> AgentNamesAsync(
        SharpClawDbContext db, IEnumerable<Approval> approvals, CancellationToken ct)
    {
        var ids = approvals.Select(a => a.AgentId).Distinct().ToList();
        return db.Agents.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    private static ApprovalDto ToDto(Approval a) => new(a.Id, a.Status, a.DecidedBy, a.ExpiresAt, a.GrantId);

    /// <summary>The detail (a command, a file list) is only for the single-approval view, never lists.</summary>
    private static ApprovalDetailDto ToDetail(Approval a, IReadOnlyDictionary<Guid, string> names, bool includeDetail = false) => new(
        a.Id, names.GetValueOrDefault(a.AgentId, ""), a.ThreadId, a.CorrelationId, a.ToolName, a.Summary, a.Risk,
        a.Reason, a.Status, a.CreatedAt, a.ExpiresAt, a.DecidedBy, a.DecidedAt, a.DecidedByUser,
        includeDetail ? a.Detail : null, a.GrantId);
}
