using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>Standing grants (3.6 spec sections 5–7): approve and remember, scope, expiry, revoke.</summary>
public partial class ApprovalTests
{
    private static CreateApprovalRequest NextRequest(Setup s, string callId, string tool = "workspace_delete") =>
        new(s.Corr, s.Thread, callId, tool, "a call", RiskLevels.High, "a reason");

    private static async Task<HttpResponseMessage> RememberAsync(Setup s, Guid id, string scope, int? ttlHours = null,
        string decision = ApprovalStatuses.Approved)
    {
        using var admin = s.Fx.Admin();
        return await admin.PostAsJsonAsync($"/api/approvals/{id}",
            new DecideApprovalRequest(decision, new ApprovalRememberRequest(scope, ttlHours)), SharpClawJson.Options);
    }

    private static async Task<List<ApprovalGrantDto>> GrantsAsync(Setup s)
    {
        using var admin = s.Fx.Admin();
        return (await admin.GetFromJsonAsync<ApprovalGrantListResponse>($"/api/agents/{s.Agent.Name}/grants", SharpClawJson.Options))!
            .Grants.ToList();
    }

    private static async Task AddGrantAsync(Setup s, string tool, Guid? thread)
    {
        await using var db = s.Fx.NewDb();
        db.ApprovalGrants.Add(new ApprovalGrant
        {
            Id = Guid.CreateVersion7(), AgentId = s.Agent.Id, ToolName = tool, ThreadId = thread,
            CreatedAt = s.Fx.Time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Approve_and_remember_for_the_thread_approves_later_calls_without_asking()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, first) = await CreateAsync(s, Request(s));
        (await RememberAsync(s, first!.Id, ApprovalGrantScopes.Thread)).EnsureSuccessStatusCode();
        var grant = Assert.Single(await GrantsAsync(s));
        var (client, events) = await WatchAsync(s, 1);
        using var __ = client;

        var (status, second) = await CreateAsync(s, NextRequest(s, "call_2"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal((ApprovalStatuses.Approved, ApprovalDecidedBy.Grant, grant.Id), (second!.Status, second.DecidedBy, second.GrantId));
        Assert.Equal(("workspace_delete", ApprovalGrantScopes.Thread, s.Thread, "admin_key"),
            (grant.ToolName, grant.Scope, grant.ThreadId, grant.CreatedByUser));
        // Only the decision is published: no approval card for the human.
        var e = Assert.Single(await events);
        Assert.Equal(SseEventNames.ApprovalDecided, e.Event);
        Assert.Contains("\"decidedBy\":\"grant\"", e.Data);
    }

    [Fact]
    public async Task A_grant_covers_only_its_tool_and_thread_but_an_agent_grant_covers_every_thread()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        await AddGrantAsync(s, "workspace_delete", Guid.CreateVersion7()); // another thread
        await AddGrantAsync(s, "workspace_move", null);

        var (_, otherThread) = await CreateAsync(s, NextRequest(s, "call_1"));
        var (_, agentWide) = await CreateAsync(s, NextRequest(s, "call_2", "workspace_move"));
        var (_, otherTool) = await CreateAsync(s, NextRequest(s, "call_3", "workspace_write"));

        Assert.Equal(ApprovalStatuses.Pending, otherThread!.Status);
        Assert.Equal(ApprovalDecidedBy.Grant, agentWide!.DecidedBy);
        Assert.Equal(ApprovalStatuses.Pending, otherTool!.Status);
    }

    [Fact]
    public async Task A_timed_grant_expires()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, first) = await CreateAsync(s, Request(s));
        (await RememberAsync(s, first!.Id, ApprovalGrantScopes.Agent, ttlHours: 1)).EnsureSuccessStatusCode();

        var (_, within) = await CreateAsync(s, NextRequest(s, "call_2"));
        s.Fx.Time.Advance(TimeSpan.FromHours(1));
        var (_, after) = await CreateAsync(s, NextRequest(s, "call_3"));

        Assert.Equal(ApprovalDecidedBy.Grant, within!.DecidedBy);
        Assert.Equal(ApprovalStatuses.Pending, after!.Status);
        Assert.Empty(await GrantsAsync(s));
    }

    [Fact]
    public async Task A_revoked_grant_stops_applying_and_cannot_be_revoked_twice()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, first) = await CreateAsync(s, Request(s));
        (await RememberAsync(s, first!.Id, ApprovalGrantScopes.Agent)).EnsureSuccessStatusCode();
        var grant = Assert.Single(await GrantsAsync(s));
        using var admin = s.Fx.Admin();

        var revoked = await admin.DeleteAsync($"/api/agents/{s.Agent.Name}/grants/{grant.Id}");
        var again = await admin.DeleteAsync($"/api/agents/{s.Agent.Name}/grants/{grant.Id}");
        var (_, next) = await CreateAsync(s, NextRequest(s, "call_2"));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(ApprovalStatuses.Pending, next!.Status);
        Assert.Empty(await GrantsAsync(s));
    }

    [Fact]
    public async Task Another_agents_grant_id_is_not_found()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, first) = await CreateAsync(s, Request(s));
        (await RememberAsync(s, first!.Id, ApprovalGrantScopes.Agent)).EnsureSuccessStatusCode();
        var grant = Assert.Single(await GrantsAsync(s));
        await s.Fx.CreateAgentAsync("other");
        using var admin = s.Fx.Admin();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/agents/other/grants/{grant.Id}")).StatusCode);
        Assert.Single(await GrantsAsync(s));
    }

    [Theory]
    [InlineData(ApprovalGrantScopes.Thread, null, ApprovalStatuses.Denied)]
    [InlineData("forever", null, ApprovalStatuses.Approved)]
    [InlineData(ApprovalGrantScopes.Agent, 2, ApprovalStatuses.Approved)]
    public async Task Invalid_remember_requests_are_refused_and_decide_nothing(string scope, int? ttl, string decision)
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));

        var resp = await RememberAsync(s, approval!.Id, scope, ttl, decision);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApprovalStatuses.Pending, (await PollAsync(s, approval.Id)).Status);
        Assert.Empty(await GrantsAsync(s));
    }

    [Theory]
    [InlineData(BrokerTools.GitHubWrite)]
    [InlineData(BrokerTools.GitHubCreatePr)]
    public async Task Tools_that_act_as_the_user_cannot_be_granted(string tool)
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        // Inserted directly: creating a broker approval needs the tool configured, which isn't the point here.
        var id = Guid.CreateVersion7();
        await using (var db = s.Fx.NewDb())
        {
            var now = s.Fx.Time.GetUtcNow().UtcDateTime;
            db.Approvals.Add(new Approval
            {
                Id = id, AgentId = s.Agent.Id, ThreadId = s.Thread, CorrelationId = s.Corr, ToolCallId = "c", ToolName = tool,
                Summary = "x", Risk = RiskLevels.High, Reason = "x", Status = ApprovalStatuses.Pending,
                CreatedAt = now, ExpiresAt = now.AddMinutes(5),
            });
            await db.SaveChangesAsync();
        }
        // Even a grant that somehow exists is ignored for these tools.
        await AddGrantAsync(s, tool, null);

        var resp = await RememberAsync(s, id, ApprovalGrantScopes.Agent);
        var (_, next) = await CreateAsync(s, NextRequest(s, "call_2", tool));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(ApprovalStatuses.Pending, next?.Status ?? ApprovalStatuses.Pending);
        await using var check = s.Fx.NewDb();
        Assert.DoesNotContain(await check.Approvals.AsNoTracking().ToListAsync(), a => a.DecidedBy == ApprovalDecidedBy.Grant);
    }

    [Fact]
    public async Task A_decision_that_loses_creates_no_grant()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, approval) = await CreateAsync(s, Request(s));
        (await DecideAsync(s, approval!.Id, ApprovalStatuses.Denied)).EnsureSuccessStatusCode();

        var resp = await RememberAsync(s, approval.Id, ApprovalGrantScopes.Agent);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Empty(await GrantsAsync(s));
    }

    [Fact]
    public async Task Grants_also_cover_unattended_messages()
    {
        var fx = await GatewayFixture.CreateAsync();
        await using var _ = fx;
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello", unattended: true);
        var s = new Setup(fx, agent, token, thread, (await fx.ClaimAsync(token))!.Id);
        await AddGrantAsync(s, "workspace_delete", thread);

        var (_, granted) = await CreateAsync(s, Request(s));
        var (_, other) = await CreateAsync(s, NextRequest(s, "call_2", "workspace_move"));

        Assert.Equal((ApprovalStatuses.Approved, ApprovalDecidedBy.Grant), (granted!.Status, granted.DecidedBy));
        Assert.Equal((ApprovalStatuses.Denied, ApprovalDecidedBy.System), (other!.Status, other.DecidedBy));
    }
}
