using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services.Broker;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// <c>POST /agent/tools/{tool}</c> (external tools spec section 4): listener, the operator's
/// per-agent gate, the turn check, routing by identity, the audit and the response cap.
/// Gateway-enforced approval is in <c>ToolBrokerTests.Approvals.cs</c>.
/// </summary>
public partial class ToolBrokerTests
{
    [Fact]
    public async Task Runs_an_enabled_tool_and_records_it_without_content()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch);
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), handler);
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { url = "https://example.com/SECRET_PATH" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(BrokerTools.HttpFetch, body.GetProperty("echoed").GetString());

        var call = Assert.Single(handler.Calls);
        Assert.Equal(t.Agent.Id, call.Agent.Id);
        Assert.Equal(t.Corr, call.CorrelationId);
        Assert.Equal("https://example.com/SECRET_PATH", call.Arguments.GetProperty("url").GetString());

        await using var db = fx.NewDb();
        var row = await db.BrokerCalls.SingleAsync();
        Assert.Equal((t.Agent.Id, BrokerTools.HttpFetch, ToolBroker.Statuses.Succeeded, "example.com", "call_1"),
            (row.AgentId, row.Tool, row.Status, row.TargetHost, row.ToolCallId));
        Assert.True(row.Bytes > 0);
        Assert.DoesNotContain("SECRET_PATH", JsonSerializer.Serialize(row));
    }

    [Fact]
    public async Task Wrong_listener_returns_404()
    {
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), new FakeBrokerHandler(BrokerTools.HttpFetch));
        var t = await fx.ClaimedTurnAsync();

        using var client = fx.ClientRaw();
        client.DefaultRequestHeaders.Authorization = new("Bearer", t.Token);
        var resp = await client.PostAsJsonAsync("/agent/tools/http_fetch",
            new InvokeToolRequest(t.Corr, t.Thread, "c", Args(new { }), null), SharpClawJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Requires_an_agent_token()
    {
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), new FakeBrokerHandler(BrokerTools.HttpFetch));

        using var raw = fx.AgentRaw();
        var resp = await raw.PostAsJsonAsync("/agent/tools/http_fetch",
            new InvokeToolRequest(Guid.NewGuid(), Guid.NewGuid(), "c", Args(new { }), null), SharpClawJson.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData("web_search")]   // a broker tool, not enabled for this agent
    [InlineData("no_such_tool")] // not a broker tool at all
    public async Task A_tool_not_enabled_for_the_agent_returns_404(string tool)
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch, BrokerTools.WebSearch);
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), handler);
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(tool, new { });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task An_agent_without_broker_config_gets_404()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch);
        await using var fx = await GatewayAsync(Config("someone-else", BrokerTools.HttpFetch), handler);
        var t = await fx.ClaimedTurnAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await t.InvokeAsync(BrokerTools.HttpFetch, new { })).StatusCode);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Outside_a_claimed_turn_returns_404()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch);
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), handler);
        var t = await fx.ClaimedTurnAsync();

        var resp = await (t with { Corr = Guid.NewGuid() }).InvokeAsync(BrokerTools.HttpFetch, new { });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Arguments_must_be_an_object()
    {
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), new FakeBrokerHandler(BrokerTools.HttpFetch));
        var t = await fx.ClaimedTurnAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await t.InvokeAsync(BrokerTools.HttpFetch, new[] { 1, 2 })).StatusCode);
    }

    [Fact]
    public async Task The_sandbox_is_chosen_by_the_agents_identity()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch);
        var config = Config("ade", BrokerTools.HttpFetch);
        foreach (var kv in Config("cody", BrokerTools.HttpFetch))
            config[kv.Key] = kv.Value;
        await using var fx = await GatewayAsync(config, handler);
        var ade = await fx.ClaimedTurnAsync("ade");
        var cody = await fx.ClaimedTurnAsync("cody");

        await ade.InvokeAsync(BrokerTools.HttpFetch, new { sandbox = "http://sandbox-cody:8090" });
        await cody.InvokeAsync(BrokerTools.HttpFetch, new { });

        Assert.Equal(new[] { "http://sandbox-ade:8090", "http://sandbox-cody:8090" }, handler.Calls.Select(c => c.Config.SandboxUrl));
    }

    [Fact]
    public async Task A_tool_without_a_handler_is_unavailable()
    {
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.WebSearch));
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.WebSearch, new { query = "x" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(ErrorCodes.ToolUnavailable, await ClientApiHelpers.ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task A_failing_handler_returns_502_and_is_recorded_as_an_error()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch) { Reply = _ => throw new InvalidOperationException("upstream said SECRET") };
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), handler);
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.DoesNotContain("SECRET", await resp.Content.ReadAsStringAsync());
        await using var db = fx.NewDb();
        Assert.Equal(ToolBroker.Statuses.Error, (await db.BrokerCalls.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_tool_error_is_returned_to_the_model_and_recorded_as_failed()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch) { Reply = _ => BrokerResult.ToolError("http_404", "Not found.") };
        await using var fx = await GatewayAsync(Config("assistant", BrokerTools.HttpFetch), handler);
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        await using var db = fx.NewDb();
        var row = await db.BrokerCalls.SingleAsync();
        Assert.Equal((ToolBroker.Statuses.Failed, "http_404"), (row.Status, row.ErrorCode));
    }

    [Fact]
    public async Task Results_over_the_response_cap_are_refused()
    {
        var handler = new FakeBrokerHandler(BrokerTools.HttpFetch) { Reply = _ => BrokerResult.Ok(new { text = new string('x', 5000) }) };
        var config = Config("assistant", BrokerTools.HttpFetch);
        config["ToolBroker:MaxResponseBytes"] = "2048";
        await using var fx = await GatewayAsync(config, handler);
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal(ErrorCodes.ToolFailed, await ClientApiHelpers.ErrorCodeAsync(resp));
    }
}
