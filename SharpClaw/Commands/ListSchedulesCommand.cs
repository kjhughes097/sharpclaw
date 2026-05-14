using SharpClaw.Scheduling;

namespace SharpClaw.Commands;

public sealed class ListSchedulesCommand(ScheduleStore store) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".schedules", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var tasks = store.GetAll()
            .Where(t => t.Enabled)
            .OrderBy(t => t.NextRunUtc)
            .ToList();

        if (tasks.Count == 0)
            return Task.FromResult(new CommandResult(true, "No scheduled tasks."));

        var lines = tasks.Select(t =>
        {
            var desc = !string.IsNullOrEmpty(t.Description)
                ? t.Description
                : t.Prompt[..Math.Min(t.Prompt.Length, 40)];
            var type = t.IsOneOff ? "once" : "recurring";
            return $"• [{t.Id}] {desc} ({type}, next: {t.NextRunUtc:yyyy-MM-dd HH:mm} UTC)";
        });

        var response = $"Scheduled tasks:\n{string.Join('\n', lines)}";
        return Task.FromResult(new CommandResult(true, response));
    }
}
