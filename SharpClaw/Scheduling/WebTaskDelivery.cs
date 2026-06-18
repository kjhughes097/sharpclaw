using SharpClaw.Auditing;
using SharpClaw.Interactions;
using SharpClaw.Models;
using SharpClaw.Sessions;

namespace SharpClaw.Scheduling;

public sealed class WebTaskDelivery(
    AgentSessionRegistry sessionRegistry,
    ChannelFanOutService fanOut,
    TranscriptService transcripts,
    ILogger<WebTaskDelivery> logger) : ITaskResultDelivery
{
    public ScheduleChannelType ChannelType => ScheduleChannelType.Web;

    public async Task DeliverAsync(ScheduledTask task, string result, CancellationToken ct = default)
    {
        var formatted = $"📋 Scheduled task result ({task.Description ?? task.Id}):\n\n{result}";

        var session = sessionRegistry.GetOrCreate(task.AgentId);

        await transcripts.LogAsync(
            agentName: task.AgentId,
            sessionId: session.SessionId,
            turnType: "agent",
            content: formatted,
            metadata: new TranscriptMetadata(Source: $"scheduler:{task.Id}"),
            ct: ct);

        await session.PublishAsync(new AgentMessage(
            session.SessionId,
            Guid.NewGuid().ToString(),
            MessageOrigin.Agent,
            task.AgentId,
            formatted,
            DateTimeOffset.UtcNow), ct);

        await fanOut.BroadcastAsync(task.AgentId, formatted, excludeChannelId: null, ct);

        logger.LogInformation(
            "Delivered scheduled task {TaskId} result to web (agent {AgentId}, session {SessionId})",
            task.Id, task.AgentId, session.SessionId);
    }
}
