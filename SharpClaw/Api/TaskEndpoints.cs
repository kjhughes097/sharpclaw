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

            var channelType = ParseChannelType(request.ChannelType);
            var channelKey = ResolveChannelKey(channelType, request.ChannelKey, request.Agent);
            if (channelType == ScheduleChannelType.Telegram && string.IsNullOrWhiteSpace(channelKey))
                return Results.BadRequest("Telegram channel requires a 'channelKey' (numeric chat ID).");
            if (channelType == ScheduleChannelType.Telegram && !long.TryParse(channelKey, out _))
                return Results.BadRequest("Telegram 'channelKey' must be a numeric chat ID.");

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
                ChannelKey = channelKey!,
                ChannelType = channelType,
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

            var newChannelType = request.ChannelType is null
                ? existing.ChannelType
                : ParseChannelType(request.ChannelType);
            var newAgent = request.Agent ?? existing.AgentId;
            var newChannelKey = request.ChannelKey
                ?? (request.ChannelType is null
                    ? existing.ChannelKey
                    : ResolveChannelKey(newChannelType, null, newAgent) ?? existing.ChannelKey);

            if (newChannelType == ScheduleChannelType.Telegram && !long.TryParse(newChannelKey, out _))
                return Results.BadRequest("Telegram 'channelKey' must be a numeric chat ID.");

            var updated = existing with
            {
                Description = request.Description ?? existing.Description,
                CronExpression = cronExpr,
                Prompt = request.Prompt ?? existing.Prompt,
                Enabled = request.Enabled ?? existing.Enabled,
                IsOneOff = request.IsOneOff ?? existing.IsOneOff,
                AgentId = newAgent,
                Command = request.Command ?? existing.Command,
                ChannelType = newChannelType,
                ChannelKey = newChannelKey,
                NextRunUtc = nextRun,
            };

            store.Save(updated);
            return Results.Ok(new { updated.Id, Message = "Task updated." });
        });

        group.MapDelete("/{id}", (string id, ScheduleStore store, TaskCommentStore commentStore) =>
        {
            if (!store.Delete(id))
                return Results.NotFound();
            commentStore.DeleteAllForTask(id);
            return Results.NoContent();
        });

        // -- Comments --
        group.MapGet("/{id}/comments", (string id, ScheduleStore store, TaskCommentStore comments) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();
            return Results.Ok(comments.GetForTask(id).Select(SerializeComment));
        });

        group.MapPost("/{id}/comments", (string id, CommentCreateRequest request, ScheduleStore store, TaskCommentStore comments) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest("Comment content is required.");

            var comment = comments.Add(id, request.Author ?? "user", request.Content.Trim());
            return Results.Created($"/api/tasks/{id}/comments/{comment.Id}", SerializeComment(comment));
        });

        group.MapPut("/{id}/comments/{commentId}", (string id, string commentId, CommentUpdateRequest request, ScheduleStore store, TaskCommentStore comments) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest("Comment content is required.");

            var existing = comments.Get(id, commentId);
            if (existing is null)
                return Results.NotFound();

            var updated = comments.Update(id, commentId, request.Content.Trim(), request.Author);
            if (updated is null)
                return Results.Forbid();

            return Results.Ok(SerializeComment(updated));
        });

        group.MapDelete("/{id}/comments/{commentId}", (string id, string commentId, string? author, ScheduleStore store, TaskCommentStore comments) =>
        {
            if (store.Get(id) is null)
                return Results.NotFound();

            var existing = comments.Get(id, commentId);
            if (existing is null)
                return Results.NotFound();

            return comments.Delete(id, commentId, author)
                ? Results.NoContent()
                : Results.Forbid();
        });
    }

    private static object SerializeComment(SharpClaw.Models.TaskComment c) => new
    {
        c.Id,
        c.TaskId,
        c.Author,
        c.Content,
        Created = c.CreatedUtc,
        Updated = c.UpdatedUtc,
    };

    private static ScheduleChannelType ParseChannelType(string? value) =>
        string.Equals(value, "telegram", StringComparison.OrdinalIgnoreCase)
            ? ScheduleChannelType.Telegram
            : ScheduleChannelType.Web;

    private static string? ResolveChannelKey(ScheduleChannelType type, string? supplied, string? agent)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
            return supplied.Trim();

        return type == ScheduleChannelType.Web
            ? $"web:{agent ?? "system"}"
            : null;
    }

}

internal sealed record TaskCreateRequest(
    string Cron,
    string? TaskType,
    string? Agent,
    string? Command,
    string? Prompt,
    string? Description,
    bool? IsOneOff,
    bool? Enabled,
    string? ChannelType,
    string? ChannelKey
);

internal sealed record TaskUpdateRequest(
    string? Description,
    string? Cron,
    string? Prompt,
    bool? Enabled,
    bool? IsOneOff,
    string? Agent,
    string? Command,
    string? ChannelType,
    string? ChannelKey
);

internal sealed record CommentCreateRequest(string? Author, string Content);

internal sealed record CommentUpdateRequest(string? Author, string Content);
