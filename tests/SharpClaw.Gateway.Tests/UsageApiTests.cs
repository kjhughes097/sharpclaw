using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary><c>GET /api/usage</c> over HTTP and the database (3.3.1 spec section 3.1).</summary>
public class UsageApiTests
{
    private static async Task SeedCallAsync(
        GatewayFixture fx, Guid agentId, Guid threadId, DateTime at, double cost, string model = "test/model")
    {
        await using var db = fx.NewDb();
        db.LlmCalls.Add(new LlmCall
        {
            Id = Guid.CreateVersion7(), AgentId = agentId, ThreadId = threadId, CorrelationId = Guid.CreateVersion7(),
            Model = model, Status = LlmCallStatus.Ok, PromptTokens = 1000, CompletionTokens = 100, CostUsd = cost,
            UsageKnown = true, LatencyMs = 50, CreatedAt = at,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Usage_buckets_across_a_utc_midnight_and_filters_by_agent()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-28T13:20:00Z"));
        await using var fx = await GatewayFixture.CreateAsync(time);
        var (ade, _) = await fx.CreateAgentAsync("ade");
        var (cody, _) = await fx.CreateAgentAsync("cody");
        var adeThread = await fx.CreateThreadAsync(ade.Id);
        var codyThread = await fx.CreateThreadAsync(cody.Id);
        var now = time.GetUtcNow().UtcDateTime;
        await SeedCallAsync(fx, ade.Id, adeThread, now.Date.AddMinutes(-1), 0.5);           // yesterday 23:59
        await SeedCallAsync(fx, ade.Id, adeThread, now.Date.AddHours(9), 0.02, "m/dear");   // today 09:00
        await SeedCallAsync(fx, cody.Id, codyThread, now.Date.AddHours(13), 0.01);          // today 13:00

        using var admin = fx.Admin();
        var today = (await admin.GetFromJsonAsync<UsageResponse>("/api/usage?range=today", SharpClawJson.Options))!;
        Assert.Equal(14, today.Buckets.Count);
        Assert.Equal(2, today.Totals.Calls);
        Assert.Equal(1, today.Buckets[9].Calls);
        Assert.Equal(1, today.Buckets[13].Calls);
        Assert.Equal(["ade", "cody"], today.ByAgent.Select(a => a.AgentName));

        var week = (await admin.GetFromJsonAsync<UsageResponse>("/api/usage?range=7d", SharpClawJson.Options))!;
        Assert.Equal(3, week.Totals.Calls);
        Assert.Equal("test/model", week.ByModel[0].Model);
        Assert.Equal(0.51, week.ByModel[0].CostUsd, 6);

        var adeOnly = (await admin.GetFromJsonAsync<UsageResponse>("/api/usage?range=today&agent=ade", SharpClawJson.Options))!;
        Assert.Equal(1, adeOnly.Totals.Calls);
        Assert.Equal("ade", Assert.Single(adeOnly.ByAgent).AgentName);
        Assert.Equal("m/dear", Assert.Single(adeOnly.ByModel).Model);
    }

    [Fact]
    public async Task Bad_range_is_400_and_unknown_agent_is_404()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var admin = fx.Admin();

        var bad = await admin.GetAsync("/api/usage?range=1y");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(ErrorCodes.ValidationFailed, await ClientApiHelpers.ErrorCodeAsync(bad));

        var missing = await admin.GetAsync("/api/usage?range=7d&agent=nobody");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Usage_is_not_served_on_the_agent_listener()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (_, token) = await fx.CreateAgentAsync();
        using var agent = fx.AgentClient(token);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/api/usage")).StatusCode);
    }
}
