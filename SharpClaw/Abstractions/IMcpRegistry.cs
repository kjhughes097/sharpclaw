using SharpClaw.Models;

namespace SharpClaw.Abstractions;

public interface IMcpRegistry
{
    void Register(string name, McpServerDefinition config);
    McpServerDefinition? Get(string name);
    IReadOnlyDictionary<string, McpServerDefinition> GetAll();
    void Clear();
}
