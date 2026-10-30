extern alias sandbox;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SharpClaw.Contracts;
using RunnerApp = sandbox::SharpClaw.Sandbox.RunnerApp;
using RunnerOptions = sandbox::SharpClaw.Sandbox.Infrastructure.RunnerOptions;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// <c>sandbox_run</c> through the gateway (spec sections 7.5, Q1, Q2, Q6): always approved by a
/// human who sees the command in the approval detail and nowhere else; the operator's time limit.
/// </summary>
public class SandboxRunTests : IAsyncLifetime
{
    private const string Secret = "runner-service-secret-0123";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ws-{Guid.NewGuid():N}");
    private WebApplication _runner = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        _port = GatewayFixture.FreePort();
        _runner = RunnerApp.Build(new RunnerOptions { Secret = Secret, Port = _port, WorkspacePath = _workspace });
        await _runner.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _runner.DisposeAsync();
        Directory.Delete(_workspace, recursive: true);
    }

    private async Task<(GatewayFixture Fx, Turn Turn)> SetupAsync(Dictionary<string, string?>? extra = null)
    {
        var config = Config("assistant", BrokerTools.SandboxRun);
        config["ToolBroker:Agents:assistant:SandboxUrl"] = $"http://127.0.0.1:{_port}";
        config["ToolBroker:Agents:assistant:SandboxSecret"] = Secret;
        foreach (var kv in extra ?? new())
            config[kv.Key] = kv.Value;
        var fx = await GatewayAsync(config);
        return (fx, await fx.ClaimedTurnAsync());
    }

    /// <summary>Asks for an approval the way the agent does: summary only, plus arguments and their hash.</summary>
    private static async Task<(Guid Id, HttpStatusCode Status)> RequestAsync(Turn t, object args, string? hash = null)
    {
        var element = Args(args);
        using var c = t.Fx.AgentClient(t.Token);
        var resp = await c.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(
            t.Corr, t.Thread, "call_1", BrokerTools.SandboxRun, "run (11 chars) in repo/", RiskLevels.High, "runs code",
            hash ?? ToolArgsHash.Compute(element), element), SharpClawJson.Options);
        return resp.IsSuccessStatusCode
            ? ((await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval.Id, resp.StatusCode)
            : (Guid.Empty, resp.StatusCode);
    }

    [Fact]
    public async Task An_approved_command_runs_in_the_sandbox()
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;
        var args = new { command = "echo hello", cwd = "." };
        var (id, _) = await RequestAsync(t, args);
        using (var admin = fx.Admin())
            (await admin.PostAsJsonAsync($"/api/approvals/{id}", new DecideApprovalRequest(ApprovalStatuses.Approved), SharpClawJson.Options))
                .EnsureSuccessStatusCode();

        var resp = await t.InvokeAsync(BrokerTools.SandboxRun, args, id);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("hello\n", 0), (body.GetProperty("stdout").GetString(), body.GetProperty("exitCode").GetInt32()));
    }

    [Fact]
    public async Task Without_an_approval_nothing_runs()
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;

        var resp = await t.InvokeAsync(BrokerTools.SandboxRun, new { command = "touch ran" });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(File.Exists(Path.Combine(_workspace, "ran")));
    }

    [Fact]
    public async Task The_command_appears_only_in_the_approval_detail()
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;
        var command = "npm test && curl SECRET_HOST " + new string('x', 1500);
        var (stream, events) = await OpenEventsAsync(fx);

        var (id, _) = await RequestAsync(t, new { command, cwd = "repo" });

        using var admin = fx.Admin();
        var detail = (await admin.GetFromJsonAsync<ApprovalDetailResponse>($"/api/approvals/{id}", SharpClawJson.Options))!.Approval.Detail!;
        Assert.StartsWith("in repo/\n$ npm test && curl SECRET_HOST", detail);
        Assert.EndsWith("… (" + (command.Length - 1000) + " more characters)", detail);
        var list = await admin.GetStringAsync("/api/approvals");
        Assert.DoesNotContain("SECRET_HOST", list);
        var requested = (await events).Single(e => e.Event == SseEventNames.ApprovalRequested);
        Assert.DoesNotContain("SECRET_HOST", requested.Data);
        stream.Dispose();
    }

    [Fact]
    public async Task Arguments_that_do_not_match_the_hash_are_refused()
    {
        var (fx, t) = await SetupAsync();
        await using var _ = fx;

        var (_, status) = await RequestAsync(t, new { command = "ls" }, hash: ToolArgsHash.Compute(Args(new { command = "rm -rf /" })));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task The_operator_time_limit_applies()
    {
        var (fx, t) = await SetupAsync(new() { ["ToolBroker:SandboxRunMaxSeconds"] = "30", ["ToolBroker:AlwaysApprove:0"] = "" });
        await using var _ = fx;

        var body = await (await t.InvokeAsync(BrokerTools.SandboxRun, new { command = "true", timeoutSeconds = 31 })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(ErrorCodes.InvalidArguments, body.GetProperty("error").GetString());
    }

    private static async Task<(HttpClient Client, Task<List<(string Event, string Data)>> Events)> OpenEventsAsync(GatewayFixture fx)
    {
        var client = fx.Admin();
        var resp = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead);
        var events = TestHelpers.ReadSseEventsAsync(await resp.Content.ReadAsStreamAsync(), 1, TimeSpan.FromSeconds(5));
        return (client, events);
    }
}
