using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpClaw.Core;

namespace SharpClaw.Api.Services;

/// <summary>
/// Generates session recap summaries and persists them as knowledge Markdown files
/// in the workspace's <c>knowledge</c> folder.
/// </summary>
public sealed partial class KnowledgeService(SessionStore store, ILogger<KnowledgeService> logger)
{
    private const string KnowledgeFolderName = "knowledge";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Generates a recap of the archived session and writes it to the knowledge folder.
    /// Returns the relative path of the created file, or <c>null</c> if the session has no messages.
    /// </summary>
    public string? GenerateAndStore(string sessionId, string agentSlug, string personaName)
    {
        var transcript = store.GetSessionTranscript(sessionId);
        if (string.IsNullOrWhiteSpace(transcript))
            return null;

        var workspacePath = store.GetWorkspacePath();
        var knowledgeDir = Path.Combine(workspacePath, KnowledgeFolderName);
        Directory.CreateDirectory(knowledgeDir);

        var summary = BuildSummary(transcript, personaName);
        var tags = ExtractTags(transcript, personaName);
        var now = DateTimeOffset.UtcNow;
        var safeTitle = SanitizeFileName(summary.Title);
        var shortId = sessionId.Length > 8 ? sessionId[..8] : sessionId;
        var filename = $"{now:yyyy-MM-dd}-{shortId}-{safeTitle}.md";
        var filePath = Path.Combine(knowledgeDir, filename);

        var content = FormatKnowledgeFile(summary, tags, sessionId, agentSlug, personaName, now);
        File.WriteAllText(filePath, content, Encoding.UTF8);

        logger.LogInformation("Knowledge file created: {FilePath}", Path.Combine(KnowledgeFolderName, filename));
        return Path.Combine(KnowledgeFolderName, filename);
    }

    /// <summary>
    /// Lists all knowledge files in the knowledge folder, returning their metadata.
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> ListKnowledge()
    {
        var workspacePath = store.GetWorkspacePath();
        var knowledgeDir = Path.Combine(workspacePath, KnowledgeFolderName);

        if (!Directory.Exists(knowledgeDir))
            return [];

        var entries = new List<KnowledgeEntry>();
        foreach (var file in Directory.EnumerateFiles(knowledgeDir, "*.md").OrderByDescending(f => f))
        {
            try
            {
                var content = File.ReadAllText(file, Encoding.UTF8);
                var entry = ParseKnowledgeFile(content, Path.GetFileName(file));
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse knowledge file: {File}", file);
            }
        }

        return entries;
    }

    /// <summary>
    /// Finds knowledge entries that share any of the provided tags.
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> FindRelatedKnowledge(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
            return [];

        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        return ListKnowledge()
            .Where(entry => entry.Tags.Any(t => tagSet.Contains(t)))
            .ToList();
    }

    private static SessionSummaryResult BuildSummary(string transcript, string personaName)
    {
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var userMessages = new List<string>();
        var assistantMessages = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("User: ", StringComparison.Ordinal))
                userMessages.Add(line["User: ".Length..].Trim());
            else if (line.StartsWith("Assistant: ", StringComparison.Ordinal))
                assistantMessages.Add(line["Assistant: ".Length..].Trim());
        }

        // Build a title from the first user message
        var title = userMessages.Count > 0
            ? Truncate(userMessages[0], 60)
            : "Untitled Session";

        // Build a recap from key points
        var recap = new StringBuilder();
        recap.AppendLine($"Session with {personaName} containing {userMessages.Count + assistantMessages.Count} messages.");
        recap.AppendLine();

        if (userMessages.Count > 0)
        {
            recap.AppendLine("**Key topics discussed:**");
            foreach (var msg in userMessages.Take(5))
                recap.AppendLine($"- {Truncate(msg, 120)}");
            recap.AppendLine();
        }

        if (assistantMessages.Count > 0)
        {
            recap.AppendLine("**Key insights provided:**");
            foreach (var msg in assistantMessages.Take(3))
                recap.AppendLine($"- {Truncate(msg, 200)}");
        }

        return new SessionSummaryResult(title, recap.ToString().TrimEnd());
    }

    private static List<string> ExtractTags(string transcript, string personaName)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            personaName.ToLowerInvariant(),
            "session-archive"
        };

        // Extract potential topic keywords from user messages
        var words = transcript
            .Split([' ', '\n', '\r', '\t', '.', ',', '!', '?', ':', ';', '(', ')', '[', ']', '{', '}', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(w => w.ToLowerInvariant())
            .Where(w => !StopWords.Contains(w));

        // Count word frequency and take top tags
        var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in words)
        {
            frequency.TryGetValue(word, out var count);
            frequency[word] = count + 1;
        }

        var topWords = frequency
            .OrderByDescending(kvp => kvp.Value)
            .Take(5)
            .Select(kvp => kvp.Key);

        foreach (var word in topWords)
            tags.Add(word);

        return [.. tags];
    }

    private static string FormatKnowledgeFile(
        SessionSummaryResult summary,
        List<string> tags,
        string sessionId,
        string agentSlug,
        string personaName,
        DateTimeOffset timestamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{EscapeYaml(summary.Title)}\"");
        sb.AppendLine($"session_id: \"{sessionId}\"");
        sb.AppendLine($"agent: \"{agentSlug}\"");
        sb.AppendLine($"persona: \"{EscapeYaml(personaName)}\"");
        sb.AppendLine($"archived_at: \"{timestamp.ToString("o", CultureInfo.InvariantCulture)}\"");
        sb.AppendLine($"tags: [{string.Join(", ", tags.Select(t => $"\"{EscapeYaml(t)}\""))}]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {summary.Title}");
        sb.AppendLine();
        sb.AppendLine(summary.Recap);

        return sb.ToString();
    }

    private static KnowledgeEntry? ParseKnowledgeFile(string content, string filename)
    {
        if (!content.StartsWith("---", StringComparison.Ordinal))
            return null;

        var endFrontmatter = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endFrontmatter < 0)
            return null;

        var frontmatter = content[3..endFrontmatter].Trim();
        var body = content[(endFrontmatter + 4)..].Trim();

        string? title = null;
        string? sessionId = null;
        string? agent = null;
        string? persona = null;
        DateTimeOffset? archivedAt = null;
        var tags = new List<string>();

        foreach (var line in frontmatter.Split('\n'))
        {
            if (TryParseYamlValue(line, "title", out var v))
                title = v;
            else if (TryParseYamlValue(line, "session_id", out v))
                sessionId = v;
            else if (TryParseYamlValue(line, "agent", out v))
                agent = v;
            else if (TryParseYamlValue(line, "persona", out v))
                persona = v;
            else if (TryParseYamlValue(line, "archived_at", out v) && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                archivedAt = dt;
            else if (line.TrimStart().StartsWith("tags:", StringComparison.Ordinal))
            {
                var tagsMatch = TagsArrayRegex().Match(line);
                if (tagsMatch.Success)
                {
                    var tagsContent = tagsMatch.Groups[1].Value;
                    tags.AddRange(TagItemRegex().Matches(tagsContent).Select(m => m.Groups[1].Value));
                }
            }
        }

        return new KnowledgeEntry(
            Filename: filename,
            Title: title ?? filename,
            SessionId: sessionId,
            Agent: agent,
            Persona: persona,
            ArchivedAt: archivedAt,
            Tags: tags,
            Summary: body);
    }

    private static bool TryParseYamlValue(string line, string key, out string value)
    {
        value = string.Empty;
        var prefix = $"{key}:";
        if (!line.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var raw = line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"'))
            raw = raw[1..^1];
        value = raw;
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = InvalidFileCharsRegex().Replace(name, "-");
        sanitized = sanitized.Replace(' ', '-');
        sanitized = sanitized.Trim('-');
        return sanitized.Length > 40 ? sanitized[..40].TrimEnd('-') : sanitized;
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string EscapeYaml(string value)
        => value.Replace("\"", "\\\"");

    [GeneratedRegex(@"\[(.+?)\]")]
    private static partial Regex TagsArrayRegex();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex TagItemRegex();

    [GeneratedRegex(@"[^\w\s-]", RegexOptions.Compiled)]
    private static partial Regex InvalidFileCharsRegex();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "from", "have", "will", "been", "they", "them",
        "their", "about", "would", "could", "should", "there", "where", "which",
        "what", "when", "your", "more", "some", "than", "other", "into", "also",
        "just", "very", "like", "make", "know", "back", "only", "come", "made",
        "after", "being", "user", "assistant", "please", "help", "want", "need",
        "does", "each", "here", "most", "much", "then", "these", "those",
    };
}

public sealed record SessionSummaryResult(string Title, string Recap);

public sealed record KnowledgeEntry(
    string Filename,
    string Title,
    string? SessionId,
    string? Agent,
    string? Persona,
    DateTimeOffset? ArchivedAt,
    IReadOnlyList<string> Tags,
    string Summary);
