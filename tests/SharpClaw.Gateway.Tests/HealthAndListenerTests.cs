using System.Net;
using System.Net.Http.Json;

namespace SharpClaw.Gateway.Tests;

public class HealthAndListenerTests
{
    [Fact]
    public async Task Healthz_ok_on_both_listeners()
    {
        await using var fx = await GatewayFixture.CreateAsync();

        using var client = fx.ClientRaw();
        using var agent = fx.AgentRaw();

        var c = await client.GetAsync("/healthz");
        var a = await agent.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, c.StatusCode);
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Contains("ok", await c.Content.ReadAsStringAsync());
    }
}
