using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SharpClaw.Telegram;

public sealed class TelegramUpdateHandler(
    ITelegramBotClient botClient,
    SharpClawApiClient sharpClawClient,
    SessionMappingStore sessionStore,
    IConfiguration configuration,
    ILogger<TelegramUpdateHandler> logger) : IUpdateHandler
{
    private readonly bool isEnabled = LoadIsEnabled(configuration);
    private readonly HashSet<long> allowedUserIds = LoadAllowedUserIds(configuration);
    private readonly HashSet<string> allowedUsernames = LoadAllowedUsernames(configuration);

    public async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Message is { } message)
                await HandleMessageAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing Telegram update {UpdateId}", update.Id);
        }
    }

    public Task HandleErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Telegram polling error from {Source}", source);
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var text = message.Text;

        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!isEnabled)
            return;

        if (!IsAllowedUser(message, out var deniedReason))
        {
            logger.LogInformation("Ignoring message from unauthorized Telegram user in chat {ChatId}. Reason: {Reason}",
                chatId, deniedReason);
            return;
        }

        // Apply a per-message timeout linked to the host shutdown token.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var messageCt = linkedCts.Token;

        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            await StartNewSessionAsync(chatId, messageCt);
            return;
        }

        var sessionId = await EnsureSessionAsync(chatId, messageCt);
        if (sessionId is null)
        {
            await SendErrorAsync(chatId, "Could not connect to SharpClaw. Please try again later.", messageCt);
            return;
        }

        try
        {
            await botClient.SendChatAction(chatId, ChatAction.Typing, cancellationToken: messageCt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send typing indicator to chat {ChatId}", chatId);
        }

        var messageId = await sharpClawClient.SendMessageAsync(sessionId, text, messageCt);
        if (messageId is null)
        {
            await SendErrorAsync(chatId, "Failed to send your message to the agent. Please try again.", messageCt);
            return;
        }

        string responseContent;
        try
        {
            responseContent = await sharpClawClient.ConsumeStreamAsync(sessionId, messageId, messageCt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to stream response for session '{SessionId}', message '{MessageId}'",
                sessionId, messageId);
            await SendErrorAsync(chatId, "The agent encountered an error. Please try again.", messageCt);
            return;
        }

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            await SendErrorAsync(chatId, "The agent returned an empty response.", messageCt);
            return;
        }

        await SendResponseAsync(chatId, responseContent, messageCt);
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
        var agentId = await sharpClawClient.GetDefaultAgentIdAsync(ct);
        if (string.IsNullOrWhiteSpace(agentId))
        {
            logger.LogWarning("No available agents found in SharpClaw for chat {ChatId}", chatId);
            return null;
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

    private bool IsAllowedUser(Message message, out string deniedReason)
    {
        deniedReason = string.Empty;

        if (allowedUserIds.Count == 0 && allowedUsernames.Count == 0)
            return true;

        var fromUser = message.From;
        if (fromUser is null)
        {
            deniedReason = "message has no sender";
            return false;
        }

        if (allowedUserIds.Contains(fromUser.Id))
            return true;

        var normalizedUsername = NormalizeUsername(fromUser.Username);
        if (normalizedUsername is not null && allowedUsernames.Contains(normalizedUsername))
            return true;

        deniedReason = $"sender id {fromUser.Id} / username '{fromUser.Username ?? "(none)"}' not allowed";
        return false;
    }

    private static HashSet<long> LoadAllowedUserIds(IConfiguration configuration)
    {
        var values = configuration.GetSection("Telegram:AllowedUserIds").Get<string[]>() ?? [];

        var allowedIds = new HashSet<long>();
        foreach (var rawValue in values)
        {
            if (!long.TryParse(rawValue, out var userId))
                continue;

            allowedIds.Add(userId);
        }

        return allowedIds;
    }

    private static bool LoadIsEnabled(IConfiguration configuration)
    {
        var configured = configuration["Telegram:IsEnabled"];

        if (string.IsNullOrWhiteSpace(configured))
            return true;

        return bool.TryParse(configured, out var parsed) ? parsed : true;
    }

    private static HashSet<string> LoadAllowedUsernames(IConfiguration configuration)
    {
        var values = configuration.GetSection("Telegram:AllowedUsernames").Get<string[]>() ?? [];

        var usernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawValue in values)
        {
            var normalized = NormalizeUsername(rawValue);
            if (normalized is not null)
                usernames.Add(normalized);
        }

        return usernames;
    }

    private static string? NormalizeUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        var normalized = username.Trim();
        return normalized.TrimStart('@');
    }
}
