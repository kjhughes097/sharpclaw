using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;

namespace SharpClaw.Telegram;

public sealed class TelegramPollingService(
    ITelegramBotClient botClient,
    TelegramUpdateHandler handler,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message],
            DropPendingUpdates = true,
        };

        logger.LogInformation("Starting Telegram long-polling");
        await botClient.ReceiveAsync(handler, receiverOptions, stoppingToken);
        logger.LogInformation("Telegram long-polling stopped");
    }
}
