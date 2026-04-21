using System.Text.Json;

namespace SharpClaw.Core;

/// <summary>
/// Manages chat folders within a project. Each chat is a directory under
/// projects/{slug}/chats/{chat-slug}/ containing messages.json, context.md, and log.md.
/// </summary>
public sealed class ChatManager
{
    private readonly string _projectsRoot;

    public ChatManager(string projectsRoot)
    {
        _projectsRoot = projectsRoot;
    }

    /// <summary>Lists all chats within a project.</summary>
    public IReadOnlyList<ChatInfo> ListChats(string projectSlug)
    {
        var chatsDir = Path.Combine(_projectsRoot, projectSlug, "chats");
        if (!Directory.Exists(chatsDir))
            return [];

        return Directory.GetDirectories(chatsDir)
            .Select(LoadChatInfo)
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();
    }

    /// <summary>Gets a single chat by project and chat slug.</summary>
    public ChatInfo? GetChat(string projectSlug, string chatSlug)
    {
        var chatDir = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug);
        return Directory.Exists(chatDir) ? LoadChatInfo(chatDir) : null;
    }

    /// <summary>Creates a new chat folder with initial files.</summary>
    public ChatInfo CreateChat(string projectSlug, string title)
    {
        var chatsDir = Path.Combine(_projectsRoot, projectSlug, "chats");
        Directory.CreateDirectory(chatsDir);

        var slug = GenerateChatSlug(title);
        var chatDir = Path.Combine(chatsDir, slug);

        if (Directory.Exists(chatDir))
            throw new InvalidOperationException($"Chat '{slug}' already exists in project '{projectSlug}'.");

        Directory.CreateDirectory(chatDir);

        var now = DateTimeOffset.UtcNow;

        // Empty messages array
        File.WriteAllText(Path.Combine(chatDir, "messages.json"), "[]");

        // Chat context
        File.WriteAllText(Path.Combine(chatDir, "context.md"), $"""
            # Chat: {title}
            Created: {now:yyyy-MM-dd HH:mm}

            ## Summary

            """);

        // Chat-level event log
        File.WriteAllText(Path.Combine(chatDir, "log.md"), $"# {title} — Chat Log\n\n");

        return new ChatInfo(slug, title, LastAgent: null, now, now);
    }

    /// <summary>Loads the full message history for a chat.</summary>
    public IReadOnlyList<ChatMessage> GetMessages(string projectSlug, string chatSlug)
    {
        var path = MessagesPath(projectSlug, chatSlug);
        if (!File.Exists(path))
            return [];

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOpts) ?? [];
    }

    /// <summary>Appends a message to the chat history.</summary>
    public void AppendMessage(string projectSlug, string chatSlug, ChatMessage message)
    {
        var path = MessagesPath(projectSlug, chatSlug);
        var messages = File.Exists(path)
            ? JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(path), JsonOpts) ?? []
            : [];

        messages.Add(message);
        File.WriteAllText(path, JsonSerializer.Serialize(messages, JsonOpts));

        // Touch context.md to update last-activity timestamp
        var contextPath = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "context.md");
        if (File.Exists(contextPath))
            File.SetLastWriteTimeUtc(contextPath, DateTime.UtcNow);
    }

    /// <summary>Deletes a chat and all its contents.</summary>
    public bool DeleteChat(string projectSlug, string chatSlug)
    {
        var chatDir = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug);
        if (!Directory.Exists(chatDir))
            return false;

        Directory.Delete(chatDir, recursive: true);
        return true;
    }

    public string GetChatPath(string projectSlug, string chatSlug) =>
        Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug);

    /// <summary>Loads ChatInfo from a chat directory. Used by both ChatManager and ProjectManager.</summary>
    internal static ChatInfo? LoadChatInfo(string chatDir)
    {
        if (!Directory.Exists(chatDir))
            return null;

        var slug = Path.GetFileName(chatDir);
        var info = new DirectoryInfo(chatDir);
        var title = slug;
        string? lastAgent = null;

        var contextPath = Path.Combine(chatDir, "context.md");
        if (File.Exists(contextPath))
        {
            var firstLine = File.ReadLines(contextPath).FirstOrDefault() ?? "";
            if (firstLine.StartsWith("# Chat: "))
                title = firstLine["# Chat: ".Length..].Trim();
        }

        // Try to determine last agent from most recent assistant message
        var messagesPath = Path.Combine(chatDir, "messages.json");
        if (File.Exists(messagesPath))
        {
            try
            {
                var json = File.ReadAllText(messagesPath);
                var messages = JsonSerializer.Deserialize<List<ChatMessage>>(json, JsonOpts);
                lastAgent = messages?.LastOrDefault(m => m.Role == ChatRole.Assistant)?.AgentSlug;
            }
            catch
            {
                // Ignore corrupt messages file
            }
        }

        // Load token usage totals
        var inputTokens = 0;
        var outputTokens = 0;
        var usagePath = Path.Combine(chatDir, "usage.json");
        if (File.Exists(usagePath))
        {
            try
            {
                var usageJson = File.ReadAllText(usagePath);
                var records = JsonSerializer.Deserialize<List<TokenUsageRecord>>(usageJson, JsonOpts);
                if (records is not null)
                {
                    foreach (var r in records)
                    {
                        inputTokens += r.InputTokens;
                        outputTokens += r.OutputTokens;
                    }
                }
            }
            catch { /* ignore corrupt usage file */ }
        }

        return new ChatInfo(
            slug,
            title,
            lastAgent,
            info.CreationTimeUtc,
            File.Exists(contextPath) ? File.GetLastWriteTimeUtc(contextPath) : info.LastWriteTimeUtc,
            TotalInputTokens: inputTokens,
            TotalOutputTokens: outputTokens
        );
    }

    private string MessagesPath(string projectSlug, string chatSlug) =>
        Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "messages.json");

    private string UsagePath(string projectSlug, string chatSlug) =>
        Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "usage.json");

    /// <summary>Appends a token usage record for a turn.</summary>
    public void AppendUsage(string projectSlug, string chatSlug, TokenUsageRecord record)
    {
        var path = UsagePath(projectSlug, chatSlug);
        var records = File.Exists(path)
            ? JsonSerializer.Deserialize<List<TokenUsageRecord>>(File.ReadAllText(path), JsonOpts) ?? []
            : [];

        records.Add(record);
        File.WriteAllText(path, JsonSerializer.Serialize(records, JsonOpts));
    }

    /// <summary>Gets all token usage records for a chat.</summary>
    public IReadOnlyList<TokenUsageRecord> GetUsage(string projectSlug, string chatSlug)
    {
        var path = UsagePath(projectSlug, chatSlug);
        if (!File.Exists(path))
            return [];

        return JsonSerializer.Deserialize<List<TokenUsageRecord>>(File.ReadAllText(path), JsonOpts) ?? [];
    }

    private static string GenerateChatSlug(string title)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var chars = new List<char>();
        var prevDash = false;

        foreach (var ch in title.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                chars.Add(ch);
                prevDash = false;
            }
            else if (!prevDash && chars.Count > 0)
            {
                chars.Add('-');
                prevDash = true;
            }
        }

        var titlePart = new string(chars.ToArray()).TrimEnd('-');
        if (titlePart.Length > 40)
            titlePart = titlePart[..40].TrimEnd('-');

        return string.IsNullOrWhiteSpace(titlePart) ? timestamp : $"{timestamp}-{titlePart}";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
