using SharpClaw.Abstractions;
using SharpClaw.Execution;
using SharpClaw.Models;
using SharpClaw.Scheduling;

namespace SharpClaw.Workers;

public sealed class SchedulerWorker(
    ScheduleStore store,
    IAgentRegistry agentRegistry,
    AgentRunner runner,
    SchedulingContextAccessor schedulingContextAccessor,
    IEnumerable<ITaskResultDelivery> deliveryHandlers,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<ScheduleChannelType, ITaskResultDelivery> _delivery =
        deliveryHandlers.ToDictionary(d => d.ChannelType);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduler worker started — checking every {Interval}s", TickInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken);
                await ProcessDueTasksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in scheduler tick");
            }
        }

        logger.LogInformation("Scheduler worker stopped");
    }

    private async Task ProcessDueTasksAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var dueTasks = store.GetDue(now);

        if (dueTasks.Count == 0)
            return;

        logger.LogInformation("Processing {Count} due scheduled task(s)", dueTasks.Count);

        foreach (var task in dueTasks)
        {
            try
            {
                await ExecuteTaskAsync(task, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to execute scheduled task {TaskId} for agent {Agent}",
                    task.Id, task.AgentId);
            }
        }
    }

    private async Task ExecuteTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        var agent = agentRegistry.Get(task.AgentId);
        if (agent is null)
        {
            logger.LogWarning("Agent '{Agent}' not found for scheduled task {TaskId} — disabling task",
                task.AgentId, task.Id);
            task.Enabled = false;
            store.Save(task);
            return;
        }

        logger.LogInformation("Executing scheduled task {TaskId}: agent={Agent}, prompt={Prompt}",
            task.Id, task.AgentId, task.Prompt[..Math.Min(task.Prompt.Length, 80)]);

        // Set scheduling context so tools (e.g. send_telegram) can access channel info
        schedulingContextAccessor.Current = new SchedulingContext(
            task.ChannelKey, task.ChannelType, task.AgentId);

        var request = new AgentRunRequest(
            Prompt: task.Prompt,
            Llm: agent.Llm,
            Model: agent.Model,
            SystemPromptOverride: agent.SystemPrompt,
            McpServerNames: agent.McpNames.Count > 0 ? agent.McpNames : null,
            ToolNames: agent.ToolNames.Count > 0 ? agent.ToolNames : null
        );

        var result = await runner.RunAsync(request, ct);
        var responseText = result.Success
            ? result.Response ?? "(no response)"
            : $"[Task error: {result.Error}]";

        // Deliver result
        if (_delivery.TryGetValue(task.ChannelType, out var delivery))
        {
            await delivery.DeliverAsync(task, responseText, ct);
        }
        else
        {
            logger.LogWarning("No delivery handler for channel type {ChannelType} — task {TaskId} result discarded",
                task.ChannelType, task.Id);
        }

        // Update task state
        task.LastRunUtc = DateTimeOffset.UtcNow;

        if (task.IsOneOff)
        {
            store.Delete(task.Id);
            logger.LogInformation("One-off task {TaskId} completed and removed", task.Id);
        }
        else
        {
            var nextRun = ScheduleStore.ComputeNextRun(task.CronExpression, DateTimeOffset.UtcNow);
            if (nextRun.HasValue)
            {
                task.NextRunUtc = nextRun.Value;
                store.Save(task);
                logger.LogDebug("Task {TaskId} next run: {NextRun}", task.Id, task.NextRunUtc);
            }
            else
            {
                task.Enabled = false;
                store.Save(task);
                logger.LogWarning("Task {TaskId} has no future occurrences — disabled", task.Id);
            }
        }
    }
}
