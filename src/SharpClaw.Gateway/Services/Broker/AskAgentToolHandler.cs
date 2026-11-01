using SharpClaw.Contracts;

namespace SharpClaw.Gateway.Services.Broker;

/// <summary><c>ask_agent</c> (3.5 spec section 5.1): runs in the gateway; the work is in <see cref="AgentAsks"/>.</summary>
public sealed class AskAgentToolHandler(AgentAsks asks) : IBrokerToolHandler
{
    public IReadOnlyCollection<string> Tools { get; } = [BrokerTools.AskAgent];

    public Task<BrokerResult> InvokeAsync(BrokerInvocation call, CancellationToken ct) => asks.AskAsync(call, ct);
}
