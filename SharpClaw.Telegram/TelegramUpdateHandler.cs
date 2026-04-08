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

        if (IsCommand(text, "start") || IsCommand(text, "new"))
        {
            await StartNewSessionAsync(chatId, messageCt);
            return;
        }

        if (IsCommand(text, "sessions") || IsCommand(text, "list"))
        {
            await ListSessionsAsync(chatId, messageCt);
            return;
        }

        if (TryMatchCommandPrefix(text, "summary", out var summaryArg))
        {
            await HandleSessionCommandAsync(chatId, summaryArg, "summary", SessionCommandKind.Summary, messageCt);
            return;
        }

        if (TryMatchCommandPrefix(text, "delete", out var deleteArg))
        {
            await HandleSessionCommandAsync(chatId, deleteArg, "delete", SessionCommandKind.Delete, messageCt);
            return;
        }

        if (TryMatchCommandPrefix(text, "connect", out var connectArg))
        {
            await HandleSessionCommandAsync(chatId, connectArg, "connect", SessionCommandKind.Connect, messageCt);
            return;
        }

        if (TryMatchCommandPrefix(text, "archive", out var archiveArg))
        {
            await HandleSessionCommandAsync(chatId, archiveArg, "archive", SessionCommandKind.Archive, messageCt);
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
            if (await TryHandleStaleSessionAsync(chatId, sessionId, messageCt))
                return;

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

            if (await TryHandleStaleSessionAsync(chatId, sessionId, messageCt))
                return;

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

    private async Task ListSessionsAsync(long chatId, CancellationToken ct)
    {
        var sessions = await sharpClawClient.ListSessionsAsync(ct);
        if (sessions is null || sessions.Count == 0)
        {
            await botClient.SendMessage(chatId, "No existing sessions found.", cancellationToken: ct);
            return;
        }

        sessionStore.TryGetSession(chatId, out var currentSessionId);

        var lines = new List<string> { "📋 *Sessions*\n" };
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var created = s.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "unknown";
            var current = string.Equals(s.SessionId, currentSessionId, StringComparison.Ordinal) ? " ✅" : "";
            lines.Add($"{i + 1}. *{EscapeMarkdown(s.Persona)}* ({EscapeMarkdown(s.AgentId)}) — {EscapeMarkdown(created)}{current}");
        }

        lines.Add("");
        lines.Add("Use .summary N, .connect N, or .delete N");

        await botClient.SendMessage(chatId, string.Join('\n', lines),
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private enum SessionCommandKind { Summary, Delete, Connect, Archive }

    private async Task HandleSessionCommandAsync(long chatId, string argument, string commandName, SessionCommandKind kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument) || !int.TryParse(argument, out var index) || index < 1)
        {
            await botClient.SendMessage(chatId, $"Usage: .{commandName} <number>\nUse .sessions to see the list.",
                cancellationToken: ct);
            return;
        }

        var sessions = await sharpClawClient.ListSessionsAsync(ct);
        if (sessions is null || sessions.Count == 0)
        {
            await botClient.SendMessage(chatId, "No existing sessions found.", cancellationToken: ct);
            return;
        }

        if (index > sessions.Count)
        {
            await botClient.SendMessage(chatId, $"Invalid session number. There are {sessions.Count} session(s).",
                cancellationToken: ct);
            return;
        }

        var session = sessions[index - 1];

        switch (kind)
        {
            case SessionCommandKind.Summary:
                await ShowSessionSummaryAsync(chatId, session, ct);
                break;
            case SessionCommandKind.Delete:
                await DeleteSessionByCommandAsync(chatId, session, ct);
                break;
            case SessionCommandKind.Connect:
                await ConnectToSessionAsync(chatId, session, ct);
                break;
            case SessionCommandKind.Archive:
                await ArchiveSessionByCommandAsync(chatId, session, ct);
                break;
        }
    }

    private async Task ShowSessionSummaryAsync(long chatId, SessionSummary session, CancellationToken ct)
    {
        var created = session.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "unknown";

        var lines = new List<string>
        {
            $"📝 *Session Summary*",
            $"Agent: {EscapeMarkdown(session.Persona)} ({EscapeMarkdown(session.AgentId)})",
            $"Created: {EscapeMarkdown(created)}",
            $"Messages: {session.MessageCount}",
        };

        if (!string.IsNullOrWhiteSpace(session.LastUserMessage))
        {
            var preview = Truncate(session.LastUserMessage, 200);
            lines.Add($"\n*Last question:*\n{EscapeMarkdown(preview)}");
        }

        if (!string.IsNullOrWhiteSpace(session.LastAssistantMessage))
        {
            var preview = Truncate(session.LastAssistantMessage, 300);
            lines.Add($"\n*Last response:*\n{EscapeMarkdown(preview)}");
        }

        if (string.IsNullOrWhiteSpace(session.LastUserMessage) && string.IsNullOrWhiteSpace(session.LastAssistantMessage))
        {
            lines.Add("\n_No messages in this session yet._");
        }

        await botClient.SendMessage(chatId, string.Join('\n', lines),
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task DeleteSessionByCommandAsync(long chatId, SessionSummary session, CancellationToken ct)
    {
        var deleted = await sharpClawClient.DeleteSessionAsync(session.SessionId, ct);
        if (!deleted)
        {
            await SendErrorAsync(chatId, "Could not delete the session. It may be currently streaming.", ct);
            return;
        }

        if (sessionStore.TryGetSession(chatId, out var currentSessionId) &&
            string.Equals(currentSessionId, session.SessionId, StringComparison.Ordinal))
        {
            sessionStore.RemoveSession(chatId);
        }

        await botClient.SendMessage(chatId,
            $"🗑️ Session with *{EscapeMarkdown(session.Persona)}* has been deleted.",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task ConnectToSessionAsync(long chatId, SessionSummary session, CancellationToken ct)
    {
        sessionStore.SetSession(chatId, session.SessionId);
        await botClient.SendMessage(chatId,
            $"🔗 Connected to session with *{EscapeMarkdown(session.Persona)}* ({EscapeMarkdown(session.AgentId)}). Send a message to continue the conversation.",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task ArchiveSessionByCommandAsync(long chatId, SessionSummary session, CancellationToken ct)
    {
        var archived = await sharpClawClient.ArchiveSessionAsync(session.SessionId, ct);
        if (!archived)
        {
            await SendErrorAsync(chatId, "Could not archive the session. It may be currently streaming or already archived.", ct);
            return;
        }

        if (sessionStore.TryGetSession(chatId, out var currentSessionId) &&
            string.Equals(currentSessionId, session.SessionId, StringComparison.Ordinal))
        {
            sessionStore.RemoveSession(chatId);
        }

        await botClient.SendMessage(chatId,
            $"📦 Session with *{EscapeMarkdown(session.Persona)}* has been archived\\. A knowledge summary has been generated\\.",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task<bool> TryHandleStaleSessionAsync(long chatId, string sessionId, CancellationToken ct)
    {
        var sessions = await sharpClawClient.ListSessionsAsync(ct);
        var sessionExists = sessions?.Any(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal)) ?? true;

        if (sessionExists)
            return false;

        sessionStore.RemoveSession(chatId);
        logger.LogInformation("Cleared stale session '{SessionId}' for chat {ChatId} — session no longer exists on server",
            sessionId, chatId);

        var lines = new List<string>
        {
            "⚠️ Your active session has been deleted.\n"
        };

        if (sessions is not null && sessions.Count > 0)
        {
            lines.Add("📋 *Available sessions:*\n");
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var created = s.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "unknown";
                lines.Add($"{i + 1}. *{EscapeMarkdown(s.Persona)}* ({EscapeMarkdown(s.AgentId)}) — {EscapeMarkdown(created)}");
            }

            lines.Add("");
            lines.Add("Use .connect N to switch to an existing session, or .new to start a fresh one.");
        }
        else
        {
            lines.Add("No other sessions exist. Use .new to start a fresh session.");
        }

        await botClient.SendMessage(chatId, string.Join('\n', lines),
            parseMode: ParseMode.Markdown, cancellationToken: ct);

        return true;
    }

    private static string EscapeMarkdown(string text)
        => text.Replace("_", "\\_").Replace("*", "\\*").Replace("[", "\\[").Replace("`", "\\`");

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static bool IsCommand(string text, string command)
        => text.Equals("/" + command, StringComparison.OrdinalIgnoreCase) ||
           text.Equals("." + command, StringComparison.OrdinalIgnoreCase);

    private static bool TryMatchCommandPrefix(string text, string command, out string argument)
    {
        foreach (var prefix in new[] { "/", "." })
        {
            var full = prefix + command;
            if (text.StartsWith(full, StringComparison.OrdinalIgnoreCase))
            {
                argument = text.Length > full.Length ? text[full.Length..].Trim() : string.Empty;
                return true;
            }
        }

        argument = string.Empty;
        return false;
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
