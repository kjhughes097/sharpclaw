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
    IOptions<SharpClawOptions> sharpClawOptions,
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
        if (update.Message is not { } message) return;

        // Extract text content — either message text or file caption
        var text = message.Text;
        string? fileContext = null;

        // Handle documents and photos
        if (message.Document is not null || message.Photo is not null)
        {
            fileContext = await DownloadFileAsync(message, ct);
            text = message.Caption;
        }

        // Must have either text or a file to process
        if (text is null && fileContext is null) return;

        var username = message.From?.Username;
        if (username is null || !_allowedUsers.Contains(username)) return;

        var chatId = message.Chat.Id;
        var channelKey = chatId.ToString();

        var agentId = router.Resolve(chatId)
            ?? ResolveFromGroupTitle(message.Chat)
            ?? (_defaultAgent.Length > 0 ? _defaultAgent : null);

        if (agentId is null)
        {
            await botClient.SendMessage(chatId,
                "No agent selected. Send .{letter} to choose one.", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        // Build the prompt: combine file context and user text
        var prompt = BuildPrompt(text, fileContext);

        var session = sessionRegistry.GetOrCreate(channelKey, agentId);

        // Publish inbound message
        await session.PublishAsync(new AgentMessage(
            session.SessionId, Guid.NewGuid().ToString(),
            ChannelMessageOrigin.Telegram, agentId, prompt, DateTimeOffset.UtcNow), ct);

        // Send typing indicator while processing
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = SendTypingIndicatorAsync(chatId, typingCts.Token);

        try
        {
            var schedulingCtx = new SchedulingContext(channelKey, ScheduleChannelType.Telegram, agentId);
            var (switchedTo, responseText) = await invoker.InvokeAsync(session, prompt, schedulingCtx, ct);

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

    private async Task<string?> DownloadFileAsync(Message message, CancellationToken ct)
    {
        var workspacePath = sharpClawOptions.Value.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            logger.LogWarning("WorkspacePath not configured — cannot save Telegram file");
            return null;
        }

        string? fileId;
        string fileName;

        if (message.Document is { } doc)
        {
            fileId = doc.FileId;
            fileName = doc.FileName ?? $"document_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        }
        else if (message.Photo is { Length: > 0 } photos)
        {
            // Use the largest photo size
            var largest = photos[^1];
            fileId = largest.FileId;
            fileName = $"photo_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg";
        }
        else
        {
            return null;
        }

        var uploadsDir = Path.Combine(workspacePath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        // Prefix with timestamp to avoid collisions
        var safeFileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{SanitizeFileName(fileName)}";
        var localPath = Path.Combine(uploadsDir, safeFileName);

        try
        {
            var file = await botClient.GetFile(fileId, ct);
            if (file.FilePath is null)
            {
                logger.LogWarning("Telegram returned no file path for FileId {FileId}", fileId);
                return null;
            }

            await using var stream = System.IO.File.Create(localPath);
            await botClient.DownloadFile(file.FilePath, stream, ct);

            logger.LogInformation("Downloaded Telegram file to {Path} ({Size} bytes)", localPath, stream.Length);
            return $"[File saved: {localPath}]";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download Telegram file {FileId}", fileId);
            return null;
        }
    }

    private static string BuildPrompt(string? text, string? fileContext)
    {
        if (fileContext is not null && text is not null)
            return $"{fileContext}\n{text}";
        if (fileContext is not null)
            return fileContext;
        return text!;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(c => invalid.Contains(c) ? '_' : c));
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
