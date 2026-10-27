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

    // ---- reconnect (pre-3.3 §8.6) ----

    private static CliSseEvent Ev<T>(string name, T payload, long? id = null) =>
        new(name, System.Text.Json.JsonSerializer.Serialize(payload, SharpClawJson.Options), id);

    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Dropped_stream_resumes_with_the_last_event_id_and_prints_each_character_once()
    {
        var gw = new FakeGateway();
        gw.Streams.Enqueue(corr => new[]
        {
            Ev(SseEventNames.Ready, new ReadyEvent(null), 10),
            Ev(SseEventNames.Delta, new DeltaEvent(corr, "Hel")),
        });
        gw.Streams.Enqueue(null); // first reconnect attempt fails
        gw.Streams.Enqueue(corr => new[]
        {
            Ev(SseEventNames.Ready, new ReadyEvent(null), 11),
            Ev(SseEventNames.Snapshot, new SnapshotEvent(corr, "Hello")),
            Ev(SseEventNames.Delta, new DeltaEvent(corr, " world")),
            Ev(SseEventNames.MessageCompleted, new MessageCompletedEvent(corr, Guid.NewGuid(), "Hello world"), 12),
        });
        var output = new StringWriter();

        await ChatRunner.RunOnceAsync(gw, Guid.NewGuid(), "hi", output, TextReader.Null, CancellationToken.None, NoDelay);

        Assert.Equal("Hello world" + Environment.NewLine, output.ToString());
        Assert.Equal(new long?[] { null, 10, 10 }, gw.OpenedWithLastIds);
    }

    [Fact]
    public async Task Replayed_events_already_handled_are_ignored()
    {
        var gw = new FakeGateway();
        gw.Streams.Enqueue(corr => new[]
        {
            Ev(SseEventNames.ToolRequested, new ToolRequestedEvent(corr, "call_1", "workspace_read"), 5),
        });
        gw.Streams.Enqueue(corr => new[]
        {
            Ev(SseEventNames.ToolRequested, new ToolRequestedEvent(corr, "call_1", "workspace_read"), 5),
            Ev(SseEventNames.MessageCompleted, new MessageCompletedEvent(corr, Guid.NewGuid(), "done"), 6),
        });
        var output = new StringWriter();

        await ChatRunner.RunOnceAsync(gw, Guid.NewGuid(), "hi", output, TextReader.Null, CancellationToken.None, NoDelay);

        Assert.Single(output.ToString().Split("[tool] workspace_read").Skip(1));
        Assert.Contains("done", output.ToString());
    }

    [Fact]
    public async Task Reply_completed_while_disconnected_is_printed_from_the_replay()
    {
        var gw = new FakeGateway();
        gw.Streams.Enqueue(corr => new[] { Ev(SseEventNames.Delta, new DeltaEvent(corr, "Hel")) });
        gw.Streams.Enqueue(corr => new[]
        {
            Ev(SseEventNames.MessageCompleted, new MessageCompletedEvent(corr, Guid.NewGuid(), "Hello"), 3),
        });
        var output = new StringWriter();

        await ChatRunner.RunOnceAsync(gw, Guid.NewGuid(), "hi", output, TextReader.Null, CancellationToken.None, NoDelay);

        Assert.Equal("Hello" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Reset_reloads_history_and_finishes_if_the_reply_is_there()
    {
        var gw = new FakeGateway();
        gw.Messages.Add(new MessageDto(Guid.NewGuid(), Roles.Assistant, "Hello", MessageStatus.Done, Guid.Empty, DateTime.UtcNow));
        gw.Streams.Enqueue(corr => new[] { Ev(SseEventNames.Delta, new DeltaEvent(corr, "He")) });
        gw.Streams.Enqueue(_ => new[] { Ev(SseEventNames.Reset, new ResetEvent(ResetReasons.HistoryPruned)) });
        var output = new StringWriter();

        await ChatRunner.RunOnceAsync(gw, Guid.NewGuid(), "hi", output, TextReader.Null, CancellationToken.None, NoDelay);

        Assert.Equal("Hello" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Gives_up_after_a_minute_of_failed_reconnects()
    {
        var gw = new FakeGateway();
        gw.Streams.Enqueue(_ => Array.Empty<CliSseEvent>());
        for (var i = 0; i < 20; i++)
            gw.Streams.Enqueue(null);
        var waited = new List<TimeSpan>();

        var ex = await Assert.ThrowsAsync<CliException>(() => ChatRunner.RunOnceAsync(gw, Guid.NewGuid(), "hi",
            new StringWriter(), TextReader.Null, CancellationToken.None, (d, _) => { waited.Add(d); return Task.CompletedTask; }));

        Assert.Contains("Lost the connection", ex.Message);
        Assert.Equal(new[] { 1, 2, 4, 8, 10, 10, 10, 10, 10 }, waited.Select(w => (int)w.TotalSeconds));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("set")]
    public async Task Context_window_flag_is_passed_through(string verb)
    {
        var gw = new FakeGateway();
        var args = verb == "create"
            ? new[] { "agents", "create", "a", "--model", "m", "--daily-budget", "1", "--context-window", "128000" }
            : new[] { "agents", "set", "a", "--context-window", "128000" };

        await Commands.RunAsync(args, gw, new StringWriter(), TextReader.Null, CancellationToken.None);

        Assert.Equal(128000, gw.LastContextWindow);
    }

    // ---- approvals queue (pre-3.3 §9.6) ----

    [Fact]
    public async Task Approvals_list_passes_filters_and_prints_the_queue()
    {
        var gw = new FakeGateway();
        gw.Approvals.Add(new ApprovalDetailDto(Guid.NewGuid(), "assistant", Guid.NewGuid(), Guid.NewGuid(), "workspace_delete",
            "delete old.md", "high", "deletes a file", "pending", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5), null, null));
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "approvals", "list", "--status", "pending", "--agent", "assistant" },
            gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal(("pending", "assistant"), gw.LastApprovalQuery);
        Assert.Contains("workspace_delete", output.ToString());
        Assert.Contains("delete old.md", output.ToString());
    }

    [Theory]
    [InlineData("approve", "approved")]
    [InlineData("deny", "denied")]
    public async Task Approvals_approve_and_deny_post_the_decision(string verb, string expected)
    {
        var gw = new FakeGateway();
        var id = Guid.NewGuid();
        var output = new StringWriter();

        await Commands.RunAsync(new[] { "approvals", verb, id.ToString() }, gw, output, TextReader.Null, CancellationToken.None);

        Assert.Equal((id, expected), Assert.Single(gw.Decisions));
        Assert.Contains(expected, output.ToString());
    }

    [Fact]
    public async Task Ask_unattended_posts_the_flag()
    {
        var gw = new FakeGateway();
        await Commands.RunAsync(new[] { "ask", "assistant", "hi", "--unattended" }, gw, new StringWriter(), TextReader.Null, CancellationToken.None);
        Assert.True(gw.PostedUnattended);
    }

    // ---- fake gateway ----

    private sealed class FakeGateway : ICliGateway
    {
        private long _seq;
        public long StreamOpenedAt { get; private set; }
        public long MessagePostedAt { get; private set; }
        private Guid _postedMessageId;

        public int? LastContextWindow { get; private set; }

        public Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget, int? contextWindow = null)
        {
            LastContextWindow = contextWindow;
            var agent = new AgentDto(Guid.NewGuid(), name, model, budget, true, DateTime.UtcNow, 0, budget);
            var token = new TokenValueDto(Guid.NewGuid(), "sc_fake-token", "sc_fake-", null);
            return Task.FromResult(new CreatedAgentResponse(agent, token));
        }

        public Task<AgentListResponse> ListAgentsAsync() =>
            Task.FromResult(new AgentListResponse(Array.Empty<AgentDto>()));

        public Task<AgentDto> GetAgentAsync(string idOrName) =>
            Task.FromResult(new AgentDto(Guid.NewGuid(), idOrName, "m", 1, true, DateTime.UtcNow, 0, 1));

        public Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled, int? contextWindow = null)
        {
            LastContextWindow = contextWindow;
            return Task.FromResult(new AgentDto(Guid.NewGuid(), idOrName, model ?? "m", budget ?? 1, enabled ?? true, DateTime.UtcNow, 0, 1,
                contextWindow));
        }

        public Task<CreatedTokenResponse> IssueTokenAsync(string agent, DateTime? expiresAt) =>
            Task.FromResult(new CreatedTokenResponse(new TokenValueDto(Guid.NewGuid(), "sc_fake-token", "sc_fake-", expiresAt)));

        public Task<TokenListResponse> ListTokensAsync(string agent) =>
            Task.FromResult(new TokenListResponse(Array.Empty<TokenMetaDto>()));

        public Task RevokeTokenAsync(string agent, string tokenId) => Task.CompletedTask;

        public Task<ThreadDto> CreateThreadAsync(string agentId, string? title) =>
            Task.FromResult(new ThreadDto(Guid.NewGuid(), Guid.NewGuid(), title, DateTime.UtcNow));

        public Task<IThreadStream> OpenThreadStreamAsync(Guid threadId, long? lastEventId, CancellationToken ct)
        {
            StreamOpenedAt = Interlocked.Increment(ref _seq);
            OpenedWithLastIds.Add(lastEventId);
            if (Streams.Count > 0)
            {
                var script = Streams.Dequeue();
                if (script is null)
                    throw new HttpRequestException("gateway unreachable");
                return Task.FromResult<IThreadStream>(new ScriptedStream(this, script));
            }
            return Task.FromResult<IThreadStream>(new FakeStream(this));
        }

        /// <summary>
        /// Scripted streams for successive opens: events for the posted message id, after which the
        /// connection drops. A null entry means the open itself fails. Empty → the default stream.
        /// </summary>
        public Queue<Func<Guid, IEnumerable<CliSseEvent>>?> Streams { get; } = new();
        public List<long?> OpenedWithLastIds { get; } = new();
        public List<MessageDto> Messages { get; } = new();

        public Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid threadId) =>
            Task.FromResult<IReadOnlyList<MessageDto>>(Messages.Select(m => m.CorrelationId == Guid.Empty
                ? m with { CorrelationId = _postedMessageId } : m).ToList());

        private sealed class ScriptedStream(FakeGateway gw, Func<Guid, IEnumerable<CliSseEvent>> script) : IThreadStream
        {
            public async IAsyncEnumerable<CliSseEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
            {
                while (gw._postedMessageId == Guid.Empty)
                    await Task.Delay(5, ct);
                foreach (var ev in script(gw._postedMessageId))
                    yield return ev;
                throw new IOException("connection dropped");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public Task<Guid> PostMessageAsync(Guid threadId, string content, bool unattended = false)
        {
            MessagePostedAt = Interlocked.Increment(ref _seq);
            PostedUnattended = unattended;
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
        public bool PostedUnattended { get; private set; }
        public List<ApprovalDetailDto> Approvals { get; } = new();
        public (string? Status, string? Agent) LastApprovalQuery { get; private set; }

        public Task<ApprovalListResponse> ListApprovalsAsync(string? status, string? agent)
        {
            LastApprovalQuery = (status, agent);
            return Task.FromResult(new ApprovalListResponse(Approvals));
        }

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
