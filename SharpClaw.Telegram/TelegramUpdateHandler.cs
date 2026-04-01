using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SharpClaw.Telegram;

public sealed class TelegramUpdateHandler(
    ITelegramBotClient botClient,
    SharpClawApiClient sharpClawClient,
    SessionMappingStore sessionStore,
    IConfiguration configuration,
    ILogger<TelegramUpdateHandler> logger)
{
    public async Task HandleUpdateAsync(Update update)
    {
        try
        {
            if (update.Message is { } message)
                await HandleMessageAsync(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing Telegram update {UpdateId}", update.Id);
        }
    }

    private async Task HandleMessageAsync(Message message)
    {
        var chatId = message.Chat.Id;
        var text = message.Text;

        if (string.IsNullOrWhiteSpace(text))
            return;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;

        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            await StartNewSessionAsync(chatId, ct);
            return;
        }

        var sessionId = await EnsureSessionAsync(chatId, ct);
        if (sessionId is null)
        {
            await SendErrorAsync(chatId, "Could not connect to SharpClaw. Please try again later.", ct);
            return;
        }

        try
        {
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send typing indicator to chat {ChatId}", chatId);
        }

        var messageId = await sharpClawClient.SendMessageAsync(sessionId, text, ct);
        if (messageId is null)
        {
            await SendErrorAsync(chatId, "Failed to send your message to the agent. Please try again.", ct);
            return;
        }

        string responseContent;
        try
        {
            responseContent = await sharpClawClient.ConsumeStreamAsync(sessionId, messageId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stream response for session '{SessionId}', message '{MessageId}'",
                sessionId, messageId);
            await SendErrorAsync(chatId, "The agent encountered an error. Please try again.", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            await SendErrorAsync(chatId, "The agent returned an empty response.", ct);
            return;
        }

        await SendResponseAsync(chatId, responseContent, ct);
    }

    private async Task<string?> EnsureSessionAsync(long chatId, CancellationToken ct)
    {
        if (sessionStore.TryGetSession(chatId, out var existing))
            return existing;

        return await CreateSessionForChatAsync(chatId, ct);
    }

    private async Task StartNewSessionAsync(long chatId, CancellationToken ct)
    {
        sessionStore.RemoveSession(chatId);
        var sessionId = await CreateSessionForChatAsync(chatId, ct);
        if (sessionId is not null)
            await botClient.SendMessage(chatId, "New session started. How can I help you?", cancellationToken: ct);
        else
            await SendErrorAsync(chatId, "Could not create a new session. Please try again later.", ct);
    }

    private async Task<string?> CreateSessionForChatAsync(long chatId, CancellationToken ct)
    {
        var agentId = configuration["SharpClaw:DefaultAgentId"]
            ?? Environment.GetEnvironmentVariable("SHARPCLAW_DEFAULT_AGENT_ID");

        if (string.IsNullOrWhiteSpace(agentId))
        {
            agentId = await sharpClawClient.GetDefaultAgentIdAsync(ct);
            if (string.IsNullOrWhiteSpace(agentId))
            {
                logger.LogWarning("No available agents found in SharpClaw for chat {ChatId}", chatId);
                return null;
            }
        }

        var sessionId = await sharpClawClient.CreateSessionAsync(agentId, ct);
        if (sessionId is null)
        {
            logger.LogWarning("Failed to create SharpClaw session for chat {ChatId}", chatId);
            return null;
        }

        sessionStore.SetSession(chatId, sessionId);
        logger.LogInformation("Created session '{SessionId}' for chat {ChatId} using agent '{AgentId}'",
            sessionId, chatId, agentId);

        return sessionId;
    }

    private async Task SendResponseAsync(long chatId, string content, CancellationToken ct)
    {
        const int maxTelegramMessageLength = 4096;

        if (content.Length <= maxTelegramMessageLength)
        {
            await botClient.SendMessage(chatId, content, cancellationToken: ct);
            return;
        }

        for (var offset = 0; offset < content.Length; offset += maxTelegramMessageLength)
        {
            var chunk = content.Substring(offset, Math.Min(maxTelegramMessageLength, content.Length - offset));
            await botClient.SendMessage(chatId, chunk, cancellationToken: ct);
        }
    }

    private async Task SendErrorAsync(long chatId, string error, CancellationToken ct)
    {
        try
        {
            await botClient.SendMessage(chatId, $"⚠️ {error}", cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send error message to chat {ChatId}", chatId);
        }
    }
}
