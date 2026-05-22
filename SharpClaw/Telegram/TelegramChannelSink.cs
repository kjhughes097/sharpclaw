using SharpClaw.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace SharpClaw.Telegram;

/// <summary>
/// Channel sink that delivers messages to a Telegram chat.
/// </summary>
internal sealed class TelegramChannelSink(
    ITelegramBotClient botClient,
    long chatId,
    ILogger logger) : IChannelSink
{
    private const int TelegramMessageChunkSize = 3500;

    public string ChannelId { get; } = chatId.ToString();
    public ChannelType Type => ChannelType.Telegram;

    public async Task DeliverAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            // Chunk long messages to respect Telegram limits
            if (text.Length <= TelegramMessageChunkSize)
            {
                await botClient.SendMessage(chatId, text, parseMode: ParseMode.None, cancellationToken: ct);
            }
            else
            {
                foreach (var chunk in ChunkText(text, TelegramMessageChunkSize))
                {
                    await botClient.SendMessage(chatId, chunk, parseMode: ParseMode.None, cancellationToken: ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deliver fan-out message to Telegram chat {ChatId}", chatId);
        }
    }

    private static IEnumerable<string> ChunkText(string text, int maxLength)
    {
        for (var i = 0; i < text.Length; i += maxLength)
            yield return text[i..Math.Min(i + maxLength, text.Length)];
    }
}
