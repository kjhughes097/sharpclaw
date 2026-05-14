using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class ListMcpsCommand(IMcpRegistry mcpRegistry) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".lsm", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var servers = mcpRegistry.GetAll()
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => $"• {kvp.Key} ({kvp.Value.Transport})");

        var response = servers.Any()
            ? $"MCP Servers:\n{string.Join('\n', servers)}"
            : "No MCP servers registered.";

        return Task.FromResult(new CommandResult(true, response));
    }
}
