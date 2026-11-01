using SharpClaw.Contracts;

namespace SharpClaw.Cli;

/// <summary>One SSE event. <c>Id</c> is set for durable (replayable) events.</summary>
public sealed record CliSseEvent(string Event, string DataJson, long? Id = null);

/// <summary>An open thread SSE stream whose events can be read until disposed.</summary>
public interface IThreadStream : IAsyncDisposable
{
    IAsyncEnumerable<CliSseEvent> ReadAsync(CancellationToken ct);
}

/// <summary>Everything the CLI needs from the gateway's client listener.</summary>
public interface ICliGateway
{
    Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget, int? contextWindow = null);
    Task<AgentListResponse> ListAgentsAsync();
    Task<AgentDto> GetAgentAsync(string idOrName);
    Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled, int? contextWindow = null);

    Task<CreatedTokenResponse> IssueTokenAsync(string agent, DateTime? expiresAt);
    Task<TokenListResponse> ListTokensAsync(string agent);
    Task RevokeTokenAsync(string agent, string tokenId);

    Task<ThreadDto> CreateThreadAsync(string agentId, string? title);

    /// <summary>
    /// Opens the thread stream and returns once response headers have arrived. With a last event
    /// id, the gateway first replays the durable events after it.
    /// </summary>
    Task<IThreadStream> OpenThreadStreamAsync(Guid threadId, long? lastEventId, CancellationToken ct);

    /// <summary>Posts a user message and returns its id. Unattended: approvals are denied at once.</summary>
    Task<Guid> PostMessageAsync(Guid threadId, string content, bool unattended = false);

    /// <summary>The thread's user messages and final replies.</summary>
    Task<IReadOnlyList<MessageDto>> GetMessagesAsync(Guid threadId);

    Task<AuditResponse> AuditAsync(string? agent, int? limit);

    Task<ToolCallAuditResponse> AuditToolCallsAsync(string? agent, int? limit);

    /// <summary>Answers an approval prompt. Throws <see cref="CliException"/> if it was already decided or expired.</summary>
    /// <summary><paramref name="remember"/> (approve only) also creates a standing grant (3.6 spec).</summary>
    Task<ApprovalDto> DecideApprovalAsync(Guid approvalId, string decision, ApprovalRememberRequest? remember = null);

    Task<ApprovalListResponse> ListApprovalsAsync(string? status, string? agent);

    Task<UserAdminDto> CreateUserAsync(string name, string password);
    Task<UserListResponse> ListUsersAsync();
    Task SetUserPasswordAsync(string name, string password);
    Task<UserAdminDto> SetUserDisabledAsync(string name, bool disabled);
}
