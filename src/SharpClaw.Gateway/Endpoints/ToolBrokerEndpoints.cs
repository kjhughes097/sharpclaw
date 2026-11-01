using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;
using SharpClaw.Gateway.Services.Broker;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// <c>POST /agent/tools/{tool}</c>: the agent asks the gateway to run a broker tool (external tools
/// spec section 4). A thin adapter: the operator's per-agent gate and the turn check live here,
/// approvals, routing and the audit in <see cref="ToolBroker"/>.
/// </summary>
public static class ToolBrokerEndpoints
{
    private const int MaxToolCallIdChars = 200;

    public static void Map(RouteGroupBuilder agent)
    {
        agent.MapGet("/tools", Policy);
        agent.MapPost("/tools/{tool}", Invoke);
    }

    /// <summary>The operator's broker settings for the calling agent, read once at agent startup.</summary>
    private static async Task<IResult> Policy(
        HttpContext http, IOptions<ToolBrokerOptions> options, IOptions<AgentMessagingOptions> messaging,
        SharpClawDbContext db, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);
        var o = options.Value;
        var tools = o.ForAgent(agent.Name)?.EnabledTools ?? [];
        return Results.Json(new AgentToolPolicyResponse(
            tools, o.AlwaysApproveSet.Where(tools.Contains).Order().ToList(), o.FetchAllowHosts, o.CloneHosts,
            await AskTargetsAsync(agent.Name, tools, messaging.Value, db, ct)), SharpClawJson.Options);
    }

    /// <summary>
    /// The agents <c>ask_agent</c> may ask (3.5 spec section 5.7): every other agent by default, or the
    /// sender's own list in config order. Disabled ones are included; the ask itself reports them.
    /// </summary>
    private static async Task<IReadOnlyList<AskAgentTargetDto>> AskTargetsAsync(
        string sender, IReadOnlyList<string> tools, AgentMessagingOptions messaging, SharpClawDbContext db, CancellationToken ct)
    {
        if (!tools.Contains(BrokerTools.AskAgent))
            return [];
        var names = await db.Agents.AsNoTracking().Select(a => a.Name).ToListAsync(ct);
        return messaging.RecipientsOf(sender, names)
            .Select(n => new AskAgentTargetDto(n, messaging.DescriptionOf(n)))
            .ToList();
    }

    private static async Task<IResult> Invoke(
        string tool, InvokeToolRequest? body, HttpContext http, SharpClawDbContext db,
        IOptions<ToolBrokerOptions> options, ToolBroker broker, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);

        // The operator's gate: a tool not enabled for this agent in gateway config doesn't exist for it.
        var config = options.Value.ForAgent(agent.Name);
        if (config is null || !config.Allows(tool))
            return ApiResults.NotFound("Tool not found.");

        if (body is null || string.IsNullOrWhiteSpace(body.ToolCallId) || body.ToolCallId.Length > MaxToolCallIdChars)
            return ApiResults.ValidationFailed($"toolCallId is required and must be at most {MaxToolCallIdChars} characters.");
        if (body.Arguments.ValueKind != JsonValueKind.Object)
            return ApiResults.ValidationFailed("arguments must be a JSON object.");

        // Broker tools run only inside a turn the agent currently holds.
        if (await TurnGuard.FindClaimedAsync(db, agent.Id, body.CorrelationId, body.ThreadId, ct) is null)
            return ApiResults.NotFound("Correlation message not found.");

        var result = await broker.InvokeAsync(new BrokerInvocation(
            agent, config, tool, body.Arguments, body.CorrelationId, body.ThreadId, body.ToolCallId, body.ApprovalId,
            body.Tainted), ct);

        // The result goes back exactly as produced (re-serializing could escape it to several times its size).
        return result.HttpStatus == 200
            ? Results.Text(result.Result!.Value.GetRawText(), "application/json")
            : ApiResults.Error(result.HttpStatus, result.ErrorCode!, result.ErrorMessage!);
    }
}
