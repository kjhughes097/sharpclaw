using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using SharpClaw.Abstractions;
using SharpClaw.Configuration;
using SharpClaw.Interactions;
using SharpClaw.Models;
using SharpClaw.Scheduling;
using SharpClaw.Sessions;
using ChannelMessageOrigin = SharpClaw.Models.MessageOrigin;

namespace SharpClaw.Telegram;

public sealed class TelegramService(
    ITelegramBotClient botClient,
    AgentSessionRegistry sessionRegistry,
    AgentInvoker invoker,
    TelegramAgentRouter router,
    IAgentRegistry agentRegistry,
    IOptions<TelegramOptions> telegramOptions,
    ILogger<TelegramService> logger) : BackgroundService
{
    private readonly HashSet<string> _allowedUsers =
        new(telegramOptions.Value.AllowedUsers, StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultAgent = telegramOptions.Value.DefaultAgent;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(telegramOptions.Value.BotToken) ||
            telegramOptions.Value.BotToken == "YOUR_BOT_TOKEN_HERE")
        {
            logger.LogWarning("Telegram bot token not configured — Telegram integration disabled");
            return;
        }

        botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            cancellationToken: ct);

        logger.LogInformation("Telegram bot started");
        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        if (update.Message?.Text is not { } text) return;

        var username = update.Message.From?.Username;
        if (username is null || !_allowedUsers.Contains(username)) return;

        var chatId = update.Message.Chat.Id;
        var channelKey = chatId.ToString();

        var agentId = router.Resolve(chatId)
            ?? ResolveFromGroupTitle(update.Message.Chat)
            ?? (_defaultAgent.Length > 0 ? _defaultAgent : null);

        if (agentId is null)
        {
            await botClient.SendMessage(chatId,
                "No agent selected. Send .{letter} to choose one.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var session = sessionRegistry.GetOrCreate(channelKey, agentId);

        // Publish inbound message
        await session.PublishAsync(new AgentMessage(
            session.SessionId, Guid.NewGuid().ToString(),
            ChannelMessageOrigin.Telegram, agentId, text, DateTimeOffset.UtcNow), ct);

        // Send typing indicator while processing
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = SendTypingIndicatorAsync(chatId, typingCts.Token);

        try
        {
            var schedulingCtx = new SchedulingContext(channelKey, ScheduleChannelType.Telegram, agentId);
            var (switchedTo, responseText) = await invoker.InvokeAsync(session, text, schedulingCtx, ct);

            if (!string.IsNullOrEmpty(responseText))
                await botClient.SendMessage(chatId, responseText, parseMode: ParseMode.Html, cancellationToken: ct);

            if (switchedTo is not null)
                router.Map(chatId, switchedTo);
        }
        finally
        {
            await typingCts.CancelAsync();
            try { await typingTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task SendTypingIndicatorAsync(long chatId, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private string? ResolveFromGroupTitle(Chat chat)
    {
        if (chat.Type is not (ChatType.Group or ChatType.Supergroup)) return null;
        if (string.IsNullOrWhiteSpace(chat.Title)) return null;
        return agentRegistry.Get(chat.Title)?.Name;
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Telegram bot error");
        return Task.CompletedTask;
    }
}
