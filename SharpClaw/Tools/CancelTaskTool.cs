using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Scheduling;

namespace SharpClaw.Tools;

public sealed class CancelTaskTool(ScheduleStore store) : ITool
{
    public string Name => "cancel_task";
    public string Description => "Cancel a previously scheduled task by its ID.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("task_id", "string", "The ID of the scheduled task to cancel.", Required: true),
    ];

    public Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var taskId = context.GetString("task_id");

        if (string.IsNullOrWhiteSpace(taskId))
            return Task.FromResult<object?>("Error: 'task_id' is required.");

        var task = store.Get(taskId);
        if (task is null)
            return Task.FromResult<object?>($"Error: No scheduled task found with ID '{taskId}'.");

        store.Delete(taskId);
        return Task.FromResult<object?>($"Cancelled scheduled task '{taskId}' ({task.Description ?? task.Prompt[..Math.Min(task.Prompt.Length, 50)]}).");
    }
}
