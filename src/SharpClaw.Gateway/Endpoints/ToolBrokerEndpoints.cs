using System.Text.Json;
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
    private static IResult Policy(HttpContext http, IOptions<ToolBrokerOptions> options)
    {
        var agent = AuthContext.Agent(http);
        var o = options.Value;
        var tools = o.ForAgent(agent.Name)?.Tools.Where(BrokerTools.All.Contains).Distinct().ToList() ?? [];
        return Results.Json(new AgentToolPolicyResponse(
            tools, o.AlwaysApproveSet.Where(tools.Contains).Order().ToList(), o.FetchAllowHosts, o.CloneHosts), SharpClawJson.Options);
    }

    private static async Task<IResult> Invoke(
        string tool, InvokeToolRequest? body, HttpContext http, SharpClawDbContext db,
        IOptions<ToolBrokerOptions> options, ToolBroker broker, CancellationToken ct)
    {
        var agent = AuthContext.Agent(http);

        // The operator's gate: a tool not enabled for this agent in gateway config doesn't exist for it.
        var config = options.Value.ForAgent(agent.Name);
        if (config is null || !BrokerTools.All.Contains(tool) || !config.Tools.Contains(tool))
            return ApiResults.NotFound("Tool not found.");

        if (body is null || string.IsNullOrWhiteSpace(body.ToolCallId) || body.ToolCallId.Length > MaxToolCallIdChars)
            return ApiResults.ValidationFailed($"toolCallId is required and must be at most {MaxToolCallIdChars} characters.");
        if (body.Arguments.ValueKind != JsonValueKind.Object)
            return ApiResults.ValidationFailed("arguments must be a JSON object.");

        // Broker tools run only inside a turn the agent currently holds.
        if (await TurnGuard.FindClaimedAsync(db, agent.Id, body.CorrelationId, body.ThreadId, ct) is null)
            return ApiResults.NotFound("Correlation message not found.");

        var result = await broker.InvokeAsync(new BrokerInvocation(
            agent, config, tool, body.Arguments, body.CorrelationId, body.ThreadId, body.ToolCallId, body.ApprovalId), ct);

        // The result goes back exactly as produced (re-serializing could escape it to several times its size).
        return result.HttpStatus == 200
            ? Results.Text(result.Result!.Value.GetRawText(), "application/json")
            : ApiResults.Error(result.HttpStatus, result.ErrorCode!, result.ErrorMessage!);
    }
}
