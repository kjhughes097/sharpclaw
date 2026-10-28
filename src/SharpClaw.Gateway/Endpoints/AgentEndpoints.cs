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

public static class AgentEndpoints
{
    public static void Map(RouteGroupBuilder agent)
    {
        agent.MapGet("/inbox", Inbox);
        agent.MapGet("/threads/{id}/messages", ThreadHistory);
        agent.MapGet("/threads/{id}/context", Context);
        agent.MapPost("/threads/{id}/messages", Reply);
        agent.MapPost("/messages/{id}/fail", Fail);
    }

    private static async Task<IResult> Inbox(
        HttpContext http, int? wait, SharpClawDbContext db, TimeProvider time,
        IAgentInboxSignal signal, MessageTurns turns, IOptions<InboxOptions> inboxOpt,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Inbox");
        var agent = AuthContext.Agent(http);
        var opt = inboxOpt.Value;
        var waitSeconds = Math.Clamp(wait ?? 30, 0, opt.MaxWaitSeconds);

        var sw = Stopwatch.StartNew();
        while (true)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var threshold = now.AddSeconds(-opt.ClaimTimeoutSeconds);
            var requeued = await InboxRepository.RequeueStaleAsync(db, agent.Id, threshold, ct);
            foreach (var stale in requeued)
                await turns.ReleaseAsync(stale, ct);

            var claimed = await InboxRepository.ClaimNextAsync(db, agent.Id, now, ct);
            if (claimed is not null)
            {
                using (LogScopes.Turn(logger, claimed.ThreadId, claimed.Id))
                    logger.LogInformation("message claimed");
                return Results.Json(new InboxMessageResponse(claimed), SharpClawJson.Options);
            }

            var remaining = TimeSpan.FromSeconds(waitSeconds) - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return Results.StatusCode(204);

            var slice = remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2);
            try
            {
                await signal.WaitAsync(agent.Id, slice, ct);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(204);
            }
        }
    }

    private static async Task<IResult> ThreadHistory(
        Guid id, HttpContext http, SharpClawDbContext db, CancellationToken ct)
    {
        if (!await TurnGuard.OwnsThreadAsync(db, AuthContext.Agent(http).Id, id, ct))
            return ApiResults.NotFound("Thread not found.");

        var messages = await MessageQueries.ListAsync(db, id, includeTools: true, ct);
        return Results.Json(new MessageListResponse(messages), SharpClawJson.Options);
    }

    /// <summary>The model-ready history that fits the agent's context window (pre-3.3 spec section 6.4).</summary>
    private static async Task<IResult> Context(
        Guid id, int? reserveTokens, HttpContext http, SharpClawDbContext db, IContextSummariser summariser,
        IOptions<ContextOptions> contextOptions, GatewayMetrics metrics, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var reserve = reserveTokens ?? 0;
        if (reserve < 0)
            return ApiResults.ValidationFailed("reserveTokens must be >= 0.");
        if (!await TurnGuard.OwnsThreadAsync(db, agent.Id, id, ct))
            return ApiResults.NotFound("Thread not found.");

        // The turn being processed: the agent's claimed message, else the latest one.
        var users = db.Messages.AsNoTracking().Where(m => m.ThreadId == id && m.Role == Roles.User && m.Kind == MessageKinds.Message);
        var current = await users.Where(m => m.Status == MessageStatus.Claimed).OrderByDescending(m => m.Seq).FirstOrDefaultAsync(ct)
            ?? await users.OrderByDescending(m => m.Seq).FirstOrDefaultAsync(ct);
        var options = contextOptions.Value;
        var window = options.WindowFor(agent.ContextWindowTokens);
        if (current is null)
            return Results.Json(new ContextResponse(Array.Empty<ContextMessage>(), false, 0, 0, window), SharpClawJson.Options);

        var result = await new ContextBuilder(options, summariser).BuildForThreadAsync(db, id, current, window, reserve, ct);
        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Context");
        using var scope = LogScopes.Turn(logger, id, current.Id);
        if (result.TooLarge)
        {
            metrics.RecordContextRejection(agent.Name);
            logger.LogWarning("context rejected: estimated_tokens={EstimatedTokens} window_tokens={WindowTokens} reserve_tokens={ReserveTokens}",
                result.EstimatedTokens, window, reserve);
            return ApiResults.Error(400, ErrorCodes.ContextTooLarge,
                "The current message does not fit in the agent's context window.");
        }

        await db.Threads.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.ContextDroppedTurns, result.DroppedTurns)
            .SetProperty(t => t.ContextEstimatedTokens, result.EstimatedTokens), ct);
        metrics.RecordContextEstimate(agent.Name, result.EstimatedTokens);
        if (result.DroppedTurns > 0)
        {
            metrics.RecordContextTruncation(agent.Name);
            logger.LogInformation("context truncated: dropped_turns={DroppedTurns} estimated_tokens={EstimatedTokens} window_tokens={WindowTokens}",
                result.DroppedTurns, result.EstimatedTokens, window);
        }

        return Results.Json(new ContextResponse(result.Messages, result.DroppedTurns > 0, result.DroppedTurns,
            result.EstimatedTokens, window), SharpClawJson.Options);
    }

    private static async Task<IResult> Reply(
        Guid id, AgentReplyRequest? body, HttpContext http, MessageTurns turns,
        IOptions<MessagesOptions> messages, IOptions<ToolsOptions> toolsOptions, CancellationToken ct)
    {
        if (body is null || string.IsNullOrEmpty(body.Content))
            return ApiResults.ValidationFailed("content is required.");
        if (body.Content.Length > messages.Value.MaxContentChars)
            return ApiResults.ValidationFailed($"content exceeds {messages.Value.MaxContentChars} characters.");
        var exchanges = body.Exchanges ?? Array.Empty<ToolExchangeDto>();
        if (ToolExchangeRows.Validate(exchanges, toolsOptions.Value) is { } invalid)
            return ApiResults.ValidationFailed(invalid);

        var (outcome, reply) = await turns.CompleteAsync(
            AuthContext.Agent(http), id, body.CorrelationId, body.Content, exchanges, ct);
        return outcome switch
        {
            TurnOutcome.Ok => Results.Json(new AgentReplyResponse(new AgentReplyDto(reply!.Id, reply.CreatedAt)),
                SharpClawJson.Options, statusCode: 201),
            TurnOutcome.AlreadyDone => ApiResults.Conflict("A reply already exists for this message."),
            TurnOutcome.NotClaimed => ApiResults.NotFound("Correlation message is not claimed."),
            _ => ApiResults.NotFound("Correlation message not found."),
        };
    }

    private static async Task<IResult> Fail(
        Guid id, FailMessageRequest? body, HttpContext http, MessageTurns turns, CancellationToken ct)
    {
        var reason = body?.Reason;
        if (string.IsNullOrWhiteSpace(reason))
            return ApiResults.ValidationFailed("reason is required.");
        if (reason.Length > 100)
            return ApiResults.ValidationFailed("reason must be at most 100 characters.");

        return await turns.FailAsync(AuthContext.Agent(http), id, reason, ct)
            ? Results.StatusCode(204)
            : ApiResults.NotFound("Message not found.");
    }
}
