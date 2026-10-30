using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary>
/// The gateway side of every broker tool call (external tools spec section 4): gateway-enforced
/// approval, routing to the handler, the response cap, and the content-free audit
/// (<c>broker_calls</c>, metrics, logs). The endpoint has already authenticated the agent, checked
/// the tool is enabled for it and that the turn is the agent's.
/// </summary>
public sealed class ToolBroker(
    SharpClawDbContext db,
    IEnumerable<IBrokerToolHandler> handlers,
    IOptions<ToolBrokerOptions> options,
    TimeProvider time,
    GatewayMetrics metrics,
    ILogger<ToolBroker> logger)
{
    public static class Statuses
    {
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Refused = "refused";
        public const string Error = "error";
    }

    public async Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await RunAsync(call, ct);
        // Measured on the raw JSON, which is what the agent receives (never re-encoded).
        var bytes = result.Result is { } r ? System.Text.Encoding.UTF8.GetByteCount(r.GetRawText()) : 0;
        if (bytes > options.Value.MaxResponseBytes)
        {
            result = BrokerResult.Refused(502, ErrorCodes.ToolFailed,
                "The tool's result was too large to return.", result.TargetHost, result.Target);
            bytes = 0;
        }

        await RecordAsync(call, result, bytes, (int)sw.ElapsedMilliseconds, sw.Elapsed.TotalSeconds);
        return result;
    }

    private async Task<BrokerResult> RunAsync(BrokerInvocation call, CancellationToken ct)
    {
        var handler = handlers.FirstOrDefault(h => h.Tools.Contains(call.Tool));
        if (handler is null)
            return BrokerResult.Unavailable($"{call.Tool} is not available on this gateway.");

        try
        {
            var (approvalHash, refusal) = await handler.PrepareAsync(call, ct);
            if (refusal is not null)
                return refusal;
            if (options.Value.AlwaysApproveSet.Contains(call.Tool) && !await ConsumeApprovalAsync(call, approvalHash!, ct))
                return BrokerResult.Refused(403, ErrorCodes.ApprovalRequired,
                    "This tool needs an approved, unused approval for exactly these arguments.");
            return await handler.InvokeAsync(call, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Exception messages may carry upstream content: only the type is logged.
            logger.LogWarning("broker handler failed: tool={Tool} error={Error}", call.Tool, ex.GetType().Name);
            return BrokerResult.Refused(502, ErrorCodes.ToolFailed, "The tool failed.");
        }
    }

    /// <summary>
    /// Gateway-enforced approval (spec section 4, step 3): the approval must belong to this agent and
    /// turn, be approved for this tool, be recent enough, be unused, and carry the hash of exactly
    /// these arguments (and, for a PR, its change set), which the gateway recomputes. It is marked used by the same conditional
    /// update that checks it, so two calls can never share one approval.
    /// </summary>
    private async Task<bool> ConsumeApprovalAsync(BrokerInvocation call, string hash, CancellationToken ct)
    {
        if (call.ApprovalId is not { } id)
            return false;
        var now = time.GetUtcNow().UtcDateTime;
        var decidedAfter = now.AddSeconds(-options.Value.ApprovalUseSeconds);

        var consumed = await db.Approvals
            .Where(a => a.Id == id && a.AgentId == call.Agent.Id && a.CorrelationId == call.CorrelationId &&
                        a.ToolName == call.Tool && a.Status == ApprovalStatuses.Approved && a.UsedAt == null &&
                        a.ArgsHash == hash && a.DecidedAt >= decidedAfter)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.UsedAt, now), ct);
        return consumed == 1;
    }

    private async Task RecordAsync(BrokerInvocation call, BrokerResult result, long bytes, int durationMs, double seconds)
    {
        var status = result.HttpStatus == 200
            ? (result.Result is { } r && r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                ? Statuses.Succeeded : Statuses.Failed)
            : result.HttpStatus is 403 or 404 ? Statuses.Refused : Statuses.Error;
        var errorCode = result.ErrorCode ?? (status == Statuses.Failed && result.Result is { } fr &&
            fr.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? Cap(e.GetString(), 100) : null);

        // The audit row is written even if the caller has gone away.
        db.BrokerCalls.Add(new BrokerCall
        {
            Id = Guid.CreateVersion7(),
            AgentId = call.Agent.Id,
            ThreadId = call.ThreadId,
            CorrelationId = call.CorrelationId,
            ToolCallId = Cap(call.ToolCallId, 200)!,
            Tool = call.Tool,
            TargetHost = Cap(result.TargetHost, 255),
            Target = Cap(result.Target, 300),
            Status = status,
            ErrorCode = errorCode,
            Bytes = bytes,
            DurationMs = durationMs,
            ApprovalId = call.ApprovalId,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        metrics.RecordBrokerCall(call.Agent.Name, call.Tool, status, seconds);
        using (LogScopes.Turn(logger, call.ThreadId, call.CorrelationId))
            logger.LogInformation(
                "broker call: tool={Tool} status={Status} error={ErrorCode} host={Host} bytes={Bytes} duration_ms={DurationMs}",
                call.Tool, status, errorCode, result.TargetHost, bytes, durationMs);
    }

    private static string? Cap(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
