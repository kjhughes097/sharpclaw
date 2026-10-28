using System.Runtime.CompilerServices;
using SharpClaw.Cli;
using SharpClaw.Contracts;

namespace SharpClaw.Cli.Tests;

/// <summary>Interactive chat: stream-before-post, approval prompts, tool markers and reconnects.</summary>
public partial class CliTests
{
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
            "delete notes/old.md", "high", "deletes a file", TestClock.Now.AddMinutes(5)), SharpClawJson.Options));

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
        gw.Messages.Add(new MessageDto(Guid.NewGuid(), Roles.Assistant, "Hello", MessageStatus.Done, Guid.Empty, TestClock.Now));
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
}
