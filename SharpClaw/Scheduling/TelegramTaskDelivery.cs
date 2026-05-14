using SharpClaw.Models;
using Telegram.Bot;

namespace SharpClaw.Scheduling;

public sealed class TelegramTaskDelivery(ITelegramBotClient botClient, ILogger<TelegramTaskDelivery> logger)
    : ITaskResultDelivery
{
    public ScheduleChannelType ChannelType => ScheduleChannelType.Telegram;

    public async Task DeliverAsync(ScheduledTask task, string result, CancellationToken ct = default)
    {
        if (!long.TryParse(task.ChannelKey, out var chatId))
        {
            logger.LogWarning("Invalid Telegram chat ID '{ChannelKey}' for task {TaskId}", task.ChannelKey, task.Id);
            return;
        }

        var message = $"📋 **Scheduled task result** ({task.Description ?? task.Id}):\n\n{result}";
        await botClient.SendMessage(chatId, message, cancellationToken: ct);
        logger.LogDebug("Delivered scheduled task {TaskId} result to Telegram chat {ChatId}", task.Id, chatId);
    }
}
