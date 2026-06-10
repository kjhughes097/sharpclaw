using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

public sealed class SemanticMemoryService(
    SemanticMemoryStore store,
    EmbeddingService embeddingService,
    IOptions<SemanticMemoryOptions> options,
    ILogger<SemanticMemoryService> logger)
{
    private readonly SemanticMemoryOptions _options = options.Value;

    public async Task StoreAsync(
        string content,
        string agentName,
        MemoryType type = MemoryType.Fact,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var embedding = embeddingService.GenerateEmbedding(content);

        // Dedup: skip if very similar memory already exists
        if (await store.HasSimilarAsync(embedding, agentName, 0.92f, ct))
        {
            logger.LogDebug("Skipping duplicate memory for agent {Agent}: {Content}", agentName, content[..Math.Min(50, content.Length)]);
            return;
        }

        var entry = new SemanticMemoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Content = content,
            AgentName = agentName,
            Type = type,
            TrustScore = 1.0f,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.StoreAsync(entry, embedding, ct);
        logger.LogDebug("Stored semantic memory {Id} ({Type}) for agent {Agent}", entry.Id, type, agentName);
    }

    public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(
        string query,
        string agentName,
        int? topK = null,
        float? minScore = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var k = topK ?? _options.TopK;
        var score = minScore ?? _options.MinScore;

        var embedding = embeddingService.GenerateEmbedding(query);
        var vectorResults = await store.RecallByVectorAsync(embedding, agentName, k, score, ct);

        // Update access counts for recalled memories
        foreach (var memory in vectorResults)
        {
            await store.UpdateAccessAsync(memory.Id, ct);
        }

        if (vectorResults.Count > 0)
        {
            logger.LogDebug(
                "Recalled {Count} memories for agent {Agent} (top score: {Score:F3})",
                vectorResults.Count, agentName, vectorResults[0].Score);
        }

        return vectorResults;
    }

    public string FormatRecalledContext(IReadOnlyList<RecalledMemory> memories)
    {
        if (memories.Count == 0) return string.Empty;

        var lines = memories.Select(m =>
            $"- [{m.Type}] {m.Content} (relevance: {m.Score:F2})");

        return $"[Relevant context from memory]\n{string.Join("\n", lines)}\n[End memory context]";
    }

    public async Task DecayAsync(CancellationToken ct = default)
    {
        await store.DecayAllAsync(0.95f, ct);
    }

    public async Task<int> GetMemoryCountAsync(string? agentName = null, CancellationToken ct = default)
    {
        return await store.GetCountAsync(agentName, ct);
    }
}
