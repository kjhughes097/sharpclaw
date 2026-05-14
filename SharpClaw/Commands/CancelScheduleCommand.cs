using SharpClaw.Scheduling;

namespace SharpClaw.Commands;

public sealed class CancelScheduleCommand(ScheduleStore store) : ICommand
{
    public bool CanHandle(string text) =>
        text.Trim().StartsWith(".cancel ", StringComparison.OrdinalIgnoreCase);

    public Task<CommandResult> ExecuteAsync(CommandContext context, CancellationToken ct = default)
    {
        var parts = context.RawText.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
            return Task.FromResult(new CommandResult(true, "Usage: .cancel <task_id>"));

        var taskId = parts[1].Trim();
        var task = store.Get(taskId);

        if (task is null)
            return Task.FromResult(new CommandResult(true, $"No scheduled task found with ID '{taskId}'."));

        store.Delete(taskId);

        var desc = !string.IsNullOrEmpty(task.Description)
            ? task.Description
            : task.Prompt[..Math.Min(task.Prompt.Length, 40)];

        return Task.FromResult(new CommandResult(true, $"Cancelled task [{taskId}]: {desc}"));
    }
}
