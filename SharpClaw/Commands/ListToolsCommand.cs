using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class ListToolsCommand(IToolRegistry toolRegistry) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".lst", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var tools = toolRegistry.GetAll()
            .OrderBy(t => t.Name)
            .Select(t => $"• {t.Name} — {t.Description}");

        var response = tools.Any()
            ? $"Tools:\n{string.Join('\n', tools)}"
            : "No tools registered.";

        return Task.FromResult(new CommandResult(true, response));
    }
}
