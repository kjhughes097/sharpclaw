namespace SharpClaw.Commands;

public interface ICommand
{
    /// <summary>
    /// Returns true if this command handles the given input text.
    /// </summary>
    bool CanHandle(string text);

    /// <summary>
    /// Execute the command. Only called if CanHandle returned true.
    /// </summary>
    Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default);
}
