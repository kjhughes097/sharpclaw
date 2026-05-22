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
            }));
    }
}
