using System.Collections.Concurrent;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Registry;

public sealed class ServiceRegistry : IServiceRegistry
{
    private readonly ConcurrentDictionary<string, ServiceDefinition> _services = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ServiceDefinition definition) =>
        _services[definition.Name] = definition;

    public ServiceDefinition? Get(string name) =>
        _services.TryGetValue(name, out var def) ? def : null;

    public IReadOnlyList<ServiceDefinition> GetAll() =>
        _services.Values.ToList().AsReadOnly();

    public void Clear() =>
        _services.Clear();
}
