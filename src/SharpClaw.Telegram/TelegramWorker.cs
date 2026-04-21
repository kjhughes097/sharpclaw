using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SharpClaw.Telegram;

/// <summary>
/// Background worker that polls Telegram for updates and forwards messages
/// to the SharpClaw API, streaming responses back as Telegram messages.
/// </summary>
public sealed class TelegramWorker : BackgroundService
{
    private readonly ILogger<TelegramWorker> _logger;
    private readonly SharpClawApiClient _apiClient;
    private readonly TelegramOptions _options;
    private readonly HashSet<string> _allowedUsers;

    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "help", "new", "agents", "status"
    };

    // Single-letter agent shortcuts: .a = ade, .c = cody, etc.
    private static readonly Dictionary<char, string> AgentShortcuts = new()
    {
        ['a'] = "ade",
        ['c'] = "cody",
        ['d'] = "debbie",
        ['f'] = "fin",
        ['m'] = "myles",
        ['n'] = "noah",
        ['p'] = "paige",
        ['r'] = "remy",
    };

    // Maps Telegram chatId → SharpClaw chatSlug for conversation continuity
    private readonly ConcurrentDictionary<long, string> _chatMap = new();

    // Maps Telegram chatId → current SharpClaw project slug
    private readonly ConcurrentDictionary<long, string> _projectMap = new();

    // Maps Telegram chatId → last-used agent slug
    private readonly ConcurrentDictionary<long, string> _lastAgentMap = new();

    // Tracks which Telegram chats currently have an in-flight request
    private readonly ConcurrentDictionary<long, bool> _processing = new();

    public TelegramWorker(
        ILogger<TelegramWorker> logger,
        SharpClawApiClient apiClient,
        IOptions<TelegramOptions> options)
    {
        _logger = logger;
        _apiClient = apiClient;
        _options = options.Value;

        _allowedUsers = string.IsNullOrWhiteSpace(_options.AllowedUsers)
            ? []
            : _options.AllowedUsers
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.TrimStart('@').ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            _logger.LogError("Telegram bot token is not configured. Set Telegram:BotToken.");
            return;
        }

        _apiClient.Configure(_options.ApiBaseUrl, _options.ApiKey);

        var bot = new TelegramBotClient(_options.BotToken);
        var me = await bot.GetMe(stoppingToken);
        _logger.LogInformation("Telegram bot started: @{BotUsername} (id: {BotId})", me.Username, me.Id);

        bot.StartReceiving(
            updateHandler: (client, update, ct) => HandleUpdateAsync(client, update, ct),
            errorHandler: (_, exception, _, ct) =>
            {
                _logger.LogError(exception, "Telegram polling error");
                return Task.CompletedTask;
            },
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message],
                DropPendingUpdates = true,
            },
            cancellationToken: stoppingToken);

        // Keep the service alive
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { Text: { Length: > 0 } text } message)
            return;

        var chatId = message.Chat.Id;
        var username = message.From?.Username?.ToLowerInvariant();

        // Access control
        if (_allowedUsers.Count > 0 && (username is null || !_allowedUsers.Contains(username)))
        {
            _logger.LogWarning("Unauthorized user @{Username} in chat {ChatId}", username ?? "(none)", chatId);
            await bot.SendMessage(chatId, "⛔ You are not authorized to use this bot.", cancellationToken: ct);
            return;
        }

        // Prevent concurrent requests per chat
        if (!_processing.TryAdd(chatId, true))
        {
            await bot.SendMessage(chatId, "⏳ Still processing your last message...", cancellationToken: ct);
            return;
        }

        try
        {
            await ProcessMessageAsync(bot, chatId, text, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from chat {ChatId}", chatId);
            await bot.SendMessage(chatId, "❌ Something went wrong. Please try again.", cancellationToken: ct);
        }
        finally
        {
            _processing.TryRemove(chatId, out _);
        }
    }

    private async Task ProcessMessageAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken ct)
    {
        // Telegram auto-sends /start when a user first opens the bot
        if (text.StartsWith('/'))
        {
            var parts = text.Split(' ', 2);
            var slashWord = parts[0][1..].ToLowerInvariant();
            if (KnownCommands.Contains(slashWord))
            {
                var cmdArgs = parts.Length > 1 ? parts[1].Trim() : "";
                await HandleCommandAsync(bot, chatId, slashWord, cmdArgs, ct);
                return;
            }
        }

        // Parse .command or .{letter} agent shortcut
        string? agentSlug = null;
        var messageText = text;
        if (text.StartsWith('.') && text.Length > 1)
        {
            var rest = text[1..];
            var spaceIdx = rest.IndexOf(' ');
            var word = spaceIdx > 0 ? rest[..spaceIdx] : rest;

            if (KnownCommands.Contains(word))
            {
                var cmdArgs = spaceIdx > 0 ? rest[(spaceIdx + 1)..].Trim() : "";
                await HandleCommandAsync(bot, chatId, word.ToLowerInvariant(), cmdArgs, ct);
                return;
            }

            // Single-letter agent shortcut: .c hello → cody
            if (word.Length == 1 && AgentShortcuts.TryGetValue(char.ToLowerInvariant(word[0]), out var resolved))
            {
                agentSlug = resolved;
                messageText = spaceIdx > 0 ? rest[(spaceIdx + 1)..] : "";
                if (string.IsNullOrWhiteSpace(messageText))
                {
                    // Just switching agent, no message
                    _lastAgentMap[chatId] = agentSlug;
                    await bot.SendMessage(chatId, $"🔀 Switched to *{agentSlug}*.",
                        parseMode: ParseMode.Markdown, cancellationToken: ct);
                    return;
                }
            }
            else if (spaceIdx > 0)
            {
                // Full agent slug: .cody hello
                agentSlug = word;
                messageText = rest[(spaceIdx + 1)..];
            }
        }

        // Fall back to last-used agent if none specified
        agentSlug ??= _lastAgentMap.GetValueOrDefault(chatId);

        // Send "typing" indicator
        await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

        // Get current project and existing chat slug for continuity
        var project = _projectMap.GetValueOrDefault(chatId, _options.DefaultProject);
        _chatMap.TryGetValue(chatId, out var chatSlug);

        // Stream the response, collecting chunks for periodic edits
        var response = new StringBuilder();
        var lastEditLength = 0;
        Message? sentMessage = null;
        var lastEditTime = DateTimeOffset.UtcNow;

        var (fullText, agent, newChatSlug, inputTokens, outputTokens) = await _apiClient.SendMessageAsync(
            messageText,
            project,
            chatSlug,
            agentSlug,
            async chunk =>
            {
                response.Append(chunk);

                // Send initial message or edit periodically (every ~1.5s and >20 new chars)
                var now = DateTimeOffset.UtcNow;
                var newChars = response.Length - lastEditLength;
                if (newChars < 20 || (now - lastEditTime).TotalMilliseconds < 1500)
                    return;

                var currentText = TruncateForTelegram(response.ToString());

                try
                {
                    if (sentMessage is null)
                    {
                        sentMessage = await bot.SendMessage(chatId, currentText + " ▍",
                            parseMode: ParseMode.Markdown, cancellationToken: ct);
                    }
                    else
                    {
                        await bot.EditMessageText(chatId, sentMessage.MessageId, currentText + " ▍",
                            parseMode: ParseMode.Markdown, cancellationToken: ct);
                    }

                    lastEditLength = response.Length;
                    lastEditTime = now;
                }
                catch
                {
                    // Telegram rate limit or markdown parse error — skip this edit
                }
            },
            ct);

        // Save chat slug and last agent for continuity
        if (newChatSlug is not null)
            _chatMap[chatId] = newChatSlug;
        if (agent is not null)
            _lastAgentMap[chatId] = agent;

        // Final message with complete text
        var finalText = TruncateForTelegram(fullText);
        if (string.IsNullOrWhiteSpace(finalText))
            finalText = "_No response from agent._";

        var footer = $"\n\n_— {agent}_";
        if (inputTokens > 0 || outputTokens > 0)
            footer = $"\n\n_— {agent} | {FormatTokens(inputTokens)} in / {FormatTokens(outputTokens)} out_";

        try
        {
            if (sentMessage is null)
            {
                await bot.SendMessage(chatId, finalText + footer,
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
            else
            {
                await bot.EditMessageText(chatId, sentMessage.MessageId, finalText + footer,
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
            }
        }
        catch
        {
            // Markdown failed — send as plain text
            try
            {
                if (sentMessage is null)
                    await bot.SendMessage(chatId, finalText + $"\n\n— {agent}", cancellationToken: ct);
                else
                    await bot.EditMessageText(chatId, sentMessage.MessageId, finalText + $"\n\n— {agent}",
                        cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send final message to chat {ChatId}", chatId);
            }
        }
    }

    private async Task HandleCommandAsync(ITelegramBotClient bot, long chatId, string command, string args, CancellationToken ct)
    {
        switch (command)
        {
            case "start":
            case "help":
                await bot.SendMessage(chatId,
                    "👋 *Welcome to SharpClaw!*\n\n" +
                    "Send me any message and I'll route it to the best agent.\n\n" +
                    "*Agent shortcuts:*\n" +
                    "`.a` — Ade (helper/delegator)\n" +
                    "`.c` — Cody (developer)\n" +
                    "`.d` — Debbie (debugger)\n" +
                    "`.f` — Fin (finance)\n" +
                    "`.m` — Myles (running)\n" +
                    "`.n` — Noah (knowledge)\n" +
                    "`.p` — Paige (media/comms)\n" +
                    "`.r` — Remy (todos/reminders)\n\n" +
                    "`.new` — New conversation via Ade (routing mode)\n\n" +
                    "No prefix → continues with the last agent used.\n\n" +
                    "*Commands:*\n" +
                    "`.help` — Show this message\n" +
                    "`.agents` — List agents\n" +
                    "`.status` — Show current session",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "new":
                _chatMap.TryRemove(chatId, out _);
                _lastAgentMap[chatId] = "ade";
                await bot.SendMessage(chatId, "🔄 New conversation started with *Ade* (routing mode).",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "agents":
                await bot.SendMessage(chatId,
                    "*Available Agents:*\n" +
                    "• `.a` *Ade* — Helper & delegator\n" +
                    "• `.c` *Cody* — Software architect & developer\n" +
                    "• `.d` *Debbie* — Debugging & troubleshooting\n" +
                    "• `.f` *Fin* — Finance & budgets\n" +
                    "• `.m` *Myles* — Trail & ultra running\n" +
                    "• `.n` *Noah* — Knowledge curator\n" +
                    "• `.p` *Paige* — Media & communications\n" +
                    "• `.r` *Remy* — Todos & reminders\n\n" +
                    "Use `.{letter} your message` to target one.",
                    parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            case "status":
                var statusProject = _projectMap.GetValueOrDefault(chatId, _options.DefaultProject);
                var hasChat = _chatMap.TryGetValue(chatId, out var currentSlug);
                var currentAgent = _lastAgentMap.GetValueOrDefault(chatId, "(none)");
                var statusText = $"📋 Project: `{statusProject}`\n" +
                    $"Agent: `{currentAgent}`\n" +
                    (hasChat ? $"Chat: `{currentSlug}`" : "Chat: _(none — next message starts a new one)_");
                await bot.SendMessage(chatId, statusText, parseMode: ParseMode.Markdown, cancellationToken: ct);
                break;

            default:
                await bot.SendMessage(chatId, "Unknown command. Try `.help` for a list.", cancellationToken: ct);
                break;
        }
    }

    /// <summary>
    /// Truncates text to fit within Telegram's 4096-character message limit.
    /// </summary>
    private static string TruncateForTelegram(string text)
    {
        const int maxLength = 4000; // leave room for footer
        if (text.Length <= maxLength)
            return text;

        return text[..maxLength] + "\n\n_(truncated)_";
    }

    /// <summary>Formats a token count as a human-readable string (e.g. 1.2k, 45.3k).</summary>
    private static string FormatTokens(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(),
        < 10_000 => $"{tokens / 1000.0:F1}k",
        < 1_000_000 => $"{tokens / 1000.0:F0}k",
        _ => $"{tokens / 1_000_000.0:F1}M"
    };
}
