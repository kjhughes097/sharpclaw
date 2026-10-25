using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;
using SharpClaw.Gateway.Infrastructure;

namespace SharpClaw.Gateway.Tests;

public class AuthAndAgentsTests
{
    [Fact]
    public async Task Admin_key_required_and_validated()
    {
        await using var fx = await GatewayFixture.CreateAsync();

        using var noAuth = fx.ClientRaw();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noAuth.GetAsync("/api/agents")).StatusCode);

        using var wrong = fx.ClientRaw();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/agents")).StatusCode);

        using var admin = fx.Admin();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/agents")).StatusCode);
    }

    [Fact]
    public async Task Create_agent_returns_token_once_and_stores_hash()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var admin = fx.Admin();

        var resp = await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest("assistant", "test/model", 1.0), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<CreatedAgentResponse>(SharpClawJson.Options);
        Assert.NotNull(created);
        Assert.StartsWith("sc_", created!.Token.Value);
        Assert.Equal(created.Token.Value[..8], created.Token.Prefix);

        // The value never appears again via the tokens listing.
        var list = await admin.GetFromJsonAsync<TokenListResponse>(
            $"/api/agents/assistant/tokens", SharpClawJson.Options);
        Assert.Single(list!.Tokens);
        Assert.Equal(created.Token.Prefix, list.Tokens[0].Prefix);

        // Stored token hash is lowercase hex SHA-256 of the value.
        using var db = fx.NewDb();
        var token = await db.AgentTokens.AsNoTracking().SingleAsync();
        Assert.Equal(Hashing.Sha256Hex(created.Token.Value), token.TokenHash);
        Assert.Matches("^[0-9a-f]{64}$", token.TokenHash);
    }

    [Fact]
    public async Task Create_agent_rejects_duplicate_and_bad_name()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        using var admin = fx.Admin();

        var ok = await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest("assistant", "m", 1.0), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var dup = await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest("assistant", "m", 1.0), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var bad = await admin.PostAsJsonAsync("/api/agents",
            new CreateAgentRequest("Bad Name!", "m", 1.0), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Patch_agent_updates_fields()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync(budget: 1.0);
        using var admin = fx.Admin();

        var resp = await admin.PatchAsJsonAsync($"/api/agents/{agent.Name}",
            new UpdateAgentRequest("new/model", 5.0, false), SharpClawJson.Options);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<AgentDto>(SharpClawJson.Options);
        Assert.Equal("new/model", dto!.Model);
        Assert.Equal(5.0, dto.DailyBudgetUsd);
        Assert.False(dto.Enabled);
    }

    [Fact]
    public async Task Token_revoke_is_idempotent_and_blocks_auth()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();
        using var admin = fx.Admin();

        var tokenId = (await admin.GetFromJsonAsync<TokenListResponse>(
            $"/api/agents/{agent.Name}/tokens", SharpClawJson.Options))!.Tokens[0].Id;

        var first = await admin.DeleteAsync($"/api/agents/{agent.Name}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var second = await admin.DeleteAsync($"/api/agents/{agent.Name}/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        using var agentClient = fx.AgentClient(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentClient.GetAsync("/agent/threads/x/messages")).StatusCode);
    }

    [Fact]
    public async Task Agent_token_validation_matrix()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, token) = await fx.CreateAgentAsync();

        // Unknown token -> 401
        using (var unknown = fx.AgentClient("sc_unknown"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await unknown.GetAsync("/agent/inbox?wait=0")).StatusCode);

        // Disabled agent -> 403
        using (var admin = fx.Admin())
            await admin.PatchAsJsonAsync($"/api/agents/{agent.Name}",
                new UpdateAgentRequest(null, null, false), SharpClawJson.Options);
        using (var disabled = fx.AgentClient(token))
            Assert.Equal(HttpStatusCode.Forbidden, (await disabled.GetAsync("/agent/inbox?wait=0")).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_unauthorized()
    {
        await using var fx = await GatewayFixture.CreateAsync();
        var (agent, _) = await fx.CreateAgentAsync();
        using var admin = fx.Admin();

        var expired = fx.Time.GetUtcNow().UtcDateTime.AddMinutes(-1);
        var resp = await admin.PostAsJsonAsync($"/api/agents/{agent.Name}/tokens",
            new CreateTokenRequest(expired), SharpClawJson.Options);
        var created = await resp.Content.ReadFromJsonAsync<CreatedTokenResponse>(SharpClawJson.Options);

        using var agentClient = fx.AgentClient(created!.Token.Value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentClient.GetAsync("/agent/inbox?wait=0")).StatusCode);
    }

    [Fact]
    public async Task Listeners_are_separated_by_port()
    {
        await using var fx = await GatewayFixture.CreateAsync();

        // Client route on the agent port -> 404
        using var agentPort = fx.AgentRaw();
        agentPort.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayFixture.AdminKey);
        Assert.Equal(HttpStatusCode.NotFound, (await agentPort.GetAsync("/api/agents")).StatusCode);

        // Agent route on the client port -> 404
        using var clientPort = fx.ClientRaw();
        clientPort.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sometoken");
        Assert.Equal(HttpStatusCode.NotFound, (await clientPort.GetAsync("/agent/inbox?wait=0")).StatusCode);
    }
}
