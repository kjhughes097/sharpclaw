using SharpClaw.Models;
using SharpClaw.Sessions;

namespace SharpClaw.Scheduling;

public sealed class WebTaskDelivery(AgentSessionRegistry sessionRegistry, ILogger<WebTaskDelivery> logger)
    : ITaskResultDelivery
{
    public ScheduleChannelType ChannelType => ScheduleChannelType.Web;

    public async Task DeliverAsync(ScheduledTask task, string result, CancellationToken ct = default)
    {
        var session = sessionRegistry.Get(task.AgentId);
        if (session is null)
        {
            logger.LogWarning("No active session for agent '{AgentId}' — task {TaskId} result not delivered",
                task.AgentId, task.Id);
            return;
        }

        var message = new AgentMessage(
            session.SessionId,
            Guid.NewGuid().ToString(),
            MessageOrigin.Agent,
            task.AgentId,
            $"📋 Scheduled task result ({task.Description ?? task.Id}):\n\n{result}",
            DateTimeOffset.UtcNow);

        await session.PublishAsync(message, ct);
        logger.LogDebug("Delivered scheduled task {TaskId} result to web session {SessionId}", task.Id, session.SessionId);
    }
}
