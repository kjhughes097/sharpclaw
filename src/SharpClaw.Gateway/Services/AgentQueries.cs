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

    public static AgentDto ToDto(Agent a, double todaySpend)
    {
        var remaining = Math.Max(0, a.DailyBudgetUsd - todaySpend);
        return new AgentDto(a.Id, a.Name, a.Model, a.DailyBudgetUsd, a.Enabled, a.CreatedAt, todaySpend, remaining);
    }
}
