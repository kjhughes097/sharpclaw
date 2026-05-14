using SharpClaw.Abstractions;

namespace SharpClaw.Commands;

public sealed class ListSkillsCommand(ISkillRegistry skillRegistry) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".lss", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var skills = skillRegistry.GetAll()
            .OrderBy(s => s.Name)
            .Select(s => $"• {s.Name} — {s.Description ?? "no description"}");

        var response = skills.Any()
            ? $"Skills:\n{string.Join('\n', skills)}"
            : "No skills registered.";

        return Task.FromResult(new CommandResult(true, response));
    }
}
