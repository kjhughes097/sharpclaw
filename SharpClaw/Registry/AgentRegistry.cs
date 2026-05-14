using System.Collections.Concurrent;
using SharpClaw.Abstractions;

namespace SharpClaw.Registry;

public sealed class AgentRegistry : IAgentRegistry
{
    private readonly ConcurrentDictionary<string, IAgent> _agents = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IAgent agent) =>
        _agents[agent.Name] = agent;

    public IAgent? Get(string name) =>
        _agents.TryGetValue(name, out var agent) ? agent : null;

    public IReadOnlyList<IAgent> GetAll() =>
        _agents.Values.ToList().AsReadOnly();

    public void Clear() =>
        _agents.Clear();
}
