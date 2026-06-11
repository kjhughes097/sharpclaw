using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

/// <summary>
/// Imports existing file-based memory (*.md) into the semantic memory store.
/// Splits content into paragraphs and stores each as a Fact memory.
/// </summary>
public sealed class MemoryImportService(
    MemoryService fileMemory,
    SemanticMemoryService semanticMemory,
    IOptions<SharpClawOptions> sharpClawOptions,
    ILogger<MemoryImportService> logger)
{
    /// <summary>
    /// Import all .md files from an agent's memory directory into semantic memory.
    /// Returns the number of memories stored (after dedup filtering).
    /// </summary>
    public async Task<ImportResult> ImportAgentMemoryAsync(string agentName, CancellationToken ct = default)
    {
        var agentDir = fileMemory.GetAgentMemoryPath(agentName);
        if (!Directory.Exists(agentDir))
        {
            logger.LogWarning("No memory directory found for agent {Agent} at {Path}", agentName, agentDir);
            return new ImportResult(0, 0, 0);
        }

        var files = Directory.GetFiles(agentDir, "*.md");
        var totalChunks = 0;
        var storedCount = 0;
        var skippedCount = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            // Skip audit files — they're append-only logs, not factual memory
            if (fileName.Equals("audit.md", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = await File.ReadAllTextAsync(file, ct);
            var chunks = SplitIntoChunks(content);

            foreach (var chunk in chunks)
            {
                totalChunks++;
                try
                {
                    await semanticMemory.StoreAsync(chunk, agentName, MemoryType.Fact, ct);
                    storedCount++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to store chunk from {File} for agent {Agent}", fileName, agentName);
                    skippedCount++;
                }
            }

            logger.LogDebug("Imported {File} for agent {Agent}: {Chunks} chunks", fileName, agentName, chunks.Count);
        }

        logger.LogInformation(
            "Import complete for agent {Agent}: {Files} files, {Total} chunks, {Stored} stored, {Skipped} skipped/deduped",
            agentName, files.Length, totalChunks, storedCount, skippedCount);

        return new ImportResult(totalChunks, storedCount, skippedCount);
    }

    /// <summary>
    /// Import all agents' memory files from the workspace.
    /// </summary>
    public async Task<Dictionary<string, ImportResult>> ImportAllAsync(CancellationToken ct = default)
    {
        var workspacePath = sharpClawOptions.Value.WorkspacePath;
        var results = new Dictionary<string, ImportResult>();

        if (!Directory.Exists(workspacePath))
        {
            logger.LogWarning("Workspace path not found: {Path}", workspacePath);
            return results;
        }

        var agentDirs = Directory.GetDirectories(workspacePath)
            .Where(d => !Path.GetFileName(d).Equals("knowledge", StringComparison.OrdinalIgnoreCase))
            .Where(d => !Path.GetFileName(d).Equals("projects", StringComparison.OrdinalIgnoreCase));

        foreach (var dir in agentDirs)
        {
            var agentName = Path.GetFileName(dir);
            var result = await ImportAgentMemoryAsync(agentName, ct);
            results[agentName] = result;
        }

        return results;
    }

    private static IReadOnlyList<string> SplitIntoChunks(string content)
    {
        var chunks = new List<string>();

        // Split by double newline (paragraphs) or markdown headers
        var paragraphs = content.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();

            // Skip very short content (headers alone, blank lines, etc.)
            if (trimmed.Length < 30) continue;

            // Skip markdown headers that are just structural
            if (trimmed.StartsWith('#') && !trimmed.Contains('\n')) continue;

            // Cap chunk size — split long paragraphs at sentence boundaries
            if (trimmed.Length > 500)
            {
                var sentences = SplitIntoSentences(trimmed);
                var buffer = "";
                foreach (var sentence in sentences)
                {
                    if (buffer.Length + sentence.Length > 500 && buffer.Length > 0)
                    {
                        chunks.Add(buffer.Trim());
                        buffer = "";
                    }
                    buffer += sentence + " ";
                }
                if (buffer.Trim().Length >= 30)
                    chunks.Add(buffer.Trim());
            }
            else
            {
                chunks.Add(trimmed);
            }
        }

        return chunks;
    }

    private static IReadOnlyList<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();
        var current = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                sentences.Add(text[current..(i + 1)]);
                current = i + 2;
            }
        }

        if (current < text.Length)
            sentences.Add(text[current..]);

        return sentences;
    }
}

public sealed record ImportResult(int TotalChunks, int Stored, int Skipped);
