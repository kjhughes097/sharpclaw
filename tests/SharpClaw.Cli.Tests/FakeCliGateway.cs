using System.Runtime.CompilerServices;
using SharpClaw.Cli;
using SharpClaw.Contracts;

namespace SharpClaw.Cli.Tests;

public partial class CliTests
{
    /// <summary>An in-memory <see cref="ICliGateway"/> that records calls and scripts the SSE stream.</summary>
    private sealed class FakeGateway : ICliGateway
    {
        private long _seq;
        public long StreamOpenedAt { get; private set; }
        public long MessagePostedAt { get; private set; }
        private Guid _postedMessageId;

        /// <summary>Completes when the CLI posts its message, so fake streams emit after the post (no polling).</summary>
        private readonly TaskCompletionSource<Guid> _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? LastContextWindow { get; private set; }

        public Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget, int? contextWindow = null)
        {
            LastContextWindow = contextWindow;
            var agent = new AgentDto(Guid.NewGuid(), name, model, budget, true, TestClock.Now, 0, budget, Array.Empty<AgentSkillDto>());
            var token = new TokenValueDto(Guid.NewGuid(), "sc_fake-token", "sc_fake-", null);
            return Task.FromResult(new CreatedAgentResponse(agent, token));
        }

        public Task<AgentListResponse> ListAgentsAsync() =>
            Task.FromResult(new AgentListResponse(Array.Empty<AgentDto>()));

        public Task<AgentDto> GetAgentAsync(string idOrName) =>
            Task.FromResult(new AgentDto(Guid.NewGuid(), idOrName, "m", 1, true, TestClock.Now, 0, 1, Array.Empty<AgentSkillDto>()));

        public Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled, int? contextWindow = null)
        {
            LastContextWindow = contextWindow;
            return Task.FromResult(new AgentDto(Guid.NewGuid(), idOrName, model ?? "m", budget ?? 1, enabled ?? true, TestClock.Now, 0, 1,
                Array.Empty<AgentSkillDto>(), contextWindow));
        }

        public Task<CreatedTokenResponse> IssueTokenAsync(string agent, DateTime? expiresAt) =>
            Task.FromResult(new CreatedTokenResponse(new TokenValueDto(Guid.NewGuid(), "sc_fake-token", "sc_fake-", expiresAt)));

        public Task<TokenListResponse> ListTokensAsync(string agent) =>
            Task.FromResult(new TokenListResponse(Array.Empty<TokenMetaDto>()));

        public Task RevokeTokenAsync(string agent, string tokenId) => Task.CompletedTask;

        public Task<ThreadDto> CreateThreadAsync(string agentId, string? title) =>
            Task.FromResult(new ThreadDto(Guid.NewGuid(), Guid.NewGuid(), title, TestClock.Now));

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
                var posted = await gw._posted.Task.WaitAsync(ct);
                foreach (var ev in script(posted))
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
            _posted.TrySetResult(_postedMessageId);
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

        public List<(string Name, string Password)> CreatedUsers { get; } = new();
        public List<(string Name, string Password)> PasswordsSet { get; } = new();
        public List<(string Name, bool Disabled)> DisabledChanges { get; } = new();

        public Task<UserAdminDto> CreateUserAsync(string name, string password)
        {
            CreatedUsers.Add((name, password));
            return Task.FromResult(new UserAdminDto(Guid.NewGuid(), name, false, TestClock.Now));
        }

        public Task<UserListResponse> ListUsersAsync() => Task.FromResult(new UserListResponse(new[]
        {
            new UserAdminDto(Guid.NewGuid(), "kevin", false, TestClock.Now),
        }));

        public Task SetUserPasswordAsync(string name, string password)
        {
            PasswordsSet.Add((name, password));
            return Task.CompletedTask;
        }

        public Task<UserAdminDto> SetUserDisabledAsync(string name, bool disabled)
        {
            DisabledChanges.Add((name, disabled));
            return Task.FromResult(new UserAdminDto(Guid.NewGuid(), name, disabled, TestClock.Now));
        }

        public Task<ApprovalDto> DecideApprovalAsync(Guid approvalId, string decision)
        {
            if (DecisionConflicts)
                throw new CliException("Approval is already expired.");
            Decisions.Add((approvalId, decision));
            return Task.FromResult(new ApprovalDto(approvalId, decision, "user", TestClock.Now));
        }

        private sealed class FakeStream : IThreadStream
        {
            private readonly FakeGateway _gw;
            public FakeStream(FakeGateway gw) => _gw = gw;

            public async IAsyncEnumerable<CliSseEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
            {
                // Wait until a message has been posted, then emit events for it.
                var corr = await _gw._posted.Task.WaitAsync(ct);
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
