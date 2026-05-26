using Microsoft.Extensions.Options;
using Microsoft.VisualBasic.FileIO;
using ClosedXML.Excel;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
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
    ChannelFanOutService fanOut,
    IOptions<TelegramOptions> telegramOptions,
    IOptions<SharpClawOptions> sharpClawOptions,
    ILogger<TelegramService> logger) : BackgroundService
{
    private const int TelegramMessageChunkSize = 3500;
    private const long InlineTextFileMaxBytes = 100_000;
    private static readonly HashSet<string> InlineTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv", ".txt", ".md", ".json", ".yaml", ".yml", ".log", ".xml"
    };
    private static readonly HashSet<string> CsvExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".tsv"
    };
    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx"
    };

    private readonly HashSet<string> _allowedUsers =
        new(telegramOptions.Value.AllowedUsers, StringComparer.OrdinalIgnoreCase);
    private readonly string _defaultAgent = telegramOptions.Value.DefaultAgent;
    private readonly HashSet<string> _registeredSinks = new();
    private readonly Lock _sinkLock = new();

    private void EnsureTelegramSinkRegistered(string agentName, long chatId)
    {
        var key = $"{agentName}:{chatId}";
        lock (_sinkLock)
        {
            if (!_registeredSinks.Add(key))
                return;
        }

        var sink = new TelegramChannelSink(botClient, chatId, logger);
        fanOut.Register(agentName, sink);
    }

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

        // Eagerly register Telegram sinks for agents with a configured chat ID
        // so that fan-out from other channels (web UI) reaches Telegram immediately.
        RegisterEagerSinks();

        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        if (update.Message is not { } message) return;

        var username = message.From?.Username;
        if (username is null || !_allowedUsers.Contains(username)) return;

        var chatId = message.Chat.Id;
        var channelKey = chatId.ToString();

        var agentId = router.Resolve(chatId)
            ?? ResolveFromGroupTitle(message.Chat)
            ?? (_defaultAgent.Length > 0 ? _defaultAgent : null);

        if (agentId is null)
        {
            await SendResponseAsync(chatId, "No agent selected. Send .{letter} to choose one.", ct);
            return;
        }

        // Extract text content — either message text or file caption
        var text = message.Text;
        string? fileContext = null;

        // Handle documents and photos
        if (message.Document is not null || message.Photo is not null)
        {
            fileContext = await DownloadFileAsync(agentId, message, ct);
            text = message.Caption;
        }

        // Must have either text or a file to process
        if (text is null && fileContext is null) return;

        // Build the prompt: combine file context and user text
        var prompt = BuildPrompt(text, fileContext);

        var session = sessionRegistry.GetOrCreate(agentId);

        // Ensure Telegram sink is registered for this agent
        EnsureTelegramSinkRegistered(agentId, chatId);

        // Publish inbound message
        await session.PublishAsync(new AgentMessage(
            session.SessionId, Guid.NewGuid().ToString(),
            ChannelMessageOrigin.Telegram, agentId, prompt, DateTimeOffset.UtcNow), ct);

        // Fan out inbound message to other channels
        await fanOut.BroadcastAsync(agentId, $"[telegram] {prompt}", channelKey, ct);

        // Send typing indicator while processing
        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var typingTask = SendTypingIndicatorAsync(chatId, typingCts.Token);

        try
        {
            var schedulingCtx = new SchedulingContext(channelKey, ScheduleChannelType.Telegram, agentId);
            var (switchedTo, responseText) = await invoker.InvokeAsync(session, prompt, schedulingCtx, ct);

            if (!string.IsNullOrEmpty(responseText))
            {
                await SendResponseAsync(chatId, responseText, ct);

                // Fan out agent response to other channels
                await fanOut.BroadcastAsync(agentId, responseText, channelKey, ct);
            }

            if (switchedTo is not null)
            {
                router.Map(chatId, switchedTo);
                // Re-register sink under new agent
                EnsureTelegramSinkRegistered(switchedTo, chatId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to process Telegram update for chat {ChatId}", chatId);
            await SendResponseAsync(chatId, "[Agent error: failed to process request]", ct);
        }
        finally
        {
            await typingCts.CancelAsync();
            try { await typingTask; } catch (OperationCanceledException) { }
        }
    }

    private async Task<string?> DownloadFileAsync(string agentId, Message message, CancellationToken ct)
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

        var uploadsDir = Path.Combine(workspacePath, agentId, "uploads");
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
            return await BuildFileContextAsync(localPath, fileName, stream.Length, ct);
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

    private async Task<string> BuildFileContextAsync(string localPath, string fileName, long sizeBytes, CancellationToken ct)
    {
        var savedLine = $"[File saved: {localPath}]";
        var extension = Path.GetExtension(fileName);
        var sections = new List<string> { savedLine };

        if (CsvExtensions.Contains(extension))
        {
            var csvSummary = await BuildCsvSummaryAsync(localPath, extension, ct);
            if (!string.IsNullOrWhiteSpace(csvSummary))
                sections.Add(csvSummary);
        }
        else if (SpreadsheetExtensions.Contains(extension))
        {
            var spreadsheetSummary = await BuildSpreadsheetSummaryAsync(localPath, ct);
            if (!string.IsNullOrWhiteSpace(spreadsheetSummary))
                sections.Add(spreadsheetSummary);
        }

        if (!InlineTextExtensions.Contains(extension) || sizeBytes > InlineTextFileMaxBytes)
            return string.Join('\n', sections);

        try
        {
            var text = await File.ReadAllTextAsync(localPath, ct);
            if (string.IsNullOrWhiteSpace(text))
                return string.Join('\n', sections.Append($"[File contents: empty {fileName}]"));

            const int previewLimit = 50_000;
            var inlineText = text.Length <= previewLimit ? text : text[..previewLimit];
            var truncationNote = text.Length > previewLimit ? $"\n[Content truncated to first {previewLimit} characters]" : string.Empty;

            sections.Add($"[File contents: {fileName}]\n{inlineText}{truncationNote}");
            return string.Join('\n', sections);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to inline uploaded text file {Path}; falling back to path-only context", localPath);
            return string.Join('\n', sections);
        }
    }

    private async Task<string?> BuildCsvSummaryAsync(string localPath, string extension, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(localPath);
            using var reader = new StreamReader(stream);
            using var parser = new TextFieldParser(reader)
            {
                TextFieldType = FieldType.Delimited,
                HasFieldsEnclosedInQuotes = true,
                TrimWhiteSpace = false,
            };

            parser.SetDelimiters(extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? "\t" : ",");

            var rows = new List<string[]>();
            while (!parser.EndOfData && rows.Count < 6)
            {
                ct.ThrowIfCancellationRequested();
                var fields = parser.ReadFields();
                if (fields is not null)
                    rows.Add(fields);
            }

            if (rows.Count == 0)
                return "[CSV summary: empty file]";

            var headers = rows[0];
            var dataRows = rows.Count > 1 ? rows.Skip(1).ToList() : [];
            var sampleCount = dataRows.Count;

            var summary = new List<string>
            {
                $"[CSV summary: {Path.GetFileName(localPath)}]",
                $"Columns ({headers.Length}): {string.Join(", ", headers)}",
                $"Sample rows shown: {sampleCount}"
            };

            for (var i = 0; i < dataRows.Count; i++)
            {
                summary.Add($"Row {i + 1}: {string.Join(" | ", dataRows[i])}");
            }

            return string.Join('\n', summary);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to summarize CSV file {Path}", localPath);
            return null;
        }
    }

    private async Task<string?> BuildSpreadsheetSummaryAsync(string localPath, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var workbook = new XLWorkbook(localPath);
                if (workbook.Worksheets.Count == 0)
                    return "[Spreadsheet summary: workbook has no worksheets]";

                var summary = new List<string>
                {
                    $"[Spreadsheet summary: {Path.GetFileName(localPath)}]",
                    $"Worksheets ({workbook.Worksheets.Count}): {string.Join(", ", workbook.Worksheets.Select(sheet => sheet.Name))}"
                };

                var firstSheet = workbook.Worksheets.First();
                var usedRange = firstSheet.RangeUsed();
                if (usedRange is null)
                {
                    summary.Add($"Sheet '{firstSheet.Name}' is empty.");
                    return string.Join('\n', summary);
                }

                var rows = usedRange.RowsUsed().Take(6).ToList();
                if (rows.Count == 0)
                {
                    summary.Add($"Sheet '{firstSheet.Name}' is empty.");
                    return string.Join('\n', summary);
                }

                var headers = rows[0]
                    .CellsUsed(XLCellsUsedOptions.AllContents)
                    .Select(cell => cell.GetFormattedString())
                    .ToList();
                var headerText = headers.Count == 0 ? "(no populated header cells)" : string.Join(", ", headers);
                summary.Add($"First sheet: {firstSheet.Name}");
                summary.Add($"Columns ({headers.Count}): {headerText}");

                var sampleRows = rows.Skip(1).ToList();
                summary.Add($"Sample rows shown: {sampleRows.Count}");

                for (var i = 0; i < sampleRows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var values = sampleRows[i]
                        .Cells(1, usedRange.ColumnCount())
                        .Select(cell => cell.GetFormattedString())
                        .ToArray();
                    summary.Add($"Row {i + 1}: {string.Join(" | ", values)}");
                }

                return string.Join('\n', summary);
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to summarize spreadsheet file {Path}", localPath);
            return null;
        }
    }

    private async Task SendResponseAsync(long chatId, string text, CancellationToken ct)
    {
        foreach (var chunk in SplitMessage(text, TelegramMessageChunkSize))
        {
            try
            {
                await botClient.SendMessage(chatId, chunk, parseMode: ParseMode.Html, cancellationToken: ct);
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(ex, "HTML parse failed for Telegram response; retrying as plain text");
                await botClient.SendMessage(chatId, chunk, cancellationToken: ct);
            }
        }
    }

    private static IReadOnlyList<string> SplitMessage(string text, int maxChunkLength)
    {
        if (text.Length <= maxChunkLength)
            return [text];

        var chunks = new List<string>();
        var remaining = text;

        while (remaining.Length > maxChunkLength)
        {
            var breakAt = remaining.LastIndexOf('\n', maxChunkLength);
            if (breakAt <= 0)
                breakAt = maxChunkLength;

            chunks.Add(remaining[..breakAt]);
            remaining = remaining[breakAt..].TrimStart('\n');
        }

        if (remaining.Length > 0)
            chunks.Add(remaining);

        return chunks;
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

    private void RegisterEagerSinks()
    {
        // Register sinks for agents with explicit telegram_chat_id in frontmatter
        foreach (var agent in agentRegistry.GetAll())
        {
            if (agent is AgentDefinition { TelegramChatId: { } chatId })
            {
                EnsureTelegramSinkRegistered(agent.Name, chatId);
                logger.LogInformation(
                    "Eagerly registered Telegram sink for agent {Agent} → chat {ChatId}",
                    agent.Name, chatId);
            }
        }

        // Discover group chats from AllowedChatIds whose title matches an agent name
        _ = DiscoverGroupSinksAsync();
    }

    private async Task DiscoverGroupSinksAsync()
    {
        var allowedChatIds = telegramOptions.Value.AllowedChatIds;
        if (allowedChatIds is null || allowedChatIds.Count == 0)
            return;

        foreach (var chatId in allowedChatIds)
        {
            try
            {
                var chat = await botClient.GetChat(chatId);
                if (chat.Type is not (ChatType.Group or ChatType.Supergroup))
                    continue;

                var title = chat.Title;
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var agent = agentRegistry.Get(title);
                if (agent is null)
                    continue;

                // Already registered via frontmatter — skip
                if (agent is AgentDefinition { TelegramChatId: not null })
                    continue;

                EnsureTelegramSinkRegistered(agent.Name, chatId);
                router.Map(chatId, agent.Name);
                logger.LogInformation(
                    "Discovered Telegram group '{Title}' (chat {ChatId}) → agent {Agent}",
                    title, chatId, agent.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to query Telegram chat {ChatId} during sink discovery", chatId);
            }
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception ex, CancellationToken ct)
    {
        logger.LogError(ex, "Telegram bot error");
        return Task.CompletedTask;
    }
}
