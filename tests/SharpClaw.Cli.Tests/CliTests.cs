using System.Runtime.CompilerServices;
using SharpClaw.Cli;
using SharpClaw.Contracts;

namespace SharpClaw.Cli.Tests;

public class CliTests
{
    [Fact]
    public void ArgumentParser_splits_positionals_and_options()
    {
        var parsed = ParsedArgs.Parse(new[]
        {
            "agents", "create", "assistant", "--model", "a/b", "--daily-budget", "1.5", "--enabled",
        });

        Assert.Equal(new[] { "agents", "create", "assistant" }, parsed.Positionals);
        Assert.Equal("a/b", parsed.Get("model"));
        Assert.Equal(1.5, parsed.GetDouble("daily-budget"));
        Assert.True(parsed.GetBool("enabled"));
    }

    [Fact]
    public async Task Create_agent_prints_token_once()
    {
        var gw = new FakeGateway();
        var output = new StringWriter();
        await Commands.RunAsync(
            new[] { "agents", "create", "assistant", "--model", "m", "--daily-budget", "1" },
            gw, output, TextReader.Null, CancellationToken.None);

        var text = output.ToString();
        Assert.Contains("sc_fake-token", text);
        Assert.Contains("shown once", text);
    }

    [Fact]
    public async Task Chat_opens_stream_before_posting_message()
    {
        var gw = new FakeGateway();
        var output = new StringWriter();

        await Commands.RunAsync(
            new[] { "ask", "assistant", "hello there" },
            gw, output, TextReader.Null, CancellationToken.None);

        Assert.True(gw.StreamOpenedAt < gw.MessagePostedAt,
            "the SSE stream must be opened before the message is posted (no replay).");
        Assert.Contains("Hello back", output.ToString());
    }

    private static CliSseEvent ApprovalEvent(Guid corr, Guid approvalId) => new(SseEventNames.ApprovalRequested,
        System.Text.Json.JsonSerializer.Serialize(new ApprovalRequestedEvent(approvalId, corr, "workspace_delete",
            "delete notes/old.md", "high", "deletes a file", DateTime.UtcNow.AddMinutes(5)), SharpClawJson.Options));

    [Theory]
    [InlineData("y", "approved")]
    [InlineData("YES", "approved")]
    [InlineData("n", "denied")]
    [InlineData("", "denied")]
    public async Task Approval_prompt_posts_the_users_decision(string answer, string expected)
    {
        var approvalId = Guid.NewGuid();
        var gw = new FakeGateway { BeforeCompletion = corr => new[] { ApprovalEvent(corr, approvalId) } };
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "ask", "assistant", "tidy up" }, gw, output, new StringReader(answer + "\n"), CancellationToken.None);

        Assert.Equal((approvalId, expected), Assert.Single(gw.Decisions));
        var text = output.ToString();
        Assert.Contains("workspace_delete (high risk): delete notes/old.md", text);
        Assert.Contains("Allow? [y/N]", text);
        Assert.Contains("Hello back", text);
    }

    [Fact]
    public async Task Approvals_for_other_messages_are_ignored()
    {
        var gw = new FakeGateway { BeforeCompletion = _ => new[] { ApprovalEvent(Guid.NewGuid(), Guid.NewGuid()) } };

        await Commands.RunAsync(new[] { "ask", "assistant", "hi" }, gw, new StringWriter(), new StringReader("y\n"), CancellationToken.None);

        Assert.Empty(gw.Decisions);
    }

    [Fact]
    public async Task Expired_approval_is_reported_and_the_chat_continues()
    {
        var gw = new FakeGateway
        {
            BeforeCompletion = corr => new[] { ApprovalEvent(corr, Guid.NewGuid()) },
            DecisionConflicts = true,
        };
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "ask", "assistant", "hi" }, gw, output, new StringReader("y\n"), CancellationToken.None);

        Assert.Contains("approval expired", output.ToString());
        Assert.Contains("Hello back", output.ToString());
    }

    [Fact]
    public async Task Tool_requests_are_marked_without_arguments()
    {
        var gw = new FakeGateway
        {
            BeforeCompletion = corr => new[]
            {
                new CliSseEvent(SseEventNames.ToolRequested, System.Text.Json.JsonSerializer.Serialize(
                    new ToolRequestedEvent(corr, "call_1", "workspace_read"), SharpClawJson.Options)),
            },
        };
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "ask", "assistant", "hi" }, gw, output, TextReader.Null, CancellationToken.None);

        Assert.Contains("[tool] workspace_read", output.ToString());
    }

    [Fact]
    public async Task Audit_tools_prints_the_tool_call_table()
    {
        var gw = new FakeGateway();
        gw.ToolCalls.Add(new ToolCallDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "call_1", "workspace_delete", "{}", "denied", "denied", "high", true, Guid.NewGuid(), "denied_by_user", 12,
            DateTime.UtcNow, DateTime.UtcNow));
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "audit", "tools", "--agent", "assistant", "--limit", "5" }, gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal(("assistant", 5), gw.LastToolAuditQuery);
        var text = output.ToString();
        Assert.Contains("TOOL", text);
        Assert.Contains("workspace_delete", text);
        Assert.Contains("high", text);
        Assert.Contains("yes", text);
    }

    // ---- fake gateway ----

    private sealed class FakeGateway : ICliGateway
    {
        private long _seq;
        public long StreamOpenedAt { get; private set; }
        public long MessagePostedAt { get; private set; }
        private Guid _postedMessageId;

        public Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget)
        {
            var agent = new AgentDto(Guid.NewGuid(), name, model, budget, true, DateTime.UtcNow, 0, budget);
            var token = new TokenValueDto(Guid.NewGuid(), "sc_fake-token", "sc_fake-", null);
            return Task.FromResult(new CreatedAgentResponse(agent, token));
        }

        public Task<AgentListResponse> ListAgentsAsync() =>
            Task.FromResult(new AgentListResponse(Array.Empty<AgentDto>()));

        public Task<AgentDto> GetAgentAsync(string idOrName) =>
            Task.FromResult(new AgentDto(Guid.NewGuid(), idOrName, "m", 1, true, DateTime.UtcNow, 0, 1));

        public Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled) =>
            Task.FromResult(new AgentDto(Guid.NewGuid(), idOrName, model ?? "m", budget ?? 1, enabled ?? true, DateTime.UtcNow, 0, 1));

        public Task<CreatedTokenResponse> IssueTokenAsync(string agent, DateTime? expiresAt) =>
            Task.FromResult(new CreatedTokenResponse(new TokenValueDto(Guid.NewGuid(), "sc_fake-token", "sc_fake-", expiresAt)));

        public Task<TokenListResponse> ListTokensAsync(string agent) =>
            Task.FromResult(new TokenListResponse(Array.Empty<TokenMetaDto>()));

        public Task RevokeTokenAsync(string agent, string tokenId) => Task.CompletedTask;

        public Task<ThreadDto> CreateThreadAsync(string agentId, string? title) =>
            Task.FromResult(new ThreadDto(Guid.NewGuid(), Guid.NewGuid(), title, DateTime.UtcNow));

        public Task<IThreadStream> OpenThreadStreamAsync(Guid threadId, CancellationToken ct)
        {
            StreamOpenedAt = Interlocked.Increment(ref _seq);
            return Task.FromResult<IThreadStream>(new FakeStream(this));
        }

        public Task<Guid> PostMessageAsync(Guid threadId, string content)
        {
            MessagePostedAt = Interlocked.Increment(ref _seq);
            _postedMessageId = Guid.NewGuid();
            return Task.FromResult(_postedMessageId);
        }

        public Task<AuditResponse> AuditAsync(string? agent, int? limit) =>
            Task.FromResult(new AuditResponse(Array.Empty<LlmCallDto>()));

        public List<ToolCallDto> ToolCalls { get; } = new();
        public (string? Agent, int? Limit) LastToolAuditQuery { get; private set; }

        public Task<ToolCallAuditResponse> AuditToolCallsAsync(string? agent, int? limit)
        {
            LastToolAuditQuery = (agent, limit);
            return Task.FromResult(new ToolCallAuditResponse(ToolCalls));
        }

        /// <summary>Extra events emitted (for the posted message) before the reply completes.</summary>
        public Func<Guid, IEnumerable<CliSseEvent>> BeforeCompletion { get; set; } = _ => Array.Empty<CliSseEvent>();
        public List<(Guid Id, string Decision)> Decisions { get; } = new();
        public bool DecisionConflicts { get; set; }

        public Task<ApprovalDto> DecideApprovalAsync(Guid approvalId, string decision)
        {
            if (DecisionConflicts)
                throw new CliException("Approval is already expired.");
            Decisions.Add((approvalId, decision));
            return Task.FromResult(new ApprovalDto(approvalId, decision, "user", DateTime.UtcNow));
        }

        private sealed class FakeStream : IThreadStream
        {
            private readonly FakeGateway _gw;
            public FakeStream(FakeGateway gw) => _gw = gw;

            public async IAsyncEnumerable<CliSseEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
            {
                // Wait until a message has been posted, then emit events for it.
                while (_gw._postedMessageId == Guid.Empty)
                    await Task.Delay(5, ct);

                var corr = _gw._postedMessageId;
                foreach (var extra in _gw.BeforeCompletion(corr))
                    yield return extra;
                yield return new CliSseEvent(SseEventNames.Delta,
                    System.Text.Json.JsonSerializer.Serialize(new DeltaEvent(corr, "Hello back"), SharpClawJson.Options));
                yield return new CliSseEvent(SseEventNames.MessageCompleted,
                    System.Text.Json.JsonSerializer.Serialize(new MessageCompletedEvent(corr, Guid.NewGuid(), "Hello back"), SharpClawJson.Options));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
