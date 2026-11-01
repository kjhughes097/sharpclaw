using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Services.Broker;
using static SharpClaw.Gateway.Tests.BrokerTestHelpers;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// Gateway-enforced approval (spec section 4, step 3, and Q2): an always-approve tool runs only with
/// an approval that is this agent's, for this turn and tool, approved, recent, unused, and hashed
/// over exactly these arguments.
/// </summary>
public partial class ToolBrokerTests
{
    private static readonly object RunArgs = new { command = "npm test", cwd = "repo" };

    private static async Task<(GatewayFixture Fx, FakeBrokerHandler Handler, Turn Turn)> ApprovalSetupAsync(
        Dictionary<string, string?>? extra = null)
    {
        var handler = new FakeBrokerHandler(BrokerTools.SandboxRun, BrokerTools.HttpFetch);
        var config = Config("assistant", BrokerTools.SandboxRun, BrokerTools.HttpFetch);
        // sandbox_run is no longer always-approve by default (3.6); these tests are about the mechanism.
        config["ToolBroker:AlwaysApprove:0"] = BrokerTools.SandboxRun;
        foreach (var kv in extra ?? new())
            config[kv.Key] = kv.Value;
        var fx = await GatewayAsync(config, handler);
        return (fx, handler, await fx.ClaimedTurnAsync());
    }

    private static async Task AssertApprovalRequired(HttpResponseMessage resp, FakeBrokerHandler handler)
    {
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Equal(ErrorCodes.ApprovalRequired, await ClientApiHelpers.ErrorCodeAsync(resp));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task An_always_approve_tool_without_an_approval_is_refused()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs), handler);
        await using var db = fx.NewDb();
        Assert.Equal(ToolBroker.Statuses.Refused, (await db.BrokerCalls.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_matching_approval_runs_once_and_is_marked_used()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var approval = await t.ApprovalAsync(BrokerTools.SandboxRun, RunArgs);

        var first = await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval);
        var reused = await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
        Assert.Single(handler.Calls);
        await using var db = fx.NewDb();
        Assert.NotNull((await db.Approvals.SingleAsync(a => a.Id == approval)).UsedAt);
        Assert.Equal(approval, (await db.BrokerCalls.OrderBy(b => b.CreatedAt).FirstAsync()).ApprovalId);
    }

    [Fact]
    public async Task Key_order_and_formatting_do_not_change_the_hash_but_values_do()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var approval = await t.ApprovalAsync(BrokerTools.SandboxRun, new { cwd = "repo", command = "npm test" });

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, new { command = "npm test; curl evil", cwd = "repo" }, approval), handler);
        Assert.Equal(HttpStatusCode.OK, (await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval)).StatusCode);
    }

    [Theory]
    [InlineData(ApprovalStatuses.Denied)]
    [InlineData(null)] // still pending
    public async Task An_approval_that_is_not_approved_is_refused(string? decision)
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var approval = await t.ApprovalAsync(BrokerTools.SandboxRun, RunArgs, decision);

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval), handler);
    }

    [Fact]
    public async Task An_approval_for_another_tool_is_refused()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var approval = await t.ApprovalAsync(BrokerTools.HttpFetch, RunArgs);

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval), handler);
    }

    [Fact]
    public async Task An_approval_without_a_hash_is_refused()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var approval = await fx.RequestApprovalAsync(t.Token, t.Thread, t.Corr); // no args hash
        using (var admin = fx.Admin())
            (await admin.PostAsJsonAsync($"/api/approvals/{approval}",
                new DecideApprovalRequest(ApprovalStatuses.Approved), SharpClawJson.Options)).EnsureSuccessStatusCode();

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval), handler);
    }

    [Fact]
    public async Task Another_agents_approval_is_refused()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var other = await fx.ClaimedTurnAsync("other");
        var approval = await other.ApprovalAsync(BrokerTools.SandboxRun, RunArgs);

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval), handler);
    }

    [Fact]
    public async Task An_approval_from_another_turn_is_refused()
    {
        var (fx, handler, t) = await ApprovalSetupAsync();
        await using var _ = fx;
        var approval = await t.ApprovalAsync(BrokerTools.SandboxRun, RunArgs);
        await fx.ReplyToClaimedAsync(t);
        await fx.PostUserMessageAsync(t.Thread, "again");
        var next = t with { Corr = (await fx.ClaimAsync(t.Token))!.Id };

        await AssertApprovalRequired(await next.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval), handler);
    }

    [Fact]
    public async Task An_approval_used_too_long_after_its_decision_is_refused()
    {
        var (fx, handler, t) = await ApprovalSetupAsync(new() { ["ToolBroker:ApprovalUseSeconds"] = "60" });
        await using var _ = fx;
        var approval = await t.ApprovalAsync(BrokerTools.SandboxRun, RunArgs);
        fx.Time.Advance(TimeSpan.FromSeconds(61));

        await AssertApprovalRequired(await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs, approval), handler);
    }

    [Fact]
    public async Task The_operator_can_remove_a_tool_from_always_approve()
    {
        var (fx, handler, t) = await ApprovalSetupAsync(new() { ["ToolBroker:AlwaysApprove:0"] = "" });
        await using var _ = fx;

        Assert.Equal(HttpStatusCode.OK, (await t.InvokeAsync(BrokerTools.SandboxRun, RunArgs)).StatusCode);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Approval_creation_rejects_a_malformed_hash()
    {
        var (fx, _, t) = await ApprovalSetupAsync();
        await using var __ = fx;
        using var c = fx.AgentClient(t.Token);

        var resp = await c.PostAsJsonAsync("/agent/approvals",
            new CreateApprovalRequest(t.Corr, t.Thread, "c", BrokerTools.SandboxRun, "run", RiskLevels.High, "r", "NOT-A-HASH"),
            SharpClawJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
