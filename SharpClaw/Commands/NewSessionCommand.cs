using SharpClaw.Sessions;

namespace SharpClaw.Commands;

public sealed class NewSessionCommand(AgentSessionRegistry sessionRegistry) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".new", StringComparison.OrdinalIgnoreCase);

    public async Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        if (context.CurrentAgentId is null)
            return new CommandResult(true, "No agent selected — nothing to reset.");

        var removed = await sessionRegistry.RemoveAsync(context.CurrentAgentId);

        return removed
            ? new CommandResult(true, $"Started a new session for **{context.CurrentAgentId}**.")
            : new CommandResult(true, $"No active session found for **{context.CurrentAgentId}**.");
    }
}
