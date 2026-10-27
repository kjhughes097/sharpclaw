using Microsoft.EntityFrameworkCore;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Auth;

/// <summary>Validates an agent token on the agent listener and sets the authenticated agent.</summary>
public sealed class AgentAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var sp = http.RequestServices;
        var db = sp.GetRequiredService<SharpClawDbContext>();
        var metrics = sp.GetRequiredService<GatewayMetrics>();
        var time = sp.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow().UtcDateTime;

        var presented = BearerToken.Read(http.Request);
        if (presented is null)
        {
            metrics.RecordAuthFailure("agent", "agent_token");
            return ApiResults.Unauthorized();
        }

        var hash = Hashing.Sha256Hex(presented);
        var token = await db.AgentTokens.Include(t => t.Agent)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, http.RequestAborted);

        if (token is null || token.RevokedAt is not null ||
            (token.ExpiresAt is not null && token.ExpiresAt <= now) || token.Agent is null)
        {
            metrics.RecordAuthFailure("agent", "agent_token");
            return ApiResults.Unauthorized();
        }

        if (!token.Agent.Enabled)
            return ApiResults.AgentDisabled();

        // Update last_used_at at most once per minute per token.
        if (token.LastUsedAt is null || now - token.LastUsedAt.Value >= TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(http.RequestAborted);
        }

        var agent = token.Agent;
        metrics.MarkSeen(agent.Name);
        AuthContext.Set(http, new AuthenticatedAgent(agent.Id, agent.Name, agent.Model, agent.DailyBudgetUsd,
            agent.ContextWindowTokens));

        // Log attributes (not resource attributes) so ids stay searchable without becoming labels.
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SharpClaw.Gateway.Agent");
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["agent"] = agent.Name,
            ["agent_id"] = agent.Id.ToString(),
        });
        return await next(context);
    }
}
