using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SharpClaw.Agent;
using SharpClaw.Contracts;

namespace SharpClaw.Agent.Tests;

public class AgentLoopTests
{
    private static AgentConfig Config() => new()
    {
        GatewayUrl = "http://gateway:8081",
        AgentToken = "sc_test",
        SoulPath = "/nonexistent/soul.md",
        LongPollSeconds = 5,
    };

    private static AgentLoop NewLoop(
        IGatewayClient client, ISoulProvider? soul = null, ILogger<AgentLoop>? logger = null) =>
        new(client, soul ?? new FixedSoul("SOUL"), Config(),
            logger ?? NullLogger<AgentLoop>.Instance, new FakeTimeProvider());

    [Fact]
    public async Task Happy_path_posts_reply()
    {
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(Guid.NewGuid(), Guid.NewGuid(), "hello", TestClock.Now)),
            NextChat = ChatOutcome.Ok("the reply"),
        };
        var loop = NewLoop(client);

        var outcome = await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(LoopOutcome.Processed, outcome);
        Assert.Single(client.Replies);
        Assert.Equal("the reply", client.Replies[0].Content);
        Assert.Empty(client.Failures);
    }

    [Fact]
    public async Task Empty_inbox_returns_empty()
    {
        var client = new FakeGatewayClient { NextInbox = InboxResult.Empty };
        var loop = NewLoop(client);
        Assert.Equal(LoopOutcome.Empty, await loop.ProcessOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Budget_exceeded_calls_fail()
    {
        var msgId = Guid.NewGuid();
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(msgId, Guid.NewGuid(), "hi", TestClock.Now)),
            NextChat = ChatOutcome.Fail("budget_exceeded"),
        };
        var loop = NewLoop(client);

        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(client.Failures);
        Assert.Equal((msgId, "budget_exceeded"), client.Failures[0]);
        Assert.Empty(client.Replies);
    }

    [Fact]
    public async Task Provider_error_calls_fail()
    {
        var msgId = Guid.NewGuid();
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(msgId, Guid.NewGuid(), "hi", TestClock.Now)),
            NextChat = ChatOutcome.Fail("provider_error"),
        };
        var loop = NewLoop(client);

        await loop.ProcessOnceAsync(CancellationToken.None);
        Assert.Equal((msgId, "provider_error"), client.Failures[0]);
    }

    [Fact]
    public async Task Unauthorized_exits_with_code_1()
    {
        var client = new FakeGatewayClient { NextInbox = InboxResult.Unauthorized };
        var loop = NewLoop(client);
        var code = await loop.RunAsync(CancellationToken.None);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Transient_error_backs_off()
    {
        var client = new FakeGatewayClient { NextInbox = InboxResult.Transient };
        var loop = NewLoop(client);
        Assert.Equal(LoopOutcome.Transient, await loop.ProcessOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_agent_backs_off()
    {
        var client = new FakeGatewayClient { NextInbox = InboxResult.Disabled };
        var loop = NewLoop(client);
        Assert.Equal(LoopOutcome.Disabled, await loop.ProcessOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Soul_is_reread_each_turn()
    {
        var soul = new CountingSoul();
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(Guid.NewGuid(), Guid.NewGuid(), "hi", TestClock.Now)),
            NextChat = ChatOutcome.Ok("r"),
        };
        var loop = NewLoop(client, soul);

        await loop.ProcessOnceAsync(CancellationToken.None);
        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.Equal(2, soul.ReadCount);
    }

    [Fact]
    public async Task System_prompt_prepended_to_history()
    {
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(Guid.NewGuid(), Guid.NewGuid(), "hi", TestClock.Now)),
            History = new List<ChatMessage> { new(Roles.User, "hi") },
            NextChat = ChatOutcome.Ok("r"),
        };
        var loop = NewLoop(client, new FixedSoul("MY SOUL"));

        await loop.ProcessOnceAsync(CancellationToken.None);

        Assert.NotNull(client.LastChatMessages);
        Assert.Equal(Roles.System, client.LastChatMessages![0].Role);
        Assert.Equal("MY SOUL", client.LastChatMessages[0].Content);
        Assert.Equal(Roles.User, client.LastChatMessages[1].Role);
    }

    [Fact]
    public async Task Context_too_large_fails_the_message_without_calling_the_model()
    {
        var msgId = Guid.NewGuid();
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(msgId, Guid.NewGuid(), "hi", TestClock.Now)),
            ContextFailCode = "context_too_large",
        };

        await NewLoop(client).ProcessOnceAsync(CancellationToken.None);

        Assert.Equal((msgId, "context_too_large"), Assert.Single(client.Failures));
        Assert.Null(client.LastChatMessages);
    }

    [Fact]
    public async Task Chat_context_too_large_fails_the_message()
    {
        var msgId = Guid.NewGuid();
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(msgId, Guid.NewGuid(), "hi", TestClock.Now)),
            NextChat = ChatOutcome.Fail("context_too_large"),
        };

        await NewLoop(client).ProcessOnceAsync(CancellationToken.None);

        Assert.Equal((msgId, "context_too_large"), Assert.Single(client.Failures));
    }

    [Fact]
    public async Task Reserve_tokens_cover_the_soul()
    {
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(Guid.NewGuid(), Guid.NewGuid(), "hi", TestClock.Now)),
            NextChat = ChatOutcome.Ok("r"),
        };

        await NewLoop(client, new FixedSoul(new string('s', 4001))).ProcessOnceAsync(CancellationToken.None);

        // ceil(4001 / 4) for the soul, plus the per-message overhead; no tools.
        Assert.Equal(1005, client.LastReserveTokens);
    }

    [Fact]
    public async Task Logs_do_not_contain_message_content()
    {
        var logs = new ConcurrentQueue<string>();
        var logger = new CapturingLogger<AgentLoop>(logs);
        var client = new FakeGatewayClient
        {
            NextInbox = InboxResult.Of(new InboxMessageDto(Guid.NewGuid(), Guid.NewGuid(), "SENSITIVE", TestClock.Now)),
            History = new List<ChatMessage> { new(Roles.User, "SENSITIVE") },
            NextChat = ChatOutcome.Ok("SECRET_REPLY"),
        };
        var loop = NewLoop(client, new FixedSoul("SOULTEXT"), logger);

        await loop.ProcessOnceAsync(CancellationToken.None);

        var all = string.Join("\n", logs);
        Assert.DoesNotContain("SENSITIVE", all);
        Assert.DoesNotContain("SECRET_REPLY", all);
        Assert.DoesNotContain("SOULTEXT", all);
    }

    // ---- fakes ----

    private sealed class FakeGatewayClient : IGatewayClient
    {
        public InboxResult NextInbox { get; set; } = InboxResult.Empty;
        public IReadOnlyList<ChatMessage> History { get; set; } = Array.Empty<ChatMessage>();
        public ChatOutcome NextChat { get; set; } = ChatOutcome.Ok("");
        public List<(Guid ThreadId, string Content, Guid Correlation)> Replies { get; } = new();
        public List<(Guid MessageId, string Reason)> Failures { get; } = new();
        public IReadOnlyList<ChatMessage>? LastChatMessages { get; private set; }

        public Task<InboxResult> PollInboxAsync(int waitSeconds, CancellationToken ct) => Task.FromResult(NextInbox);
        public string? ContextFailCode { get; set; }
        public int? LastReserveTokens { get; private set; }
        public Task<ContextResult> GetContextAsync(Guid threadId, int reserveTokens, CancellationToken ct)
        {
            LastReserveTokens = reserveTokens;
            return Task.FromResult(ContextFailCode is null ? new ContextResult(History) : ContextResult.Fail(ContextFailCode));
        }
        public Task<ChatOutcome> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<SharpClaw.Agent.Tools.IAgentTool> tools,
            Guid correlationId, Guid threadId, CancellationToken ct)
        {
            LastChatMessages = messages;
            return Task.FromResult(NextChat);
        }
        public Task PostReplyAsync(Guid threadId, string content, Guid correlationId,
            IReadOnlyList<ToolExchangeDto> exchanges, CancellationToken ct)
        {
            Replies.Add((threadId, content, correlationId));
            return Task.CompletedTask;
        }
        public Task FailAsync(Guid messageId, string reason, CancellationToken ct)
        {
            Failures.Add((messageId, reason));
            return Task.CompletedTask;
        }

        public Task<ApprovalDto> RequestApprovalAsync(CreateApprovalRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ApprovalDto> PollApprovalAsync(Guid approvalId, int waitSeconds, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ReportToolCallAsync(ToolCallReportRequest report, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> ClassifyRiskAsync(
            IReadOnlyList<ChatMessage> messages, Guid correlationId, Guid threadId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FixedSoul : ISoulProvider
    {
        private readonly string _text;
        public FixedSoul(string text) => _text = text;
        public string Read() => _text;
    }

    private sealed class CountingSoul : ISoulProvider
    {
        public int ReadCount { get; private set; }
        public string Read() { ReadCount++; return "soul"; }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _logs;
        public CapturingLogger(ConcurrentQueue<string> logs) => _logs = logs;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _logs.Enqueue(formatter(state, exception));
    }
}
