using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Agent;
using SharpClaw.Agent.Tools;
using SharpClaw.Agent.Tools.Workspace;
using SharpClaw.Contracts;
using SharpClaw.Gateway.Data;

namespace SharpClaw.Gateway.Tests;

/// <summary>
/// The real agent (HTTP client, loop, dispatcher, workspace tools) against the real gateway with a
/// scripted FakeProvider: the 3.2 exit criteria end to end.
/// </summary>
public class AgentToolEndToEndTests : IDisposable
{
    private const string Script = """
    { "rules": [
      { "match": "^make a note$", "role": "user",
        "toolCalls": [ { "name": "workspace_write", "arguments": { "path": "notes/todo.md", "content": "buy milk" } } ] },
      { "match": "\"created\":true", "role": "tool",
        "toolCalls": [ { "name": "workspace_read", "arguments": { "path": "notes/todo.md" } } ] },
      { "match": "buy milk", "role": "tool", "response": "Your note says: buy milk" },

      { "match": "^delete the old note$", "role": "user",
        "toolCalls": [ { "name": "workspace_delete", "arguments": { "path": "old.md" } } ] },
      { "match": "\"deleted\":true", "role": "tool", "response": "Deleted old.md." },
      { "match": "denied_by_user", "role": "tool", "response": "OK, I left it alone." },
      { "match": "no_approver", "role": "tool", "response": "Nobody could approve that." },

      { "match": "^escape$", "role": "user",
        "toolCalls": [ { "name": "workspace_read", "arguments": { "path": "../../etc/passwd" } } ] },
      { "match": "\"error\":\"blocked\"", "role": "tool", "response": "That path is not allowed." }
    ] }
    """;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"e2e-ws-{Guid.NewGuid():N}");

    public AgentToolEndToEndTests() => Directory.CreateDirectory(_workspace);

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private sealed record Harness(GatewayFixture Fx, AgentDto Agent, Guid Thread, AgentLoop Loop, HttpClient Http) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Fx.DisposeAsync();
        }
    }

    private async Task<Harness> StartAsync()
    {
        var fx = await GatewayFixture.CreateAsync(extraConfig: new Dictionary<string, string?>
        {
            ["Fake:ScriptPath"] = ChatTestHelpers.WriteScript(Script),
            // Short, so a broken approval flow fails fast instead of waiting the default 300 s.
            ["Approvals:TimeoutSeconds"] = "20",
        });
        var (agent, token) = await fx.CreateAgentAsync(budget: 10);
        var thread = await fx.CreateThreadAsync(agent.Id);

        var config = new AgentConfig
        {
            GatewayUrl = fx.AgentBaseUrl,
            AgentToken = token,
            SoulPath = "/nonexistent",
            WorkspacePath = _workspace,
            LongPollSeconds = 1,
        };
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var gateway = new GatewayHttpClient(http, config);
        var toolConfig = new AgentToolConfig(RiskLevel.Low, SmartMode: false, MaxToolIterations: 5, WorkspaceTools.Names.ToList());
        var registry = new ToolRegistry(WorkspaceTools.All(_workspace), toolConfig.Tools);
        var dispatcher = new ToolDispatcher(registry, toolConfig, gateway, NullLogger<ToolDispatcher>.Instance);
        var loop = new AgentLoop(gateway, new NoSoul(), config, NullLogger<AgentLoop>.Instance, TimeProvider.System,
            new AgentTools(registry, dispatcher, toolConfig.MaxToolIterations));
        return new Harness(fx, agent, thread, loop, http);
    }

    private sealed class NoSoul : ISoulProvider
    {
        public string Read() => "You are a test agent.";
    }

    private static async Task<string?> FinalReplyAsync(Harness h)
    {
        using var db = h.Fx.NewDb();
        return await db.Messages.AsNoTracking()
            .Where(m => m.ThreadId == h.Thread && m.Role == Roles.Assistant)
            .Select(m => m.Content).SingleOrDefaultAsync();
    }

    private static async Task<List<ToolCallRecord>> ToolCallsAsync(Harness h)
    {
        using var db = h.Fx.NewDb();
        return await db.ToolCalls.AsNoTracking().OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync();
    }

    [Fact]
    public async Task Multi_step_task_writes_reads_and_replies()
    {
        await using var h = await StartAsync();
        await h.Fx.PostUserMessageAsync(h.Thread, "make a note");

        Assert.Equal(LoopOutcome.Processed, await h.Loop.ProcessOnceAsync(CancellationToken.None));

        Assert.Equal("Your note says: buy milk", await FinalReplyAsync(h));
        Assert.Equal("buy milk", File.ReadAllText(Path.Combine(_workspace, "notes", "todo.md")));

        var calls = await ToolCallsAsync(h);
        Assert.Equal(new[] { "workspace_write", "workspace_read" }, calls.Select(c => c.ToolName));
        Assert.All(calls, c =>
        {
            Assert.Equal(ToolCallStatuses.Succeeded, c.Status);
            Assert.Equal(ToolDecisions.Allowed, c.Decision);
            Assert.NotNull(c.ReportedAt);
        });

        using var db = h.Fx.NewDb();
        Assert.Equal(3, await db.LlmCalls.CountAsync());
        Assert.Equal(MessageStatus.Done, (await db.Messages.AsNoTracking().SingleAsync(m => m.Role == Roles.User)).Status);
    }

    [Theory]
    [InlineData(true, "Deleted old.md.")]
    [InlineData(false, "OK, I left it alone.")]
    public async Task High_risk_call_waits_for_the_human(bool approve, string expectedReply)
    {
        await using var h = await StartAsync();
        File.WriteAllText(Path.Combine(_workspace, "old.md"), "old");

        // A human watching the thread answers the approval prompt (as the CLI does).
        using var watcher = h.Fx.Admin();
        var stream = await (await watcher.GetAsync($"/api/threads/{h.Thread}/stream", HttpCompletionOption.ResponseHeadersRead))
            .Content.ReadAsStreamAsync();
        var human = Task.Run(async () =>
        {
            // message.created, then tool.requested, then approval.requested.
            var events = await TestHelpers.ReadSseEventsAsync(stream, 3, TimeSpan.FromSeconds(10));
            var evt = events.Single(e => e.Event == SseEventNames.ApprovalRequested);
            var approvalId = JsonDocument.Parse(evt.Data).RootElement.GetProperty("approvalId").GetGuid();
            using var admin = h.Fx.Admin();
            await admin.PostAsJsonAsync($"/api/approvals/{approvalId}",
                new DecideApprovalRequest(approve ? ApprovalStatuses.Approved : ApprovalStatuses.Denied), SharpClawJson.Options);
            return evt.Data;
        });
        await Task.Delay(200);

        await h.Fx.PostUserMessageAsync(h.Thread, "delete the old note");
        await h.Loop.ProcessOnceAsync(CancellationToken.None);

        var eventData = await human;
        Assert.Contains("delete old.md", eventData);
        Assert.Contains("\"risk\":\"high\"", eventData);
        Assert.Equal(expectedReply, await FinalReplyAsync(h));
        Assert.Equal(!approve, File.Exists(Path.Combine(_workspace, "old.md")));

        var call = Assert.Single(await ToolCallsAsync(h));
        Assert.Equal(approve ? ToolDecisions.Approved : ToolDecisions.Denied, call.Decision);
        Assert.Equal(RiskLevels.High, call.Risk);
        Assert.NotNull(call.ApprovalId);
    }

    [Fact]
    public async Task Approval_waits_with_nobody_watching_and_can_be_approved_from_the_queue()
    {
        await using var h = await StartAsync();
        File.WriteAllText(Path.Combine(_workspace, "old.md"), "old");
        await h.Fx.PostUserMessageAsync(h.Thread, "delete the old note");

        var turn = Task.Run(() => h.Loop.ProcessOnceAsync(CancellationToken.None));
        using var admin = h.Fx.Admin();
        ApprovalDetailDto? pending = null;
        for (var i = 0; i < 50 && pending is null; i++)
        {
            await Task.Delay(100);
            var list = await admin.GetFromJsonAsync<ApprovalListResponse>("/api/approvals?status=pending", SharpClawJson.Options);
            pending = list!.Approvals.FirstOrDefault();
        }
        Assert.NotNull(pending);
        (await admin.PostAsJsonAsync($"/api/approvals/{pending.Id}",
            new DecideApprovalRequest(ApprovalStatuses.Approved), SharpClawJson.Options)).EnsureSuccessStatusCode();

        await turn;
        Assert.Equal("Deleted old.md.", await FinalReplyAsync(h));
        Assert.False(File.Exists(Path.Combine(_workspace, "old.md")));
    }

    [Fact]
    public async Task Unattended_high_risk_call_is_denied_without_waiting()
    {
        await using var h = await StartAsync();
        File.WriteAllText(Path.Combine(_workspace, "old.md"), "old");
        await h.Fx.PostUserMessageAsync(h.Thread, "delete the old note", unattended: true);

        var started = DateTime.UtcNow;
        await h.Loop.ProcessOnceAsync(CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal("Nobody could approve that.", await FinalReplyAsync(h));
        Assert.True(File.Exists(Path.Combine(_workspace, "old.md")));
        Assert.Equal(ToolDecisions.Denied, Assert.Single(await ToolCallsAsync(h)).Decision);

        using var db = h.Fx.NewDb();
        var approval = await db.Approvals.AsNoTracking().SingleAsync();
        Assert.Equal(ApprovalStatuses.Denied, approval.Status);
        Assert.Equal(ApprovalDecidedBy.System, approval.DecidedBy);
    }

    [Fact]
    public async Task Hardline_rules_block_escapes_and_are_audited()
    {
        await using var h = await StartAsync();
        await h.Fx.PostUserMessageAsync(h.Thread, "escape");

        await h.Loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal("That path is not allowed.", await FinalReplyAsync(h));
        var call = Assert.Single(await ToolCallsAsync(h));
        Assert.Equal(ToolCallStatuses.Blocked, call.Status);
        Assert.Equal(ToolDecisions.Blocked, call.Decision);
    }
}
