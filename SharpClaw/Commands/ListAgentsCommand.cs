using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class ListAgentsCommand(IAgentRegistry agentRegistry) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".lsa", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var agents = agentRegistry.GetAll()
            .OrderBy(a => a.Name)
            .Select(a => $"• {a.Name} ({a.Model ?? "default"}) — {a.Description ?? "no description"}");

        var response = agents.Any()
            ? $"Agents:\n{string.Join('\n', agents)}"
            : "No agents registered.";

        return Task.FromResult(new CommandResult(true, response));
    }
}
