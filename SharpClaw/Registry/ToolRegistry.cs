using System.Collections.Concurrent;
using SharpClaw.Abstractions;

namespace SharpClaw.Registry;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) =>
        _tools[tool.Name] = tool;

    public ITool? Get(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyCollection<ITool> GetAll() =>
        _tools.Values.ToList().AsReadOnly();

    public void Clear() =>
        _tools.Clear();
}
