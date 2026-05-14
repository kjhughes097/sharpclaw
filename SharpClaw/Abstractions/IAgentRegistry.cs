namespace SharpClaw.Abstractions;

public interface IAgentRegistry
{
    void Register(IAgent agent);
    IAgent? Get(string name);
    IReadOnlyList<IAgent> GetAll();
    void Clear();
}
