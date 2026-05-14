using System.Threading.Channels;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Sessions;

public sealed class AgentSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString();
    public string AgentId { get; private set; }

    /// <summary>
    /// The live LLM session. Null until the first message is sent.
    /// </summary>
    public ILlmSession? LlmSession { get; private set; }

    private readonly Channel<AgentMessage> _bus =
        Channel.CreateUnbounded<AgentMessage>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = false });

    private readonly int _historyLimit;
    private readonly List<AgentMessage> _history = [];
    private readonly Lock _historyLock = new();

    public AgentSession(string agentId, int historyLimit)
    {
        AgentId = agentId;
        _historyLimit = historyLimit;
    }

    public void SetAgent(string agentId) => AgentId = agentId;

    public void SetLlmSession(ILlmSession session) =>
        LlmSession = session;

    public ValueTask PublishAsync(AgentMessage msg, CancellationToken ct = default)
    {
        lock (_historyLock)
        {
            _history.Add(msg);
            if (_history.Count > _historyLimit * 2)
                _history.RemoveAt(0);
        }
        return _bus.Writer.WriteAsync(msg, ct);
    }

    public IReadOnlyList<AgentMessage> GetHistory()
    {
        lock (_historyLock)
            return _history.ToList();
    }

    public IAsyncEnumerable<AgentMessage> ReadAllAsync(CancellationToken ct = default) =>
        _bus.Reader.ReadAllAsync(ct);
}
