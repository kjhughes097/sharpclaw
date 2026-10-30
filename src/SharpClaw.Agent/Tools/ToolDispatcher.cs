using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tools;

/// <summary>A tool call the model requested, as received from the chat stream.</summary>
public sealed record ToolCallRequest(string Id, string Name, string ArgumentsJson);

/// <summary>
/// The tool message for the model, plus what the thread history needs to know: whether the call
/// succeeded and whether its content is untrusted (never replayed on later turns).
/// </summary>
public sealed record ToolDispatchResult(string Content, bool Ok, bool Untrusted);

/// <summary>State for one turn (the processing of one user message).</summary>
public sealed class TurnContext(Guid correlationId, Guid threadId)
{
    public Guid CorrelationId { get; } = correlationId;
    public Guid ThreadId { get; } = threadId;

    /// <summary>Set once untrusted content (e.g. a file's text) has entered the turn.</summary>
    public bool Tainted { get; set; }
}

/// <summary>
/// The single path through which tools run (3.2 spec section 7):
/// hardline rules → risk → taint → approval if above the allowed level → execute → report.
/// A hijacked model can only <i>request</i> calls; this class decides. It is the only code allowed
/// to call <see cref="IAgentTool.ExecuteUncheckedAsync"/> (enforced by an architecture test).
/// </summary>
public sealed class ToolDispatcher
{
    /// <summary>How long each approval long poll waits before asking again.</summary>
    private const int ApprovalPollSeconds = 30;

    private const string ClassifierPrompt =
        "You are a security risk classifier for an AI agent's tool calls. The agent runs in an isolated " +
        "sandbox with only its own workspace folder. Rate the risk of the described call. " +
        "Reply with exactly one word: low, medium, high or critical.";

    private readonly ToolRegistry _registry;
    private readonly AgentToolConfig _config;
    private readonly IGatewayClient _gateway;
    private readonly ILogger<ToolDispatcher> _logger;

    public ToolDispatcher(ToolRegistry registry, AgentToolConfig config, IGatewayClient gateway, ILogger<ToolDispatcher> logger)
    {
        _registry = registry;
        _config = config;
        _gateway = gateway;
        _logger = logger;
    }

    /// <summary>Runs (or refuses) one tool call and returns the tool message content for the model.</summary>
    public async Task<ToolDispatchResult> DispatchAsync(ToolCallRequest call, TurnContext turn, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outcome = await DecideAndRunAsync(call, turn, ct);

        _logger.LogInformation(
            "tool call: tool={Tool} status={Status} decision={Decision} risk={Risk} tainted={Tainted} duration_ms={DurationMs}",
            call.Name, outcome.Status, outcome.Decision, outcome.Risk?.ToWire(), outcome.Tainted, sw.ElapsedMilliseconds);

        await ReportAsync(call, turn, outcome, (int)sw.ElapsedMilliseconds);
        var untrusted = outcome.Result.Ok && _registry.TryGet(call.Name, out var tool) && tool.ReturnsUntrustedContent;
        return new ToolDispatchResult(outcome.Result.Json, outcome.Result.Ok, untrusted);
    }

    /// <summary>Everything we learned about a call, for the model, the logs and the audit.</summary>
    private sealed record Outcome(
        ToolResult Result, string Status, string? Decision, RiskLevel? Risk, bool Tainted, Guid? ApprovalId = null);

    private async Task<Outcome> DecideAndRunAsync(ToolCallRequest call, TurnContext turn, CancellationToken ct)
    {
        if (!_registry.TryGet(call.Name, out var tool))
            return new Outcome(ToolResult.Error("unknown_tool", $"No tool named '{call.Name}' is available."),
                ToolCallStatuses.UnknownTool, null, null, turn.Tainted);

        JsonDocument argsDoc;
        try
        {
            argsDoc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
        }
        catch (JsonException)
        {
            return InvalidArguments(turn);
        }

        using (argsDoc)
        {
            var args = argsDoc.RootElement;
            if (args.ValueKind != JsonValueKind.Object)
                return InvalidArguments(turn);

            // 1. The tool's own judgement; hardline rules can't be approved away.
            var assessment = tool.Assess(args);
            if (assessment is ToolAssessment.Blocked blocked)
                return new Outcome(ToolResult.Error("blocked", blocked.Reason),
                    ToolCallStatuses.Blocked, ToolDecisions.Blocked, null, turn.Tainted);

            // 2. Final risk: smart mode for uncertain calls, then the taint bump.
            var summary = tool.Summarize(args);
            var (risk, reason) = assessment switch
            {
                ToolAssessment.Scored s => (s.Level, s.Reason),
                // The human decides these anyway, so the classifier's answer couldn't change anything.
                ToolAssessment.Uncertain u when tool.AlwaysRequiresApproval => (RiskLevel.High, $"{u.Reason} (unscored)"),
                ToolAssessment.Uncertain u => await ClassifyAsync(tool, args, summary, u.Reason, turn, ct),
                _ => (RiskLevel.High, "unscored"),
            };
            var tainted = turn.Tainted;
            if (tainted)
            {
                risk = risk.Raise();
                reason += "; untrusted content was read earlier in this turn";
            }

            // 3. Above the allowed level, or for an always-approve tool, the human decides.
            var decision = ToolDecisions.Allowed;
            Guid? approvalId = null;
            if (risk > _config.AllowedRiskLevel || tool.AlwaysRequiresApproval)
            {
                var (approval, refusal) = await RequestApprovalAsync(call, tool, args, summary, risk, reason, turn, ct);
                approvalId = approval?.Id;
                if (refusal is not null)
                    return refusal with { Risk = risk, Tainted = tainted, ApprovalId = approvalId };
                decision = ToolDecisions.Approved;
            }

            // 4. Execute. Exceptions become an error result; their messages may contain content,
            //    so only the exception type is passed on.
            ToolResult result;
            try
            {
                var context = new ToolCallContext(turn.CorrelationId, turn.ThreadId, call.Id, approvalId);
                result = await tool.ExecuteUncheckedAsync(args, context, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = ToolResult.Error("tool_failed", $"The tool failed ({ex.GetType().Name}).");
            }

            if (result.Ok && tool.ReturnsUntrustedContent)
                turn.Tainted = true;

            return new Outcome(result, result.Ok ? ToolCallStatuses.Succeeded : ToolCallStatuses.Failed,
                decision, risk, tainted, approvalId);
        }
    }

    /// <summary>
    /// Asks the human (through the gateway) and waits for the answer. Returns the refusal outcome
    /// when the call must not run; null refusal means approved. Fails closed on any error.
    /// </summary>
    private async Task<(ApprovalDto? Approval, Outcome? Refusal)> RequestApprovalAsync(
        ToolCallRequest call, IAgentTool tool, JsonElement args, string summary, RiskLevel risk, string reason,
        TurnContext turn, CancellationToken ct)
    {
        ApprovalDto approval;
        try
        {
            // The args hash binds the approval to exactly these arguments; the gateway's broker
            // recomputes it and refuses a call that doesn't match.
            approval = await _gateway.RequestApprovalAsync(new CreateApprovalRequest(
                turn.CorrelationId, turn.ThreadId, call.Id, tool.Name,
                Truncate(summary, 200), risk.ToWire(), Truncate(reason, 200), ToolArgsHash.Compute(args),
                tool.ApprovalNeedsArguments ? args.Clone() : null), ct);

            while (approval.Status == ApprovalStatuses.Pending)
                approval = await _gateway.PollApprovalAsync(approval.Id, ApprovalPollSeconds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApprovalRefusedException refused)
        {
            // The gateway's hardline rules (e.g. a PR touching a blocked path): never offered to the human.
            return (null, Refused(refused.Code, refused.Message, ToolCallStatuses.Blocked, ToolDecisions.Blocked));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("approval request failed: {Error}", ex.GetType().Name);
            return (null, Refused("approval_unavailable", "Approval could not be requested, so the call was not run.",
                ToolCallStatuses.Denied, ToolDecisions.Denied));
        }

        return approval.Status switch
        {
            ApprovalStatuses.Approved => (approval, null),
            ApprovalStatuses.Expired => (approval, Refused("approval_expired",
                "Nobody approved this call in time, so it was not run.", ToolCallStatuses.Expired, ToolDecisions.Expired)),
            _ when approval.DecidedBy == ApprovalDecidedBy.System => (approval, Refused("no_approver",
                "Nobody is available to approve this call, so it was not run.", ToolCallStatuses.Denied, ToolDecisions.Denied)),
            _ => (approval, Refused("denied_by_user",
                "The user denied this call. Do not retry it; continue without it.", ToolCallStatuses.Denied, ToolDecisions.Denied)),
        };
    }

    private static Outcome Refused(string code, string message, string status, string decision) =>
        new(ToolResult.Error(code, message), status, decision, null, false);

    /// <summary>Smart mode: an auxiliary LLM call scores an uncertain call. Otherwise, or on failure, High.</summary>
    private async Task<(RiskLevel, string)> ClassifyAsync(
        IAgentTool tool, JsonElement args, string summary, string reason, TurnContext turn, CancellationToken ct)
    {
        if (!_config.SmartMode)
            return (RiskLevel.High, $"{reason} (unscored)");

        string? reply = null;
        try
        {
            // Only the tool name and either its structured fields or the content-free summary are
            // sent, never file content or other model-supplied free text.
            reply = await _gateway.ClassifyRiskAsync(new[]
            {
                new ChatMessage(Roles.System, ClassifierPrompt),
                new ChatMessage(Roles.User, ClassifierInput(tool, args, summary)),
            }, turn.CorrelationId, turn.ThreadId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("risk classification failed: {Error}", ex.GetType().Name);
        }

        return ParseClassification(reply) is { } level
            ? (level, $"{reason} (classified {level.ToWire()} by smart mode)")
            : (RiskLevel.High, $"{reason} (smart mode gave no usable answer)");
    }

    /// <summary>The classifier's view of a call. Field values are capped and JSON-encoded, so they read as data.</summary>
    internal static string ClassifierInput(IAgentTool tool, JsonElement args, string summary)
    {
        if (tool.ClassifierFields(args) is not { } fields)
            return $"Tool: {tool.Name}\nCall: {summary}";
        var capped = fields.ToDictionary(f => f.Key, f => Truncate(f.Value, 200));
        return $"Tool: {tool.Name}\nFields: {JsonSerializer.Serialize(capped, SharpClawJson.Options)}";
    }

    /// <summary>
    /// The risk level the reply names, or null. A reply naming more than one level ("not low —
    /// critical") is ambiguous and yields null, so the caller fails closed (High) rather than
    /// trusting whichever word came first.
    /// </summary>
    public static RiskLevel? ParseClassification(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        var levels = new HashSet<RiskLevel>();
        foreach (var word in reply.Split(new[] { ' ', '\n', '\r', '\t', '.', ',', ':', ';', '!', '-', '—', '"', '\'' },
                     StringSplitOptions.RemoveEmptyEntries))
            if (RiskLevelExtensions.TryParse(word, out var level))
                levels.Add(level);
        return levels.Count == 1 ? levels.Single() : null;
    }

    private static Outcome InvalidArguments(TurnContext turn) =>
        new(ToolResult.Error("invalid_arguments", "Arguments must be a JSON object."),
            ToolCallStatuses.InvalidArguments, null, null, turn.Tainted);

    /// <summary>Audit report; a failure here is logged and never fails the turn.</summary>
    private async Task ReportAsync(ToolCallRequest call, TurnContext turn, Outcome outcome, int durationMs)
    {
        try
        {
            await _gateway.ReportToolCallAsync(new ToolCallReportRequest(
                turn.CorrelationId, call.Id, outcome.Status, outcome.Decision, outcome.Risk?.ToWire(),
                outcome.Tainted, outcome.ApprovalId, outcome.Result.ErrorCode, durationMs), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("tool call report failed: {Error}", ex.GetType().Name);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
