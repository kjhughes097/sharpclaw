using System.Net;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

public class BudgetTests
{
    private const string HalfDollarScript = """
    { "rules": [ { "match": ".*", "response": "ok",
      "usage": { "promptTokens": 1, "completionTokens": 1, "costUsd": 0.5 } } ] }
    """;

    [Fact]
    public async Task Allows_under_cap_then_429_with_retry_after_once_spent_reaches_cap()
    {
        var script = ChatTestHelpers.WriteScript(HalfDollarScript);
        await using var fx = await GatewayFixture.CreateAsync(
            extraConfig: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        var (agent, token) = await fx.CreateAgentAsync(budget: 1.0);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;

        // Two calls at $0.5 are allowed (spent 0 then 0.5, both < 1.0).
        Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);

        // Spent is now 1.0 >= cap -> 429 with Retry-After.
        var rejected = await fx.ChatAsync(token, corr, thread);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));

        using var db = fx.NewDb();
        Assert.True(await db.LlmCalls.AnyAsync(c => c.Status == LlmCallStatus.Rejected));
    }

    [Fact]
    public async Task Budget_resets_after_utc_midnight()
    {
        var script = ChatTestHelpers.WriteScript(HalfDollarScript);
        await using var fx = await GatewayFixture.CreateAsync(
            time: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
                DateTimeOffset.Parse("2026-10-25T23:00:00Z")),
            extraConfig: new Dictionary<string, string?> { ["Fake:ScriptPath"] = script });
        var (agent, token) = await fx.CreateAgentAsync(budget: 1.0);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;

        Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fx.ChatAsync(token, corr, thread)).StatusCode);

        // Roll past UTC midnight; spend resets.
        fx.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);
    }

    [Fact]
    public async Task Zero_budget_always_rejects()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync(budget: 0.0);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;

        var resp = await fx.ChatAsync(token, corr, thread);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }
}
