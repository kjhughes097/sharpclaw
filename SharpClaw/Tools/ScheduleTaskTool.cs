using Cronos;
using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Scheduling;
namespace SharpClaw.Tools;

public sealed class ScheduleTaskTool(
    ScheduleStore store,
    SchedulingContextAccessor contextAccessor) : ITool
{
    public string Name => "schedule_task";
    public string Description => "Schedule a task to run an agent prompt at a specified time. Supports one-off and recurring schedules using cron expressions (5-field standard cron: minute hour day-of-month month day-of-week).";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("prompt", "string", "The prompt to send to the agent when the task fires.", Required: true),
        new("cron_expression", "string", "Standard 5-field cron expression (e.g. '0 8 * * 3' for every Wednesday at 8am UTC).", Required: true),
        new("one_off", "boolean", "If true, the task runs once at the next matching time and is then deleted. Defaults to false (recurring).", Required: false),
        new("description", "string", "A short human-readable description of what this scheduled task does.", Required: false),
    ];

    public Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var prompt = context.GetString("prompt");
        var cronExpr = context.GetString("cron_expression");
        var oneOffStr = context.GetString("one_off");
        var description = context.GetString("description");

        if (string.IsNullOrWhiteSpace(prompt))
            return Task.FromResult<object?>("Error: 'prompt' is required.");

        if (string.IsNullOrWhiteSpace(cronExpr))
            return Task.FromResult<object?>("Error: 'cron_expression' is required.");

        // Validate cron expression
        CronExpression cron;
        try
        {
            cron = CronExpression.Parse(cronExpr, CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            return Task.FromResult<object?>($"Error: Invalid cron expression '{cronExpr}': {ex.Message}");
        }

        var isOneOff = string.Equals(oneOffStr, "true", StringComparison.OrdinalIgnoreCase);

        // Get channel context
        var schedulingContext = contextAccessor.Current;
        if (schedulingContext is null)
            return Task.FromResult<object?>("Error: Unable to determine delivery channel for scheduled task.");

        // Determine which agent is currently running (from context accessor)
        var channelKey = schedulingContext.ChannelKey;
        var channelType = schedulingContext.ChannelType;
        var agentId = schedulingContext.AgentId;

        // Compute next run time
        var now = DateTimeOffset.UtcNow;
        var nextOccurrence = cron.GetNextOccurrence(now.UtcDateTime, inclusive: false);
        if (nextOccurrence is null)
            return Task.FromResult<object?>("Error: Could not compute next run time from cron expression.");

        var nextRunUtc = new DateTimeOffset(nextOccurrence.Value, TimeSpan.Zero);

        var task = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            AgentId = agentId,
            Prompt = prompt,
            CronExpression = cronExpr,
            Description = description,
            IsOneOff = isOneOff,
            ChannelKey = channelKey,
            ChannelType = channelType,
            CreatedUtc = now,
            NextRunUtc = nextRunUtc,
        };

        store.Save(task);

        var scheduleType = isOneOff ? "one-off" : "recurring";
        var result = $"Scheduled {scheduleType} task (ID: {task.Id}). Next run: {nextRunUtc:yyyy-MM-dd HH:mm} UTC. Cron: {cronExpr}";
        if (!string.IsNullOrEmpty(description))
            result += $"\nDescription: {description}";

        return Task.FromResult<object?>(result);
    }
}
