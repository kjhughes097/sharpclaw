using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Services;

namespace SharpClaw.Gateway.Endpoints;

public static class AgentsEndpoints
{
    public static void Map(RouteGroupBuilder client)
    {
        var agents = client.MapGroup("/agents").WithTags("agents");
        agents.MapPost("", CreateAgent).WithName("createAgent").Produces<CreatedAgentResponse>(StatusCodes.Status201Created);
        agents.MapGet("", ListAgents).WithName("listAgents").Produces<AgentListResponse>();
        agents.MapGet("/{idOrName}", GetAgent).WithName("getAgent").Produces<AgentDto>();
        agents.MapPatch("/{idOrName}", PatchAgent).WithName("updateAgent").Produces<AgentDto>();

        var tokens = client.MapGroup("/agents/{idOrName}/tokens").WithTags("tokens");
        tokens.MapPost("", IssueToken).WithName("issueToken").Produces<CreatedTokenResponse>(StatusCodes.Status201Created);
        tokens.MapGet("", ListTokens).WithName("listTokens").Produces<TokenListResponse>();
        tokens.MapDelete("/{tokenId}", RevokeToken).WithName("revokeToken").Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<IResult> CreateAgent(
        CreateAgentRequest? body, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (body is null)
            return ApiResults.ValidationFailed("Request body is required.");
        if (string.IsNullOrWhiteSpace(body.Name) || !Validation.IsValidAgentName(body.Name))
            return ApiResults.ValidationFailed("name must match ^[a-z0-9][a-z0-9-]{1,31}$.");
        if (string.IsNullOrWhiteSpace(body.Model))
            return ApiResults.ValidationFailed("model is required.");
        if (body.DailyBudgetUsd < 0)
            return ApiResults.ValidationFailed("dailyBudgetUsd must be >= 0.");
        if (AgentQueries.ValidateContextWindow(body.ContextWindowTokens) is { } windowError)
            return ApiResults.ValidationFailed(windowError);

        if (await db.Agents.AnyAsync(a => a.Name == body.Name, ct))
            return ApiResults.Conflict($"Agent name '{body.Name}' is already taken.");

        var now = time.GetUtcNow().UtcDateTime;
        var agent = new Agent
        {
            Id = Guid.CreateVersion7(),
            Name = body.Name,
            Model = body.Model,
            DailyBudgetUsd = body.DailyBudgetUsd,
            Enabled = true,
            ContextWindowTokens = body.ContextWindowTokens,
            CreatedAt = now,
        };
        var (tokenEntity, tokenValue) = NewToken(agent.Id, now, null);
        db.Agents.Add(agent);
        db.AgentTokens.Add(tokenEntity);
        await db.SaveChangesAsync(ct);

        var dto = AgentQueries.ToDto(agent, 0);
        var response = new CreatedAgentResponse(dto, tokenValue);
        return Results.Json(response, SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> ListAgents(SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        var dayStart = AgentQueries.DayStartUtc(time);
        var spend = await db.LlmCalls.AsNoTracking()
            .Where(c => c.CreatedAt >= dayStart)
            .GroupBy(c => c.AgentId)
            .Select(g => new { g.Key, Total = g.Sum(c => c.CostUsd) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, ct);

        var dtos = agents
            .Select(a => AgentQueries.ToDto(a, spend.GetValueOrDefault(a.Id)))
            .ToList();
        return Results.Json(new AgentListResponse(dtos), SharpClawJson.Options);
    }

    private static async Task<IResult> GetAgent(
        string idOrName, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");
        var spend = await AgentQueries.TodaySpendAsync(db, time, agent.Id, ct);
        return Results.Json(AgentQueries.ToDto(agent, spend), SharpClawJson.Options);
    }

    private static async Task<IResult> PatchAgent(
        string idOrName, UpdateAgentRequest? body, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");
        if (body is null)
            return ApiResults.ValidationFailed("Request body is required.");

        if (body.Model is not null)
        {
            if (string.IsNullOrWhiteSpace(body.Model))
                return ApiResults.ValidationFailed("model must be non-empty.");
            agent.Model = body.Model;
        }
        if (body.DailyBudgetUsd is not null)
        {
            if (body.DailyBudgetUsd < 0)
                return ApiResults.ValidationFailed("dailyBudgetUsd must be >= 0.");
            agent.DailyBudgetUsd = body.DailyBudgetUsd.Value;
        }
        if (body.Enabled is not null)
            agent.Enabled = body.Enabled.Value;
        if (body.ContextWindowTokens is not null)
        {
            if (AgentQueries.ValidateContextWindow(body.ContextWindowTokens) is { } windowError)
                return ApiResults.ValidationFailed(windowError);
            agent.ContextWindowTokens = body.ContextWindowTokens;
        }

        await db.SaveChangesAsync(ct);
        var spend = await AgentQueries.TodaySpendAsync(db, time, agent.Id, ct);
        return Results.Json(AgentQueries.ToDto(agent, spend), SharpClawJson.Options);
    }

    private static async Task<IResult> IssueToken(
        string idOrName,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateTokenRequest? body,
        SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var now = time.GetUtcNow().UtcDateTime;
        var expiresAt = body?.ExpiresAt?.ToUniversalTime();
        var (tokenEntity, tokenValue) = NewToken(agent.Id, now, expiresAt);
        db.AgentTokens.Add(tokenEntity);
        await db.SaveChangesAsync(ct);

        return Results.Json(new CreatedTokenResponse(tokenValue), SharpClawJson.Options, statusCode: 201);
    }

    private static async Task<IResult> ListTokens(string idOrName, SharpClawDbContext db, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var tokens = await db.AgentTokens.AsNoTracking()
            .Where(t => t.AgentId == agent.Id)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TokenMetaDto(t.Id, t.Prefix, t.CreatedAt, t.ExpiresAt, t.RevokedAt, t.LastUsedAt))
            .ToListAsync(ct);
        return Results.Json(new TokenListResponse(tokens), SharpClawJson.Options);
    }

    private static async Task<IResult> RevokeToken(
        string idOrName, Guid tokenId, SharpClawDbContext db, TimeProvider time, CancellationToken ct)
    {
        var agent = await AgentQueries.ResolveAsync(db, idOrName, ct);
        if (agent is null)
            return ApiResults.NotFound("Agent not found.");

        var token = await db.AgentTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.AgentId == agent.Id, ct);
        if (token is null)
            return ApiResults.NotFound("Token not found.");

        if (token.RevokedAt is null)
        {
            token.RevokedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static (AgentToken Entity, TokenValueDto Value) NewToken(Guid agentId, DateTime now, DateTime? expiresAt)
    {
        var generated = TokenGenerator.Create();
        var id = Guid.CreateVersion7();
        var entity = new AgentToken
        {
            Id = id,
            AgentId = agentId,
            TokenHash = generated.Hash,
            Prefix = generated.Prefix,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
        var value = new TokenValueDto(id, generated.Value, generated.Prefix, expiresAt);
        return (entity, value);
    }
}
