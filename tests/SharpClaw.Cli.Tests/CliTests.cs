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
                yield return new CliSseEvent(SseEventNames.Delta,
                    System.Text.Json.JsonSerializer.Serialize(new DeltaEvent(corr, "Hello back"), SharpClawJson.Options));
                yield return new CliSseEvent(SseEventNames.MessageCompleted,
                    System.Text.Json.JsonSerializer.Serialize(new MessageCompletedEvent(corr, Guid.NewGuid(), "Hello back"), SharpClawJson.Options));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
