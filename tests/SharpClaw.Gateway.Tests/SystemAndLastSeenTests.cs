using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>System info and agent last-seen (3.3 spec sections 5.5 and 5.6).</summary>
public class SystemAndLastSeenTests
{
    [Fact]
    public async Task System_reports_version_provider_and_next_utc_midnight()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-10-25T21:30:00Z"));
        await using var fx = await GatewayFixture.CreateAsync(time);
        using var admin = fx.Admin();
        var info = (await admin.GetFromJsonAsync<SystemInfoResponse>("/api/system", SharpClawJson.Options))!;
        Assert.Equal("Fake", info.Provider);
        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.DoesNotContain("+", info.Version);
        Assert.Equal(DateTime.Parse("2026-10-25T21:30:00Z").ToUniversalTime(), info.UtcNow);
        Assert.Equal(new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc), info.BudgetResetAtUtc);
        Assert.Equal(DateTimeKind.Utc, info.BudgetResetAtUtc.Kind);
    }

    [Fact]
    public async Task Last_seen_is_null_until_the_agent_calls_and_updates_at_most_once_a_minute()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        using var admin = fx.Admin();
        Assert.Null((await admin.GetFromJsonAsync<AgentDto>("/api/agents/assistant", SharpClawJson.Options))!.LastSeenAt);

        var t0 = fx.Time.GetUtcNow().UtcDateTime;
        await fx.ClaimAsync(token);
        Assert.Equal(t0, (await admin.GetFromJsonAsync<AgentDto>("/api/agents/assistant", SharpClawJson.Options))!.LastSeenAt);

        fx.Time.Advance(TimeSpan.FromSeconds(30));
        await fx.ClaimAsync(token);
        Assert.Equal(t0, (await admin.GetFromJsonAsync<AgentDto>("/api/agents/assistant", SharpClawJson.Options))!.LastSeenAt);

        fx.Time.Advance(TimeSpan.FromSeconds(31));
        await fx.ClaimAsync(token);
        var list = (await admin.GetFromJsonAsync<AgentListResponse>("/api/agents", SharpClawJson.Options))!;
        Assert.Equal(fx.Time.GetUtcNow().UtcDateTime, Assert.Single(list.Agents, a => a.Id == agent.Id).LastSeenAt);
    }
}
