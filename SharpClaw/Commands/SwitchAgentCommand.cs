using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class SwitchAgentCommand(IAgentRegistry agentRegistry) : ICommand
{
    public bool CanHandle(string text) =>
        text.Length == 2 && text[0] == '.' && char.IsLetter(text[1]);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var letter = char.ToLowerInvariant(context.RawText[1]);
        var match = agentRegistry.GetAll()
            .FirstOrDefault(a => char.ToLowerInvariant(a.Name[0]) == letter);

        if (match is null)
        {
            var available = string.Join(", ", agentRegistry.GetAll()
                .OrderBy(a => a.Name)
                .Select(a => $".{char.ToLowerInvariant(a.Name[0])} ({a.Name})"));
            return Task.FromResult(new CommandResult(true, $"No agent starting with '{letter}'. Available: {available}"));
        }

        return Task.FromResult(new CommandResult(true, $"Switched to {match.Name}.", SwitchedToAgent: match.Name));
    }
}
