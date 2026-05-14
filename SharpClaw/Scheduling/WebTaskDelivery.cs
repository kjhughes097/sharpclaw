using SharpClaw.Models;
using SharpClaw.Sessions;

namespace SharpClaw.Scheduling;

public sealed class WebTaskDelivery(AgentSessionRegistry sessionRegistry, ILogger<WebTaskDelivery> logger)
    : ITaskResultDelivery
{
    public ScheduleChannelType ChannelType => ScheduleChannelType.Web;

    public async Task DeliverAsync(ScheduledTask task, string result, CancellationToken ct = default)
    {
        var session = sessionRegistry.Get(task.ChannelKey);
        if (session is null)
        {
            logger.LogWarning("No active web session for channel key '{ChannelKey}' — task {TaskId} result not delivered",
                task.ChannelKey, task.Id);
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
