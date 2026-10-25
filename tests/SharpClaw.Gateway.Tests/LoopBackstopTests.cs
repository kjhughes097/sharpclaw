using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>The gateway's per-message LLM call backstop (3.2 spec section 8.1).</summary>
public class LoopBackstopTests
{
    [Fact]
    public async Task Calls_beyond_the_limit_for_one_message_are_rejected()
    {
        await using var fx = await GatewayFixture.CreateAsync(
            extraConfig: new Dictionary<string, string?> { ["Chat:MaxLlmCallsPerMessage"] = "3" });
        var (agent, token) = await fx.CreateAgentAsync(budget: 10);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "first");
        var corr = (await fx.ClaimAsync(token))!.Id;

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);

        var rejected = await fx.ChatAsync(token, corr, thread);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.False(rejected.Headers.Contains("Retry-After"));
        var error = await rejected.Content.ReadFromJsonAsync<ApiErrorResponse>(SharpClawJson.Options);
        Assert.Equal(ErrorCodes.LoopLimitExceeded, error!.Error.Code);

        using (var db = fx.NewDb())
        {
            var row = await db.LlmCalls.AsNoTracking().Where(c => c.Status == LlmCallStatus.Rejected).SingleAsync();
            Assert.Equal(ErrorCodes.LoopLimitExceeded, row.ErrorCode);
            Assert.Equal(0, row.CostUsd);
        }

        // The limit is per message: the next message starts from zero.
        await fx.PostUserMessageAsync(thread, "second");
        var next = (await fx.ClaimAsync(token))!.Id;
        Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, next, thread)).StatusCode);
    }

    [Fact]
    public async Task Default_limit_is_twenty()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync(budget: 10);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;

        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.OK, (await fx.ChatAsync(token, corr, thread)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await fx.ChatAsync(token, corr, thread)).StatusCode);
    }
}
