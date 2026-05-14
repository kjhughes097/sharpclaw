namespace SharpClaw.Commands;

public sealed class CommandRouter(IEnumerable<ICommand> commands)
{
    private readonly IReadOnlyList<ICommand> _commands = commands.ToList();

    /// <summary>
    /// Tries to match and execute a command. Returns null if no command matched.
    /// </summary>
    public async Task<CommandResult?> TryExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        foreach (var command in _commands)
        {
            if (command.CanHandle(context.RawText))
                return await command.ExecuteAsync(context, ct);
        }

        return null;
    }
}
