using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services.Broker;

namespace SharpClaw.Gateway.Tests;

/// <summary>A broker handler that records its calls and returns a scripted result.</summary>
public sealed class FakeBrokerHandler(params string[] tools) : IBrokerToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = tools;
    public List<BrokerInvocation> Calls { get; } = new();
    public Func<BrokerInvocation, BrokerResult> Reply { get; set; } =
        call => BrokerResult.Ok(new { echoed = call.Tool }, targetHost: "example.com");

    public Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct)
    {
        Calls.Add(call);
        return Task.FromResult(Reply(call));
    }
}

/// <summary>Setup for broker tests: a gateway with broker config, an agent holding a claimed turn.</summary>
public static class BrokerTestHelpers
{
    public sealed record Turn(GatewayFixture Fx, AgentDto Agent, string Token, Guid Thread, Guid Corr);

    /// <summary>Broker config enabling <paramref name="tools"/> for <paramref name="agent"/>, with a sandbox.</summary>
    public static Dictionary<string, string?> Config(string agent, params string[] tools)
    {
        var config = new Dictionary<string, string?>
        {
            [$"ToolBroker:Agents:{agent}:SandboxUrl"] = $"http://sandbox-{agent}:8090",
            [$"ToolBroker:Agents:{agent}:SandboxSecret"] = $"secret-for-{agent}-0123456789",
        };
        for (var i = 0; i < tools.Length; i++)
            config[$"ToolBroker:Agents:{agent}:Tools:{i}"] = tools[i];
        return config;
    }

    public static async Task<GatewayFixture> GatewayAsync(
        IDictionary<string, string?> config, params IBrokerToolHandler[] handlers) =>
        await GatewayFixture.CreateAsync(extraConfig: config, configure: b =>
        {
            foreach (var h in handlers)
                b.Services.AddSingleton(h);
        });

    /// <summary>Creates the agent and a claimed user message for it.</summary>
    public static async Task<Turn> ClaimedTurnAsync(this GatewayFixture fx, string name = "assistant")
    {
        var (agent, token) = await fx.CreateAgentAsync(name);
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello");
        var corr = (await fx.ClaimAsync(token))!.Id;
        return new Turn(fx, agent, token, thread, corr);
    }

    /// <summary>Finishes the claimed turn with a reply.</summary>
    public static async Task ReplyToClaimedAsync(this GatewayFixture fx, Turn t)
    {
        using var c = fx.AgentClient(t.Token);
        (await c.PostAsJsonAsync($"/agent/threads/{t.Thread}/messages",
            new AgentReplyRequest("done", t.Corr), SharpClawJson.Options)).EnsureSuccessStatusCode();
    }

    public static JsonElement Args(object args) => JsonSerializer.SerializeToElement(args, SharpClawJson.Options);

    public static async Task<HttpResponseMessage> InvokeAsync(
        this Turn t, string tool, object args, Guid? approvalId = null, string toolCallId = "call_1")
    {
        using var c = t.Fx.AgentClient(t.Token);
        return await c.PostAsJsonAsync($"/agent/tools/{tool}",
            new InvokeToolRequest(t.Corr, t.Thread, toolCallId, Args(args), approvalId), SharpClawJson.Options);
    }

    /// <summary>Requests an approval for <paramref name="tool"/> with the hash of <paramref name="args"/>; optionally decides it.</summary>
    public static async Task<Guid> ApprovalAsync(this Turn t, string tool, object args, string? decision = ApprovalStatuses.Approved,
        string? argsHash = null)
    {
        using var c = t.Fx.AgentClient(t.Token);
        var resp = await c.PostAsJsonAsync("/agent/approvals", new CreateApprovalRequest(
            t.Corr, t.Thread, "call_1", tool, $"{tool} call", RiskLevels.High, "needs approval",
            argsHash ?? ToolArgsHash.Compute(Args(args))), SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        var id = (await resp.Content.ReadFromJsonAsync<ApprovalResponse>(SharpClawJson.Options))!.Approval.Id;
        if (decision is not null)
        {
            using var admin = t.Fx.Admin();
            (await admin.PostAsJsonAsync($"/api/approvals/{id}", new DecideApprovalRequest(decision), SharpClawJson.Options))
                .EnsureSuccessStatusCode();
        }
        return id;
    }
}
