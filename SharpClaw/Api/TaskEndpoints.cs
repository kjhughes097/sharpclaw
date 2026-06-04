using Cronos;
using SharpClaw.Models;
using SharpClaw.Scheduling;

namespace SharpClaw.Api;

internal static class TaskEndpoints
{
    public static void MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tasks").WithTags("Tasks");

        group.MapGet("/", (ScheduleStore store) =>
            store.GetAll().OrderBy(t => t.NextRunUtc).Select(t => new
            {
                t.Id,
                Agent = t.AgentId,
                t.Description,
                Cron = t.CronExpression,
                t.IsOneOff,
                t.ChannelType,
                t.Enabled,
                Created = t.CreatedUtc,
                NextRun = t.NextRunUtc,
                LastRun = t.LastRunUtc,
                Prompt = t.Prompt.Length > 200 ? t.Prompt[..200] + "…" : t.Prompt,
                TaskType = t.TaskType.ToString().ToLowerInvariant(),
                t.Command,
            }));

        group.MapGet("/{id}", (string id, ScheduleStore store) =>
        {
            var task = store.Get(id);
            if (task is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                task.Id,
                Agent = task.AgentId,
                task.Description,
                Cron = task.CronExpression,
                task.IsOneOff,
                ChannelKey = task.ChannelKey,
                ChannelType = task.ChannelType.ToString(),
                task.Enabled,
                Created = task.CreatedUtc,
                NextRun = task.NextRunUtc,
                LastRun = task.LastRunUtc,
                task.Prompt,
                TaskType = task.TaskType.ToString().ToLowerInvariant(),
                task.Command,
            });
        });

        group.MapPost("/", (TaskCreateRequest request, ScheduleStore store) =>
        {
            // Validate cron expression
            try
            {
                CronExpression.Parse(request.Cron, CronFormat.Standard);
            }
            catch (CronFormatException)
            {
                return Results.BadRequest("Invalid cron expression.");
            }

            var taskType = string.Equals(request.TaskType, "command", StringComparison.OrdinalIgnoreCase)
                ? ScheduledTaskType.Command
                : ScheduledTaskType.Agent;

            if (taskType == ScheduledTaskType.Command && string.IsNullOrWhiteSpace(request.Command))
                return Results.BadRequest("Command tasks require a 'command' field.");

            if (taskType == ScheduledTaskType.Agent && string.IsNullOrWhiteSpace(request.Agent))
                return Results.BadRequest("Agent tasks require an 'agent' field.");

            var id = Guid.NewGuid().ToString("N")[..12];
            var nextRun = ScheduleStore.ComputeNextRun(request.Cron, DateTimeOffset.UtcNow)
                          ?? DateTimeOffset.UtcNow;

            var task = new ScheduledTask
            {
                Id = id,
                AgentId = request.Agent ?? string.Empty,
                Prompt = request.Prompt ?? string.Empty,
                CronExpression = request.Cron,
                Description = request.Description ?? string.Empty,
                IsOneOff = request.IsOneOff ?? false,
                ChannelKey = $"web:{request.Agent ?? "system"}",
                ChannelType = ScheduleChannelType.Web,
                TaskType = taskType,
                Command = taskType == ScheduledTaskType.Command ? request.Command : null,
                Enabled = request.Enabled ?? true,
                NextRunUtc = nextRun,
            };

            store.Save(task);
            return Results.Created($"/api/tasks/{id}", new { task.Id, Message = "Task created." });
        });

        group.MapPut("/{id}", (string id, TaskUpdateRequest request, ScheduleStore store) =>
        {
            var existing = store.Get(id);
            if (existing is null)
                return Results.NotFound();

            // Validate cron expression
            var cronExpr = request.Cron ?? existing.CronExpression;
            try
            {
                CronExpression.Parse(cronExpr, CronFormat.Standard);
            }
            catch (CronFormatException)
            {
                return Results.BadRequest("Invalid cron expression.");
            }

            var nextRun = ScheduleStore.ComputeNextRun(cronExpr, DateTimeOffset.UtcNow)
                          ?? existing.NextRunUtc;

            var updated = existing with
            {
                Description = request.Description ?? existing.Description,
                CronExpression = cronExpr,
                Prompt = request.Prompt ?? existing.Prompt,
                Enabled = request.Enabled ?? existing.Enabled,
                IsOneOff = request.IsOneOff ?? existing.IsOneOff,
                AgentId = request.Agent ?? existing.AgentId,
                Command = request.Command ?? existing.Command,
                NextRunUtc = nextRun,
            };

            store.Save(updated);
            return Results.Ok(new { updated.Id, Message = "Task updated." });
        });

        group.MapDelete("/{id}", (string id, ScheduleStore store) =>
        {
            return store.Delete(id) ? Results.NoContent() : Results.NotFound();
        });
    }

    private sealed record TaskCreateRequest(
        string Cron,
        string? TaskType,
        string? Agent,
        string? Command,
        string? Prompt,
        string? Description,
        bool? IsOneOff,
        bool? Enabled
    );

    private sealed record TaskUpdateRequest(
        string? Description,
        string? Cron,
        string? Prompt,
        bool? Enabled,
        bool? IsOneOff,
        string? Agent,
        string? Command
    );
}
