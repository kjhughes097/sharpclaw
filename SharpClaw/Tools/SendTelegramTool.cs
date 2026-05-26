using SharpClaw.Abstractions;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace SharpClaw.Tools;

public sealed class SendTelegramTool(
    ITelegramBotClient botClient,
    IConfiguration configuration,
    SchedulingContextAccessor schedulingContextAccessor,
    ILogger<SendTelegramTool> logger) : ITool
{
    private readonly HashSet<long> _allowedChatIds = configuration
        .GetSection("Telegram:AllowedChatIds")
        .Get<long[]>()
        ?.ToHashSet() ?? [];

    public string Name => "send_telegram";
    public string Description => "Send a message to a Telegram chat. Only pre-approved chat IDs are permitted. When running from a scheduled task, the chat_id defaults to the originating chat.";

    public IReadOnlyList<ToolParameterDefinition> Parameters { get; } =
    [
        new("chat_id", "string", "The Telegram chat ID to send the message to. Optional when running from a scheduled task — defaults to the originating chat.", Required: false),
        new("message", "string", "The message text to send.", Required: true),
    ];

    public async Task<object?> ExecuteAsync(ToolCallContext context, CancellationToken ct = default)
    {
        var chatIdStr = context.GetString("chat_id");
        var message = context.GetString("message");

        // Fall back to scheduling context channel key if chat_id not provided
        if (string.IsNullOrWhiteSpace(chatIdStr))
        {
            var schedulingContext = schedulingContextAccessor.Current;
            if (schedulingContext is { ChannelType: ScheduleChannelType.Telegram })
            {
                chatIdStr = schedulingContext.ChannelKey;
                logger.LogDebug("SendTelegramTool: using channel key {ChatId} from scheduling context", chatIdStr);
            }
        }

        if (string.IsNullOrWhiteSpace(chatIdStr))
            return "Error: chat_id is required (no scheduling context available to infer it).";

        if (string.IsNullOrWhiteSpace(message))
            return "Error: message is required.";

        if (!long.TryParse(chatIdStr, out var chatId))
            return $"Error: invalid chat_id '{chatIdStr}'. Must be a numeric chat ID.";

        if (_allowedChatIds.Count == 0)
        {
            logger.LogWarning("SendTelegramTool blocked: no AllowedChatIds configured");
            return "Error: no allowed chat IDs configured. Add chat IDs to Telegram:AllowedChatIds in configuration.";
        }

        if (!_allowedChatIds.Contains(chatId))
        {
            logger.LogWarning("SendTelegramTool blocked: chat {ChatId} is not in the allowed list", chatId);
            return $"Error: chat {chatId} is not in the allowed list. Only pre-approved chat IDs can receive messages.";
        }

        try
        {
            await botClient.SendMessage(chatId, message, parseMode: ParseMode.Markdown, cancellationToken: ct);
            logger.LogInformation("Sent Telegram message to chat {ChatId}", chatId);
            return $"Message sent successfully to chat {chatId}.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Telegram message to chat {ChatId}", chatId);
            return $"Error: failed to send message to chat {chatId}: {ex.Message}";
        }
    }
}
