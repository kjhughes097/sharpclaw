extern alias sandbox;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services.Broker;
using RunnerApp = sandbox::SharpClaw.Sandbox.RunnerApp;
using RunnerOptions = sandbox::SharpClaw.Sandbox.Infrastructure.RunnerOptions;
using ProxiedOutboundHttp = sandbox::SharpClaw.Sandbox.Infrastructure.ProxiedOutboundHttp;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Sandbox tools end to end: agent → gateway broker → the agent's own runner (in process) → a
/// loopback page; and the runner's outbound client through the gateway's real egress listener.
/// </summary>
public class SandboxBrokerTests : IAsyncLifetime
{
    private const string RunnerSecret = "runner-service-secret-0123";
    private WebApplication _runner = null!;
    private WebApplication _pages = null!;
    private int _runnerPort;
    private int _pagesPort;
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        _runnerPort = GatewayFixture.FreePort();
        _runner = RunnerApp.Build(new RunnerOptions { Secret = RunnerSecret, Port = _runnerPort, WorkspacePath = _workspace });
        await _runner.StartAsync();

        _pagesPort = GatewayFixture.FreePort();
        var pages = WebApplication.CreateSlimBuilder();
        pages.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, _pagesPort));
        _pages = pages.Build();
        _pages.MapGet("/doc.txt", () => Results.Text("plain text page", "text/plain"));
        await _pages.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _runner.DisposeAsync();
        await _pages.DisposeAsync();
        Directory.Delete(_workspace, recursive: true);
    }

    private Dictionary<string, string?> SandboxConfig(string secret = RunnerSecret)
    {
        var config = Config("assistant", BrokerTools.HttpFetch, BrokerTools.DownloadToWorkspace);
        config["ToolBroker:Agents:assistant:SandboxUrl"] = $"http://127.0.0.1:{_runnerPort}";
        config["ToolBroker:Agents:assistant:SandboxSecret"] = secret;
        return config;
    }

    [Fact]
    public async Task Http_fetch_runs_in_the_agents_sandbox_and_is_audited_by_host()
    {
        await using var fx = await GatewayAsync(SandboxConfig());
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { url = $"http://127.0.0.1:{_pagesPort}/doc.txt?q=SECRET" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plain text page", body.GetProperty("content").GetString());
        await using var db = fx.NewDb();
        var row = await db.BrokerCalls.SingleAsync();
        Assert.Equal(("127.0.0.1", ToolBroker.Statuses.Succeeded), (row.TargetHost, row.Status));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(row));
    }

    [Fact]
    public async Task Download_lands_in_the_workspace()
    {
        await using var fx = await GatewayAsync(SandboxConfig());
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.DownloadToWorkspace, new { url = $"http://127.0.0.1:{_pagesPort}/doc.txt", path = "in/doc.txt" });

        Assert.True((await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ok").GetBoolean());
        Assert.Equal("plain text page", File.ReadAllText(Path.Combine(_workspace, "in", "doc.txt")));
    }

    [Fact]
    public async Task Without_a_sandbox_secret_the_tool_is_unavailable()
    {
        var config = SandboxConfig();
        config["ToolBroker:Agents:assistant:SandboxSecret"] = "";
        await using var fx = await GatewayAsync(config);
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { url = "http://example.com/" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(ErrorCodes.ToolUnavailable, await ClientApiHelpers.ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task A_runner_that_rejects_the_gateway_is_a_tool_failure()
    {
        await using var fx = await GatewayAsync(SandboxConfig(secret: "not-the-runners-secret-0123"));
        var t = await fx.ClaimedTurnAsync();

        var resp = await t.InvokeAsync(BrokerTools.HttpFetch, new { url = "http://example.com/" });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal(ErrorCodes.ToolFailed, await ClientApiHelpers.ErrorCodeAsync(resp));
    }

    [Fact]
    public async Task The_policy_endpoint_reports_the_operators_settings_for_the_calling_agent()
    {
        var config = SandboxConfig();
        config["ToolBroker:Agents:assistant:Tools:2"] = BrokerTools.SandboxRun;
        config["ToolBroker:FetchAllowHosts:0"] = "docs.github.com";
        await using var fx = await GatewayAsync(config);
        var (_, token) = await fx.CreateAgentAsync("assistant");
        var (_, other) = await fx.CreateAgentAsync("other");

        using var c = fx.AgentClient(token);
        var policy = await c.GetFromJsonAsync<AgentToolPolicyResponse>("/agent/tools", SharpClawJson.Options);
        using var o = fx.AgentClient(other);
        var none = await o.GetFromJsonAsync<AgentToolPolicyResponse>("/agent/tools", SharpClawJson.Options);

        Assert.Equal(new[] { BrokerTools.HttpFetch, BrokerTools.DownloadToWorkspace, BrokerTools.SandboxRun }, policy!.Tools);
        Assert.Equal(new[] { BrokerTools.SandboxRun }, policy.AlwaysApprove);
        Assert.Equal(new[] { "docs.github.com" }, policy.FetchAllowHosts);
        Assert.Empty(none!.Tools);
    }

    [Fact]
    public async Task The_runners_client_goes_through_the_egress_proxy_with_its_credential()
    {
        await using var h = await EgressHarness.StartAsync();
        using var http = new ProxiedOutboundHttp(new Uri($"http://sbx-{EgressHarness.Sandbox}:{EgressHarness.Secret}@127.0.0.1:{h.Port}"));

        using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://public.test/page"), default);

        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        Assert.Equal(new IPEndPoint(EgressHarness.PublicIp, 80), Assert.Single(h.Connector.Calls));
    }

    [Fact]
    public async Task The_runners_https_tunnel_presents_its_credential_and_respects_a_refusal()
    {
        await using var h = await EgressHarness.StartAsync();
        using var allowed = new ProxiedOutboundHttp(new Uri($"http://sbx-{EgressHarness.Sandbox}:{EgressHarness.Secret}@127.0.0.1:{h.Port}"));
        using var wrong = new ProxiedOutboundHttp(new Uri($"http://sbx-{EgressHarness.Sandbox}:wrong-secret@127.0.0.1:{h.Port}"));

        // Blocked by the proxy: a private address.
        var blocked = await Assert.ThrowsAsync<HttpRequestException>(() =>
            allowed.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://private.test/"), default));
        var unauthorised = await Assert.ThrowsAsync<HttpRequestException>(() =>
            wrong.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://public.test/"), default));

        Assert.Contains("403", blocked.InnerException?.Message ?? blocked.Message);
        Assert.Contains("407", unauthorised.InnerException?.Message ?? unauthorised.Message);
        Assert.Empty(h.Connector.Calls);
    }
}
