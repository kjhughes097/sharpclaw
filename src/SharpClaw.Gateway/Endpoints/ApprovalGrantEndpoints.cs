using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// Standing approvals (3.6 spec section 7). Grants are created only by a human approving a call with
/// <c>remember</c>; here they are listed and revoked. Client listener only: agents never see them.
/// </summary>
public static class ApprovalGrantEndpoints
{
    public static void Map(RouteGroupBuilder client)
    {
        var grants = client.MapGroup("/agents/{idOrName}/grants").WithTags("approvals");
        grants.MapGet("", List).WithName("listApprovalGrants").Produces<ApprovalGrantListResponse>();
        grants.MapDelete("/{grantId}", Revoke).WithName("revokeApprovalGrant").Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>Null when <paramref name="remember"/> may be granted for <paramref name="toolName"/>; otherwise why not.</summary>
    public static string? Validate(ApprovalRememberRequest remember, string toolName)
    {
        if (remember.Scope is not (ApprovalGrantScopes.Thread or ApprovalGrantScopes.Agent))
            return "remember.scope must be 'thread' or 'agent'.";
        if (remember.TtlHours is { } ttl && !ApprovalGrantScopes.TtlHours.Contains(ttl))
            return $"remember.ttlHours must be one of {string.Join(", ", ApprovalGrantScopes.TtlHours)}.";
        if (BrokerTools.NonGrantable.Contains(toolName))
            return $"{toolName} acts as the user and can't be approved in advance; only autonomy mode covers it.";
        return null;
    }

    /// <summary>The grant a remembered approval creates (already validated). Caller saves it.</summary>
    public static ApprovalGrant NewGrant(Approval approval, ApprovalRememberRequest remember, string actor, DateTime now) => new()
    {
        Id = Guid.CreateVersion7(),
        AgentId = approval.AgentId,
        ToolName = approval.ToolName,
        ThreadId = remember.Scope == ApprovalGrantScopes.Thread ? approval.ThreadId : null,
        ExpiresAt = remember.TtlHours is { } hours ? now.AddHours(hours) : null,
        CreatedByUser = actor,
        CreatedAt = now,
    };

    private static async Task<IResult> List(string idOrName, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var rows = await ApprovalPolicy.ActiveGrants(db, agent.Id, time.GetUtcNow().UtcDateTime)
            .OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id)
            .ToListAsync(ct);
        return Results.Json(new ApprovalGrantListResponse(rows.Select(ToDto).ToList()), SharpClawJson.Options);
    }

    private static async Task<IResult> Revoke(
        string idOrName, Guid grantId, HttpContext http, SharpClawDbContext db, TimeProvider time,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var now = time.GetUtcNow().UtcDateTime;
        var revoked = await db.ApprovalGrants
            .Where(g => g.Id == grantId && g.AgentId == agent.Id && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RevokedAt, now), ct);
        if (revoked == 0)
            return ApiResults.NotFound("Grant not found.");

        loggerFactory.CreateLogger("SharpClaw.Gateway.Approvals").LogInformation(
            "approval grant revoked: agent={Agent} by={Actor}", agent.Name, ClientAuthFilter.ActorName(http));
        return Results.NoContent();
    }

    private static ApprovalGrantDto ToDto(ApprovalGrant g) => new(
        g.Id, g.ToolName, g.ThreadId is null ? ApprovalGrantScopes.Agent : ApprovalGrantScopes.Thread,
        g.ThreadId, g.ExpiresAt, g.CreatedByUser, g.CreatedAt);
}
