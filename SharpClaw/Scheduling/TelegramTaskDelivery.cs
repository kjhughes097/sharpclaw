using SharpClaw.Auditing;
using SharpClaw.Interactions;
using SharpClaw.Models;
using SharpClaw.Sessions;
using Telegram.Bot;

namespace SharpClaw.Scheduling;

public sealed class TelegramTaskDelivery(
    ITelegramBotClient botClient,
    AgentSessionRegistry sessionRegistry,
    ChannelFanOutService fanOut,
    TranscriptService transcripts,
    ILogger<TelegramTaskDelivery> logger) : ITaskResultDelivery
{
    public ScheduleChannelType ChannelType => ScheduleChannelType.Telegram;

    public async Task DeliverAsync(ScheduledTask task, string result, CancellationToken ct = default)
    {
        if (!long.TryParse(task.ChannelKey, out var chatId))
        {
            logger.LogWarning("Invalid Telegram chat ID '{ChannelKey}' for task {TaskId}", task.ChannelKey, task.Id);
            return;
        }

        var formatted = $"📋 **Scheduled task result** ({task.Description ?? task.Id}):\n\n{result}";

        await botClient.SendMessage(chatId, formatted, cancellationToken: ct);

        var session = sessionRegistry.GetOrCreate(task.AgentId);

        await transcripts.LogAsync(
            agentName: task.AgentId,
            sessionId: session.SessionId,
            turnType: "agent",
            content: formatted,
            metadata: new TranscriptMetadata(Source: $"scheduler:{task.Id}:telegram"),
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
            "Delivered scheduled task {TaskId} result to Telegram chat {ChatId} (agent {AgentId})",
            task.Id, chatId, task.AgentId);
    }
}
