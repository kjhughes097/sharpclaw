using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class PingCommand(IAgentRegistry agentRegistry) : ICommand
{
    private static readonly HashSet<string> Triggers = new(StringComparer.OrdinalIgnoreCase) { "hi", "ping" };

    public bool CanHandle(string text) => Triggers.Contains(text.Trim());

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.CurrentAgentId is null)
            return Task.FromResult(new CommandResult(true, "No agent set yet."));

        var agent = agentRegistry.Get(context.CurrentAgentId);
        if (agent is null)
            return Task.FromResult(new CommandResult(true, "No agent set yet."));

        var desc = agent.Description ?? "No description available.";
        return Task.FromResult(new CommandResult(true, $"{agent.Name}: {desc}"));
    }
}
