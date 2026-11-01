using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Telemetry;

namespace SharpClaw.Gateway.Tests;

/// <summary>Autonomy mode and batch decisions (3.6 spec Q7, Q9, sections 6–7).</summary>
public partial class ApprovalTests
{
    private static async Task<AgentDto> SetAutonomyAsync(Setup s, bool on)
    {
        using var admin = s.Fx.Admin();
        var resp = await admin.PatchAsJsonAsync($"/api/agents/{s.Agent.Name}", new UpdateAgentRequest(null, null, null, Autonomous: on),
            SharpClawJson.Options);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AgentDto>(SharpClawJson.Options))!;
    }

    private static async Task<(HttpStatusCode Status, BatchDecideApprovalsResponse? Body)> BatchAsync(
        Setup s, BatchDecideApprovalsRequest request)
    {
        using var admin = s.Fx.Admin();
        var resp = await admin.PostAsJsonAsync("/api/approvals/batch", request, SharpClawJson.Options);
        return (resp.StatusCode, resp.IsSuccessStatusCode
            ? await resp.Content.ReadFromJsonAsync<BatchDecideApprovalsResponse>(SharpClawJson.Options)
            : null);
    }

    [Fact]
    public async Task Autonomy_mode_approves_every_risk_until_switched_off()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        Assert.False(s.Agent.Autonomous);

        var dto = await SetAutonomyAsync(s, true);
        var (_, critical) = await CreateAsync(s, NextRequest(s, "call_1") with { Risk = RiskLevels.Critical });
        await SetAutonomyAsync(s, false);
        var (_, after) = await CreateAsync(s, NextRequest(s, "call_2"));

        Assert.True(dto.Autonomous);
        Assert.Equal((ApprovalStatuses.Approved, ApprovalDecidedBy.Autonomy), (critical!.Status, critical.DecidedBy));
        Assert.Null(critical.GrantId);
        Assert.Equal(ApprovalStatuses.Pending, after!.Status);
    }

    [Fact]
    public async Task Autonomy_mode_covers_unattended_messages_and_is_counted_by_type()
    {
        var fx = await GatewayFixture.CreateAsync();
        await using var _ = fx;
        var (agent, token) = await fx.CreateAgentAsync();
        var thread = await fx.CreateThreadAsync(agent.Id);
        await fx.PostUserMessageAsync(thread, "hello", unattended: true);
        var s = new Setup(fx, agent, token, thread, (await fx.ClaimAsync(token))!.Id);
        await SetAutonomyAsync(s, true);

        var observed = new ConcurrentBag<(string Name, Dictionary<string, string?> Tags)>();
        var meter = fx.App.Services.GetRequiredService<GatewayMetrics>().Meter;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, meter))
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, _, tags, _) =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var t in tags) dict[t.Key] = t.Value?.ToString();
            observed.Add((inst.Name, dict));
        });
        listener.Start();

        var (_, approval) = await CreateAsync(s, Request(s));

        Assert.Equal((ApprovalStatuses.Approved, ApprovalDecidedBy.Autonomy), (approval!.Status, approval.DecidedBy));
        Assert.Contains(observed, m => m.Name == "sharpclaw.approvals" && m.Tags["type"] == ApprovalDecidedBy.Autonomy
            && m.Tags["status"] == ApprovalStatuses.Approved);
    }

    [Fact]
    public async Task Batch_by_ids_decides_the_pending_ones_and_counts_the_rest_as_skipped()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        var (_, a) = await CreateAsync(s, NextRequest(s, "call_1"));
        var (_, b) = await CreateAsync(s, NextRequest(s, "call_2"));
        var (_, c) = await CreateAsync(s, NextRequest(s, "call_3"));
        (await DecideAsync(s, c!.Id, ApprovalStatuses.Denied)).EnsureSuccessStatusCode();

        var (status, body) = await BatchAsync(s, new BatchDecideApprovalsRequest(ApprovalStatuses.Approved,
            [a!.Id, b!.Id, c.Id, Guid.CreateVersion7()]));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { a.Id, b.Id }, body!.Decided.Select(d => d.Id));
        Assert.All(body.Decided, d => Assert.Equal((ApprovalStatuses.Approved, ApprovalDecidedBy.User), (d.Status, d.DecidedBy)));
        Assert.Equal(2, body.Skipped);
        Assert.Equal(ApprovalStatuses.Approved, (await PollAsync(s, a.Id)).Status);
    }

    [Fact]
    public async Task Batch_by_thread_decides_every_pending_approval_in_it()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;
        await CreateAsync(s, NextRequest(s, "call_1"));
        await CreateAsync(s, NextRequest(s, "call_2"));

        var (_, body) = await BatchAsync(s, new BatchDecideApprovalsRequest(ApprovalStatuses.Denied, ThreadId: s.Thread));

        Assert.Equal(2, body!.Decided.Count);
        Assert.All(body.Decided, d => Assert.Equal(ApprovalStatuses.Denied, d.Status));
        Assert.Equal(0, body.Skipped);
        Assert.Empty(await ListAsync(s, "?status=pending"));
    }

    [Fact]
    public async Task Batch_requests_are_validated()
    {
        var s = await SetupAsync();
        await using var _ = s.Fx;

        Assert.Equal(HttpStatusCode.BadRequest, (await BatchAsync(s, new BatchDecideApprovalsRequest(ApprovalStatuses.Approved))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await BatchAsync(s,
            new BatchDecideApprovalsRequest(ApprovalStatuses.Approved, [Guid.NewGuid()], s.Thread))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await BatchAsync(s, new BatchDecideApprovalsRequest(ApprovalStatuses.Approved, []))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await BatchAsync(s, new BatchDecideApprovalsRequest("maybe", ThreadId: s.Thread))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await BatchAsync(s,
            new BatchDecideApprovalsRequest(ApprovalStatuses.Approved, ThreadId: Guid.NewGuid()))).Status);
    }
}
