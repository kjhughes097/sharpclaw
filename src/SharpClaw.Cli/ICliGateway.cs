using SharpClaw.Contracts;

namespace SharpClaw.Cli;

public sealed record CliSseEvent(string Event, string DataJson);

/// <summary>An open thread SSE stream whose events can be read until disposed.</summary>
public interface IThreadStream : IAsyncDisposable
{
    IAsyncEnumerable<CliSseEvent> ReadAsync(CancellationToken ct);
}

/// <summary>Everything the CLI needs from the gateway's client listener.</summary>
public interface ICliGateway
{
    Task<CreatedAgentResponse> CreateAgentAsync(string name, string model, double budget);
    Task<AgentListResponse> ListAgentsAsync();
    Task<AgentDto> GetAgentAsync(string idOrName);
    Task<AgentDto> PatchAgentAsync(string idOrName, string? model, double? budget, bool? enabled);

    Task<CreatedTokenResponse> IssueTokenAsync(string agent, DateTime? expiresAt);
    Task<TokenListResponse> ListTokensAsync(string agent);
    Task RevokeTokenAsync(string agent, string tokenId);

    Task<ThreadDto> CreateThreadAsync(string agentId, string? title);

    /// <summary>Opens the thread stream and returns once response headers have arrived.</summary>
    Task<IThreadStream> OpenThreadStreamAsync(Guid threadId, CancellationToken ct);

    /// <summary>Posts a user message and returns its id.</summary>
    Task<Guid> PostMessageAsync(Guid threadId, string content);

    Task<AuditResponse> AuditAsync(string? agent, int? limit);
}
