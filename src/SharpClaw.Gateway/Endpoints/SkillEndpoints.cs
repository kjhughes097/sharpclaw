using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Auth;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Endpoints;

/// <summary>
/// The agent reports the skills it loaded at startup (skills spec section 9.1), so the operator can
/// see them. The gateway validates them with the same rules as the agent's loader and never trusts
/// the agent beyond its own record.
/// </summary>
public static class SkillEndpoints
{
    public static void Map(RouteGroupBuilder agent) => agent.MapPut("/skills", ReportSkills);

    private static async Task<IResult> ReportSkills(
        ReportSkillsRequest? body, HttpContext http, SharpClawDbContext db, TimeProvider time,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        if (body?.Skills is not { } skills)
            return ApiResults.ValidationFailed("skills (an array) is required.");
        if (skills.Count > SkillRules.MaxSkills)
            return ApiResults.ValidationFailed($"At most {SkillRules.MaxSkills} skills may be reported.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var skill in skills)
        {
            // Messages name the rule that failed; a description is never echoed back.
            if (skill is null)
                return ApiResults.ValidationFailed("Invalid skill: entry is null.");
            if (SkillRules.NameError(skill.Name) is { } nameError)
                return ApiResults.ValidationFailed($"Invalid skill: {nameError}.");
            if (SkillRules.DescriptionError(skill.Description) is { } descriptionError)
                return ApiResults.ValidationFailed($"Invalid skill '{skill.Name}': {descriptionError}.");
            if (!names.Add(skill.Name))
                return ApiResults.ValidationFailed($"Skill '{skill.Name}' is listed more than once.");
        }

        var agent = AuthContext.Agent(http);
        var now = time.GetUtcNow().UtcDateTime;

        // Replace the whole set and stamp the report in one transaction; no client event (9.3).
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.AgentSkills.Where(s => s.AgentId == agent.Id).ExecuteDeleteAsync(ct);
            db.AgentSkills.AddRange(skills.Select(s => new AgentSkill
            {
                AgentId = agent.Id,
                Name = s.Name,
                Description = s.Description.Trim(),
            }));
            await db.SaveChangesAsync(ct);
            await db.Agents.Where(a => a.Id == agent.Id)
                .ExecuteUpdateAsync(a => a.SetProperty(x => x.SkillsReportedAt, now), ct);
            await tx.CommitAsync(ct);
        }

        var logger = loggerFactory.CreateLogger("SharpClaw.Gateway.Skills");
        using (LogScopes.ForAgent(logger, agent.Name, agent.Id))
            logger.LogInformation("skills reported: {SkillCount}", skills.Count);
        return Results.NoContent();
    }
}
