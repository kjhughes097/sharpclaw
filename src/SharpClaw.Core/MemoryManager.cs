namespace SharpClaw.Core;

/// <summary>
/// Reads and writes the 4-layer memory system:
///   Layer 1 — Interaction Log  (append-only, per-chat and per-project)
///   Layer 2 — Chat Context     (rewritten after each turn)
///   Layer 3 — Project Context  (updated after each chat interaction)
///   Layer 4 — Long-Term Knowledge (global: facts.md, learned.md, archived-projects.md)
/// </summary>
public sealed class MemoryManager
{
    private readonly string _projectsRoot;
    private readonly string _knowledgeRoot;

    public MemoryManager(string projectsRoot, string knowledgeRoot)
    {
        _projectsRoot = projectsRoot;
        _knowledgeRoot = knowledgeRoot;
        EnsureKnowledgeFiles();
    }

    // ── Layer 1: Interaction Log (append-only) ──────────────────────────

    /// <summary>Appends a log entry to the chat-level log.</summary>
    public void AppendChatLog(string projectSlug, string chatSlug, string agentName, string summary)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "log.md");
        EnsureFileExists(path, $"# Chat Log\n\n");
        var entry = FormatLogEntry(agentName, summary);
        File.AppendAllText(path, entry);
    }

    /// <summary>Appends a log entry to the project-level log.</summary>
    public void AppendProjectLog(string projectSlug, string agentName, string summary)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "log.md");
        EnsureFileExists(path, $"# Project Log\n\n");
        var entry = FormatLogEntry(agentName, summary);
        File.AppendAllText(path, entry);
    }

    /// <summary>Reads the chat-level log.</summary>
    public string ReadChatLog(string projectSlug, string chatSlug)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "log.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Reads the project-level log.</summary>
    public string ReadProjectLog(string projectSlug)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "log.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    // ── Layer 2: Chat Context (rewritten per turn) ──────────────────────

    /// <summary>Reads the chat context summary.</summary>
    public string ReadChatContext(string projectSlug, string chatSlug)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "context.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Replaces the chat context summary entirely.</summary>
    public void WriteChatContext(string projectSlug, string chatSlug, string content)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "chats", chatSlug, "context.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── Layer 3: Project Context (updated per interaction) ──────────────

    /// <summary>Reads the project context summary.</summary>
    public string ReadProjectContext(string projectSlug)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "context.md");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Replaces the project context summary entirely.</summary>
    public void WriteProjectContext(string projectSlug, string content)
    {
        var path = Path.Combine(_projectsRoot, projectSlug, "context.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ── Layer 4: Long-Term Knowledge ────────────────────────────────────

    /// <summary>Reads a knowledge file (facts.md, learned.md, or archived-projects.md).</summary>
    public string ReadKnowledge(string fileName)
    {
        ValidateKnowledgeFileName(fileName);
        var path = Path.Combine(_knowledgeRoot, fileName);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>Replaces a knowledge file entirely.</summary>
    public void WriteKnowledge(string fileName, string content)
    {
        ValidateKnowledgeFileName(fileName);
        var path = Path.Combine(_knowledgeRoot, fileName);
        File.WriteAllText(path, content);
    }

    /// <summary>Appends content to a knowledge file.</summary>
    public void AppendKnowledge(string fileName, string content)
    {
        ValidateKnowledgeFileName(fileName);
        var path = Path.Combine(_knowledgeRoot, fileName);
        EnsureFileExists(path, $"# {Path.GetFileNameWithoutExtension(fileName)}\n\n");
        File.AppendAllText(path, content);
    }

    /// <summary>Reads all knowledge files concatenated, for injection into system prompts.</summary>
    public string ReadAllKnowledge()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in KnowledgeFiles)
        {
            var path = Path.Combine(_knowledgeRoot, file);
            if (!File.Exists(path)) continue;
            var content = File.ReadAllText(path).Trim();
            if (content.Length == 0) continue;
            sb.AppendLine($"<!-- {file} -->");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Assembles the full context block injected into an agent's system prompt.
    /// Ordered from broadest (knowledge) to narrowest (chat context).
    /// </summary>
    public string AssembleContext(string projectSlug, string chatSlug)
    {
        var sb = new System.Text.StringBuilder();

        var knowledge = ReadAllKnowledge();
        if (knowledge.Length > 0)
        {
            sb.AppendLine("<knowledge>");
            sb.AppendLine(knowledge.TrimEnd());
            sb.AppendLine("</knowledge>");
            sb.AppendLine();
        }

        var projectCtx = ReadProjectContext(projectSlug);
        if (projectCtx.Length > 0)
        {
            sb.AppendLine("<project-context>");
            sb.AppendLine(projectCtx.TrimEnd());
            sb.AppendLine("</project-context>");
            sb.AppendLine();
        }

        var chatCtx = ReadChatContext(projectSlug, chatSlug);
        if (chatCtx.Length > 0)
        {
            sb.AppendLine("<chat-context>");
            sb.AppendLine(chatCtx.TrimEnd());
            sb.AppendLine("</chat-context>");
        }

        return sb.ToString();
    }

    private void EnsureKnowledgeFiles()
    {
        Directory.CreateDirectory(_knowledgeRoot);
        foreach (var file in KnowledgeFiles)
        {
            var path = Path.Combine(_knowledgeRoot, file);
            if (!File.Exists(path))
            {
                var title = Path.GetFileNameWithoutExtension(file).Replace("-", " ");
                title = char.ToUpper(title[0]) + title[1..];
                File.WriteAllText(path, $"# {title}\n\n");
            }
        }
    }

    private static void EnsureFileExists(string path, string defaultContent)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, defaultContent);
    }

    private static string FormatLogEntry(string agentName, string summary)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm");
        return $"## {timestamp} — {agentName}\n{summary}\n\n";
    }

    private static void ValidateKnowledgeFileName(string fileName)
    {
        if (!KnowledgeFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Unknown knowledge file: {fileName}. Valid files: {string.Join(", ", KnowledgeFiles)}");
    }

    private static readonly string[] KnowledgeFiles = ["facts.md", "learned.md", "archived-projects.md"];
}
