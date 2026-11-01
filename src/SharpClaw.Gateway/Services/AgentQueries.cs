using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Services;

public static class AgentQueries
{
    public static async Task<Agent?> ResolveAsync(SharpClawDbContext db, string idOrName, CancellationToken ct)
    {
        if (Guid.TryParse(idOrName, out var id))
            return await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct);
        return await db.Agents.FirstOrDefaultAsync(a => a.Name == idOrName, ct);
    }

    public static DateTime DayStartUtc(TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
    }

    public static async Task<double> TodaySpendAsync(
        SharpClawDbContext db, TimeProvider time, Guid agentId, CancellationToken ct)
    {
        var dayStart = DayStartUtc(time);
        return await db.LlmCalls
            .Where(c => c.AgentId == agentId && c.CreatedAt >= dayStart)
            .SumAsync(c => (double?)c.CostUsd, ct) ?? 0;
    }

    /// <param name="skills">The agent's reported skills (<see cref="SkillsAsync"/>); empty when none.</param>
    public static AgentDto ToDto(Agent a, double todaySpend, IReadOnlyList<AgentSkillDto> skills)
    {
        var remaining = Math.Max(0, a.DailyBudgetUsd - todaySpend);
        return new AgentDto(a.Id, a.Name, a.Model, a.DailyBudgetUsd, a.Enabled, a.CreatedAt, todaySpend, remaining,
            skills, a.ContextWindowTokens, a.LastSeenAt, a.SkillsReportedAt, a.Autonomous);
    }

    /// <summary>One agent's reported skills, in ordinal name order.</summary>
    public static async Task<IReadOnlyList<AgentSkillDto>> SkillsAsync(SharpClawDbContext db, Guid agentId, CancellationToken ct) =>
        (await SkillsByAgentAsync(db, agentId, ct)).GetValueOrDefault(agentId) ?? Array.Empty<AgentSkillDto>();

    /// <summary>Reported skills grouped by agent (all agents, or just <paramref name="agentId"/>), each in ordinal name order.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<AgentSkillDto>>> SkillsByAgentAsync(
        SharpClawDbContext db, Guid? agentId, CancellationToken ct)
    {
        var query = db.AgentSkills.AsNoTracking();
        if (agentId is { } id)
            query = query.Where(s => s.AgentId == id);
        var rows = await query.ToListAsync(ct);

        // Sorted in memory: SQLite's default collation is binary, but ordinal order is the contract.
        return rows.GroupBy(s => s.AgentId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<AgentSkillDto>)g.OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new AgentSkillDto(s.Name, s.Description)).ToList());
    }

    /// <summary>Null when valid; otherwise the validation message.</summary>
    public static string? ValidateContextWindow(int? tokens) =>
        tokens is null or (>= ContextDefaults.MinWindowTokens and <= ContextDefaults.MaxWindowTokens)
            ? null
            : $"contextWindowTokens must be between {ContextDefaults.MinWindowTokens} and {ContextDefaults.MaxWindowTokens}.";
}
