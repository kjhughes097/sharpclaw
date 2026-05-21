using SharpClaw.Models;

namespace SharpClaw.Abstractions;

public interface IServiceRegistry
{
    void Register(ServiceDefinition definition);
    ServiceDefinition? Get(string name);
    IReadOnlyList<ServiceDefinition> GetAll();
    void Clear();
}
