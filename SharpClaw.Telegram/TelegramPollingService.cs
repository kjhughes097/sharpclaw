using Telegram.Bot;
using Telegram.Bot.Types;

namespace SharpClaw.Telegram;

/// <summary>
/// Background service that receives Telegram updates via long polling
/// and forwards them to the update handler.
/// </summary>
public sealed class TelegramPollingService(
    ITelegramBotClient botClient,
    TelegramUpdateHandler handler,
    ILogger<TelegramPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telegram polling service started");

        // Remove any previously registered webhook so polling works.
        await botClient.DeleteWebhook(cancellationToken: stoppingToken);

        int offset = 0;
        var pending = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await botClient.GetUpdates(
                    offset,
                    limit: 100,
                    timeout: 30,
                    cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error fetching Telegram updates, retrying in 5 seconds");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            // Remove completed tasks before adding new ones.
            pending.RemoveAll(t => t.IsCompleted);

            foreach (var update in updates)
            {
                offset = update.Id + 1;

                pending.Add(Task.Run(async () =>
                {
                    try
                    {
                        await handler.HandleUpdateAsync(update);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process update {UpdateId}", update.Id);
                    }
                }, CancellationToken.None));
            }
        }

        // Wait for in-flight updates to finish before shutting down.
        if (pending.Count > 0)
        {
            logger.LogInformation("Waiting for {Count} in-flight update(s) to complete", pending.Count);
            await Task.WhenAll(pending);
        }

        logger.LogInformation("Telegram polling service stopped");
    }
}
