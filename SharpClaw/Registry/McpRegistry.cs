using System.Collections.Concurrent;
using SharpClaw.Abstractions;
using SharpClaw.Models;

namespace SharpClaw.Registry;

public sealed class McpRegistry : IMcpRegistry
{
    private readonly ConcurrentDictionary<string, McpServerDefinition> _servers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, McpServerDefinition config) =>
        _servers[name] = config;

    public McpServerDefinition? Get(string name) =>
        _servers.TryGetValue(name, out var config) ? config : null;

    public IReadOnlyDictionary<string, McpServerDefinition> GetAll() =>
        new Dictionary<string, McpServerDefinition>(_servers);

    public void Clear() =>
        _servers.Clear();
}
