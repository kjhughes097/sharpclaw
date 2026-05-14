using SharpClaw.Workers;

namespace SharpClaw.Commands;

public sealed class ReloadCommand(RegistryWorker registryWorker) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".reload", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        registryWorker.Reload();
        return Task.FromResult(new CommandResult(true, "Registries reloaded."));
    }
}
