using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;

namespace SharpClaw.Memory;

public sealed class SemanticMemoryStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _dimension;
    private readonly ILogger<SemanticMemoryStore> _logger;
    private bool _disposed;

    public SemanticMemoryStore(IOptions<SemanticMemoryOptions> options, ILogger<SemanticMemoryStore> logger)
    {
        _logger = logger;
        _dimension = options.Value.EmbeddingDimension;

        var dbPath = Path.IsPathRooted(options.Value.DatabasePath)
            ? options.Value.DatabasePath
            : Path.Combine(AppContext.BaseDirectory, options.Value.DatabasePath);

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();

        InitializeSchema();
        _logger.LogInformation("Semantic memory store initialized at {Path}", dbPath);
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memories (
                id TEXT PRIMARY KEY,
                content TEXT NOT NULL,
                agent_name TEXT NOT NULL,
                type INTEGER NOT NULL DEFAULT 0,
                trust_score REAL NOT NULL DEFAULT 1.0,
                access_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                embedding BLOB NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_memories_agent ON memories(agent_name);
            CREATE INDEX IF NOT EXISTS idx_memories_type ON memories(type);

            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(
                content,
                content_rowid='rowid'
            );
            """;
        cmd.ExecuteNonQuery();

        // Attempt to load sqlite-vec extension and create vec table
        TryInitializeVecTable();
    }

    private void TryInitializeVecTable()
    {
        try
        {
            using var vecCmd = _connection.CreateCommand();
            vecCmd.CommandText = $"""
                CREATE VIRTUAL TABLE IF NOT EXISTS memories_vec USING vec0(
                    id TEXT PRIMARY KEY,
                    embedding float[{_dimension}]
                );
                """;
            vecCmd.ExecuteNonQuery();
            _logger.LogInformation("sqlite-vec extension available; using vector index for similarity search");
        }
        catch (SqliteException ex)
        {
            _logger.LogInformation(
                "sqlite-vec extension not available; using brute-force similarity search. Detail: {Message}",
                ex.Message);
        }
    }

    public async Task StoreAsync(SemanticMemoryEntry entry, float[] embedding, CancellationToken ct = default)
    {
        await Task.CompletedTask; // Sync SQLite ops wrapped for async interface

        using var transaction = _connection.BeginTransaction();

        try
        {
            // Insert into main table
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT OR REPLACE INTO memories (id, content, agent_name, type, trust_score, access_count, created_at, last_accessed_at, updated_at, embedding)
                VALUES (@id, @content, @agent_name, @type, @trust_score, @access_count, @created_at, @last_accessed_at, @updated_at, @embedding)
                """;
            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@content", entry.Content);
            cmd.Parameters.AddWithValue("@agent_name", entry.AgentName);
            cmd.Parameters.AddWithValue("@type", (int)entry.Type);
            cmd.Parameters.AddWithValue("@trust_score", entry.TrustScore);
            cmd.Parameters.AddWithValue("@access_count", entry.AccessCount);
            cmd.Parameters.AddWithValue("@created_at", entry.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@last_accessed_at", entry.LastAccessedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@updated_at", entry.UpdatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@embedding", EmbeddingToBlob(embedding));
            cmd.ExecuteNonQuery();

            // Insert into FTS index
            using var ftsCmd = _connection.CreateCommand();
            ftsCmd.Transaction = transaction;
            ftsCmd.CommandText = """
                INSERT OR REPLACE INTO memories_fts (rowid, content)
                SELECT rowid, content FROM memories WHERE id = @id
                """;
            ftsCmd.Parameters.AddWithValue("@id", entry.Id);
            ftsCmd.ExecuteNonQuery();

            // Insert into vec table
            try
            {
                using var vecCmd = _connection.CreateCommand();
                vecCmd.Transaction = transaction;
                vecCmd.CommandText = """
                    INSERT OR REPLACE INTO memories_vec (id, embedding)
                    VALUES (@id, @embedding)
                    """;
                vecCmd.Parameters.AddWithValue("@id", entry.Id);
                vecCmd.Parameters.AddWithValue("@embedding", EmbeddingToBlob(embedding));
                vecCmd.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // vec table may not exist if extension unavailable
            }

            transaction.Commit();
            _logger.LogDebug("Stored memory {Id} for agent {Agent}", entry.Id, entry.AgentName);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<RecalledMemory>> RecallByVectorAsync(
        float[] queryEmbedding,
        string agentName,
        int topK,
        float minScore,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // Try sqlite-vec first
        try
        {
            return RecallByVecExtension(queryEmbedding, agentName, topK, minScore);
        }
        catch (SqliteException)
        {
            // Fallback to brute-force
            return RecallByBruteForce(queryEmbedding, agentName, topK, minScore);
        }
    }

    public async Task<IReadOnlyList<RecalledMemory>> RecallByKeywordAsync(
        string query,
        string agentName,
        int topK,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;

        var results = new List<RecalledMemory>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.content, m.type, m.trust_score
            FROM memories_fts f
            JOIN memories m ON m.rowid = f.rowid
            WHERE memories_fts MATCH @query AND m.agent_name = @agent_name
            ORDER BY rank
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@agent_name", agentName);
        cmd.Parameters.AddWithValue("@limit", topK);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RecalledMemory(
                Id: reader.GetString(0),
                Content: reader.GetString(1),
                Type: (MemoryType)reader.GetInt32(2),
                Score: 0.5f, // FTS doesn't give cosine score
                TrustScore: reader.GetFloat(3)));
        }

        return results;
    }

    public async Task UpdateAccessAsync(string id, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE memories
            SET access_count = access_count + 1,
                last_accessed_at = @now,
                trust_score = MIN(trust_score * 1.05, 2.0)
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public async Task DecayAllAsync(float decayFactor = 0.95f, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE memories SET trust_score = trust_score * @decay, updated_at = @now
            WHERE trust_score > 0.1
            """;
        cmd.Parameters.AddWithValue("@decay", decayFactor);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        var affected = cmd.ExecuteNonQuery();

        // Prune memories that decayed below threshold
        using var pruneCmd = _connection.CreateCommand();
        pruneCmd.CommandText = "DELETE FROM memories WHERE trust_score <= 0.1";
        var pruned = pruneCmd.ExecuteNonQuery();

        _logger.LogInformation("Memory decay applied: {Affected} memories decayed, {Pruned} pruned", affected, pruned);
    }

    public async Task<bool> HasSimilarAsync(float[] embedding, string agentName, float threshold = 0.92f, CancellationToken ct = default)
    {
        var results = await RecallByVectorAsync(embedding, agentName, 1, threshold, ct);
        return results.Count > 0;
    }

    public async Task<int> GetCountAsync(string? agentName = null, CancellationToken ct = default)
    {
        await Task.CompletedTask;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = agentName is null
            ? "SELECT COUNT(*) FROM memories"
            : "SELECT COUNT(*) FROM memories WHERE agent_name = @agent_name";

        if (agentName is not null)
            cmd.Parameters.AddWithValue("@agent_name", agentName);

        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private IReadOnlyList<RecalledMemory> RecallByVecExtension(
        float[] queryEmbedding, string agentName, int topK, float minScore)
    {
        var results = new List<RecalledMemory>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT v.id, v.distance, m.content, m.type, m.trust_score
            FROM memories_vec v
            JOIN memories m ON m.id = v.id
            WHERE v.embedding MATCH @embedding
                AND k = @k
                AND m.agent_name = @agent_name
            ORDER BY v.distance
            """;
        cmd.Parameters.AddWithValue("@embedding", EmbeddingToBlob(queryEmbedding));
        cmd.Parameters.AddWithValue("@k", topK * 2); // Over-fetch to filter by agent
        cmd.Parameters.AddWithValue("@agent_name", agentName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read() && results.Count < topK)
        {
            var distance = reader.GetFloat(1);
            var score = 1.0f - distance; // Convert distance to similarity
            if (score < minScore) continue;

            results.Add(new RecalledMemory(
                Id: reader.GetString(0),
                Content: reader.GetString(2),
                Type: (MemoryType)reader.GetInt32(3),
                Score: score,
                TrustScore: reader.GetFloat(4)));
        }

        return results;
    }

    private IReadOnlyList<RecalledMemory> RecallByBruteForce(
        float[] queryEmbedding, string agentName, int topK, float minScore)
    {
        var results = new List<(RecalledMemory Memory, float Score)>();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, content, type, trust_score, embedding FROM memories WHERE agent_name = @agent_name";
        cmd.Parameters.AddWithValue("@agent_name", agentName);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var storedEmbedding = BlobToEmbedding((byte[])reader["embedding"]);
            var score = CosineSimilarity(queryEmbedding, storedEmbedding);
            if (score < minScore) continue;

            results.Add((new RecalledMemory(
                Id: reader.GetString(0),
                Content: reader.GetString(1),
                Type: (MemoryType)reader.GetInt32(2),
                Score: score,
                TrustScore: reader.GetFloat(3)), score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => r.Memory)
            .ToList();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }

    private static byte[] EmbeddingToBlob(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToEmbedding(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}
