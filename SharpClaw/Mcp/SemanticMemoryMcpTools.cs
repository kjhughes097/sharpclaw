using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SharpClaw.Memory;

namespace SharpClaw.Mcp;

public sealed class SemanticMemoryMcpTools(
    SemanticMemoryService? semanticMemory,
    MemoryImportService? importService)
{
    [McpServerTool]
    [Description("Search semantic memory for relevant stored facts, decisions, preferences, and learnings. Returns the most relevant memories ranked by similarity.")]
    public async Task<string> SemanticRecall(
        [Description("The query text to search for relevant memories.")] string query,
        [Description("Agent name to search memories for.")] string agentName,
        [Description("Maximum number of results to return (default: 5).")] int? topK = null,
        [Description("Minimum relevance score 0.0-1.0 (default: 0.3).")] float? minScore = null)
    {
        if (semanticMemory is null)
            return "Semantic memory is not enabled. Set SemanticMemory:Enabled to true in configuration.";

        var memories = await semanticMemory.RecallAsync(query, agentName, topK, minScore);

        if (memories.Count == 0)
            return "No relevant memories found.";

        var results = memories.Select(m => new
        {
            m.Content,
            Type = m.Type.ToString().ToLowerInvariant(),
            Relevance = Math.Round(m.Score, 3),
            Trust = Math.Round(m.TrustScore, 3)
        });

        return JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool]
    [Description("Store a fact, decision, preference, or learning in semantic memory for future recall.")]
    public async Task<string> SemanticStore(
        [Description("The content to store as a memory.")] string content,
        [Description("Agent name to store the memory for.")] string agentName,
        [Description("Memory type: fact, decision, preference, or learning.")] string type = "fact")
    {
        if (semanticMemory is null)
            return "Semantic memory is not enabled. Set SemanticMemory:Enabled to true in configuration.";

        var memoryType = type.ToLowerInvariant() switch
        {
            "decision" => MemoryType.Decision,
            "preference" => MemoryType.Preference,
            "learning" => MemoryType.Learning,
            _ => MemoryType.Fact
        };

        await semanticMemory.StoreAsync(content, agentName, memoryType);
        return $"Stored {memoryType.ToString().ToLowerInvariant()} memory for agent '{agentName}'.";
    }

    [McpServerTool]
    [Description("Get the count of stored semantic memories, optionally filtered by agent.")]
    public async Task<string> SemanticMemoryCount(
        [Description("Agent name to count memories for. Omit for total count across all agents.")] string? agentName = null)
    {
        if (semanticMemory is null)
            return "Semantic memory is not enabled.";

        var count = await semanticMemory.GetMemoryCountAsync(agentName);
        return agentName is not null
            ? $"Agent '{agentName}' has {count} semantic memories."
            : $"Total semantic memories across all agents: {count}.";
    }

    [McpServerTool]
    [Description("Import existing file-based memory (.md files) into semantic memory for an agent. Splits content into chunks and stores with deduplication.")]
    public async Task<string> SemanticMemoryImport(
        [Description("Agent name whose .md files to import. Use 'all' to import all agents.")] string agentName)
    {
        if (semanticMemory is null)
            return "Semantic memory is not enabled.";

        if (importService is null)
            return "Memory import service is not available.";

        if (agentName.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var results = await importService.ImportAllAsync();
            var summary = results.Select(kv =>
                $"  {kv.Key}: {kv.Value.Stored}/{kv.Value.TotalChunks} stored ({kv.Value.Skipped} skipped/deduped)");
            return $"Import complete:\n{string.Join("\n", summary)}";
        }

        var result = await importService.ImportAgentMemoryAsync(agentName);
        return $"Import complete for '{agentName}': {result.Stored}/{result.TotalChunks} chunks stored ({result.Skipped} skipped/deduped).";
    }
}
