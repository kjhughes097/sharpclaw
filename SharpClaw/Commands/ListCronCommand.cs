using SharpClaw.Scheduling;

namespace SharpClaw.Commands;

public sealed class ListCronCommand(ScheduleStore store) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().Equals(".cron", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var tasks = store.GetAll()
            .Where(t => t.Enabled)
            .OrderBy(t => t.NextRunUtc)
            .ToList();

        if (tasks.Count == 0)
            return Task.FromResult(new CommandResult(true, "No cron jobs."));

        var lines = tasks.Select(t =>
        {
            var desc = !string.IsNullOrEmpty(t.Description)
                ? t.Description
                : t.Prompt[..Math.Min(t.Prompt.Length, 40)];
            var type = t.IsOneOff ? "once" : "recurring";
            return $"• [{t.Id}] `{t.CronExpression}` ({type}) — {desc}\n  Next: {t.NextRunUtc:yyyy-MM-dd HH:mm} UTC";
        });

        var response = $"Cron jobs:\n{string.Join('\n', lines)}";
        return Task.FromResult(new CommandResult(true, response));
    }
}
