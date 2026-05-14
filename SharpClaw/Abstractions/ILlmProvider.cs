using SharpClaw.Models;

namespace SharpClaw.Abstractions;

public interface ILlmProvider
{
    string ProviderName { get; }
    Task<ILlmSession> CreateSessionAsync(LlmSessionRequest request, CancellationToken ct = default);
    Task<AgentRunResult> SendAsync(ILlmSession session, string prompt, CancellationToken ct = default);
}
