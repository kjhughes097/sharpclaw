namespace SharpClaw.Abstractions;

public interface IToolRegistry
{
    void Register(ITool tool);
    ITool? Get(string name);
    IReadOnlyCollection<ITool> GetAll();
    void Clear();
}
