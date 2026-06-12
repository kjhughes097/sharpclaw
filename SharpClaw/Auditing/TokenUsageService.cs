using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SharpClaw.Configuration;
using SharpClaw.Models;

namespace SharpClaw.Auditing;

public sealed class TokenUsageService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger<TokenUsageService> _logger;
    private readonly Counter<long> _inputTokenCounter;
    private readonly Counter<long> _outputTokenCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly Lock _writeLock = new();
    private bool _disposed;

    public TokenUsageService(
        IOptions<SharpClawOptions> options,
        IMeterFactory meterFactory,
        ILogger<TokenUsageService> logger)
    {
        _logger = logger;

        var workspacePath = options.Value.WorkspacePath;
        var dbPath = !string.IsNullOrEmpty(workspacePath)
            ? Path.Combine(workspacePath, "token-usage.db")
            : Path.Combine(AppContext.BaseDirectory, "token-usage.db");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();

        var meter = meterFactory.Create("SharpClaw.Tokens");
        _inputTokenCounter = meter.CreateCounter<long>("sharpclaw.tokens.input", "tokens", "Input tokens consumed");
        _outputTokenCounter = meter.CreateCounter<long>("sharpclaw.tokens.output", "tokens", "Output tokens consumed");
        _durationHistogram = meter.CreateHistogram<double>("sharpclaw.llm.duration_ms", "ms", "LLM request duration");

        _logger.LogInformation("Token usage service initialized at {Path}", dbPath);
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS token_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                agent_name TEXT NOT NULL,
                provider TEXT NOT NULL,
                model TEXT,
                session_id TEXT,
                input_tokens INTEGER,
                output_tokens INTEGER,
                duration_ms REAL,
                tool_count INTEGER NOT NULL DEFAULT 0,
                mcp_count INTEGER NOT NULL DEFAULT 0,
                skills TEXT,
                success INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_token_usage_agent ON token_usage(agent_name);
            CREATE INDEX IF NOT EXISTS idx_token_usage_timestamp ON token_usage(timestamp_utc);
            CREATE INDEX IF NOT EXISTS idx_token_usage_provider ON token_usage(provider);
            CREATE INDEX IF NOT EXISTS idx_token_usage_model ON token_usage(model);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Record(TokenUsage usage)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO token_usage (timestamp_utc, agent_name, provider, model, session_id, input_tokens, output_tokens, duration_ms, tool_count, mcp_count, skills, success)
                VALUES (@timestamp, @agent, @provider, @model, @session, @input, @output, @duration, @tools, @mcps, @skills, @success)
                """;
            cmd.Parameters.AddWithValue("@timestamp", usage.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@agent", usage.AgentName);
            cmd.Parameters.AddWithValue("@provider", usage.Provider);
            cmd.Parameters.AddWithValue("@model", (object?)usage.Model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@session", (object?)usage.SessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@input", (object?)usage.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@output", (object?)usage.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@duration", (object?)usage.DurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tools", usage.ToolCount);
            cmd.Parameters.AddWithValue("@mcps", usage.McpCount);
            cmd.Parameters.AddWithValue("@skills", (object?)usage.Skills ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@success", usage.Success ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        // Emit OTel metrics
        var tags = new TagList
        {
            { "agent", usage.AgentName },
            { "provider", usage.Provider },
            { "model", usage.Model ?? "unknown" }
        };

        if (usage.InputTokens.HasValue)
            _inputTokenCounter.Add(usage.InputTokens.Value, tags);

        if (usage.OutputTokens.HasValue)
            _outputTokenCounter.Add(usage.OutputTokens.Value, tags);

        if (usage.DurationMs.HasValue)
            _durationHistogram.Record(usage.DurationMs.Value, tags);

        _logger.LogInformation(
            "Token usage: Agent={Agent} Provider={Provider} Model={Model} Input={InputTokens} Output={OutputTokens} Duration={DurationMs:F0}ms",
            usage.AgentName, usage.Provider, usage.Model, usage.InputTokens, usage.OutputTokens, usage.DurationMs);
    }

    public IReadOnlyList<TokenUsageSummary> GetSummary(DateTimeOffset? from = null, DateTimeOffset? to = null, string? agent = null, string? provider = null)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            var where = BuildWhereClause(cmd, from, to, agent, provider);
            cmd.CommandText = $"""
                SELECT agent_name, provider, model,
                       COUNT(*) as request_count,
                       COALESCE(SUM(input_tokens), 0) as total_input,
                       COALESCE(SUM(output_tokens), 0) as total_output,
                       COALESCE(AVG(duration_ms), 0) as avg_duration
                FROM token_usage
                {where}
                GROUP BY agent_name, provider, model
                ORDER BY total_input + total_output DESC
                """;

            using var reader = cmd.ExecuteReader();
            var results = new List<TokenUsageSummary>();
            while (reader.Read())
            {
                results.Add(new TokenUsageSummary(
                    AgentName: reader.GetString(0),
                    Provider: reader.GetString(1),
                    Model: reader.IsDBNull(2) ? null : reader.GetString(2),
                    RequestCount: reader.GetInt32(3),
                    TotalInputTokens: reader.GetInt64(4),
                    TotalOutputTokens: reader.GetInt64(5),
                    AvgDurationMs: reader.GetDouble(6)));
            }
            return results;
        }
    }

    public IReadOnlyList<TokenUsageDaily> GetDaily(DateTimeOffset? from = null, DateTimeOffset? to = null, string? agent = null, string? provider = null)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            var where = BuildWhereClause(cmd, from, to, agent, provider);
            cmd.CommandText = $"""
                SELECT DATE(timestamp_utc) as day,
                       COUNT(*) as request_count,
                       COALESCE(SUM(input_tokens), 0) as total_input,
                       COALESCE(SUM(output_tokens), 0) as total_output
                FROM token_usage
                {where}
                GROUP BY DATE(timestamp_utc)
                ORDER BY day DESC
                LIMIT 90
                """;

            using var reader = cmd.ExecuteReader();
            var results = new List<TokenUsageDaily>();
            while (reader.Read())
            {
                results.Add(new TokenUsageDaily(
                    Date: reader.GetString(0),
                    RequestCount: reader.GetInt32(1),
                    TotalInputTokens: reader.GetInt64(2),
                    TotalOutputTokens: reader.GetInt64(3)));
            }
            return results;
        }
    }

    public IReadOnlyList<TokenUsage> GetRecent(int limit = 50, string? agent = null, string? provider = null)
    {
        lock (_writeLock)
        {
            using var cmd = _connection.CreateCommand();
            var conditions = new List<string>();
            if (!string.IsNullOrEmpty(agent))
            {
                conditions.Add("agent_name = @agent");
                cmd.Parameters.AddWithValue("@agent", agent);
            }
            if (!string.IsNullOrEmpty(provider))
            {
                conditions.Add("provider = @provider");
                cmd.Parameters.AddWithValue("@provider", provider);
            }
            var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

            cmd.CommandText = $"""
                SELECT id, timestamp_utc, agent_name, provider, model, session_id, input_tokens, output_tokens, duration_ms, tool_count, mcp_count, skills, success
                FROM token_usage
                {where}
                ORDER BY id DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@limit", limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<TokenUsage>();
            while (reader.Read())
            {
                results.Add(new TokenUsage
                {
                    Id = reader.GetInt64(0),
                    TimestampUtc = DateTimeOffset.Parse(reader.GetString(1)),
                    AgentName = reader.GetString(2),
                    Provider = reader.GetString(3),
                    Model = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
                    InputTokens = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    OutputTokens = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    DurationMs = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    ToolCount = reader.GetInt32(9),
                    McpCount = reader.GetInt32(10),
                    Skills = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Success = reader.GetInt32(12) == 1
                });
            }
            return results;
        }
    }

    private static string BuildWhereClause(SqliteCommand cmd, DateTimeOffset? from, DateTimeOffset? to, string? agent, string? provider)
    {
        var conditions = new List<string>();
        if (from.HasValue)
        {
            conditions.Add("timestamp_utc >= @from");
            cmd.Parameters.AddWithValue("@from", from.Value.ToString("O"));
        }
        if (to.HasValue)
        {
            conditions.Add("timestamp_utc <= @to");
            cmd.Parameters.AddWithValue("@to", to.Value.ToString("O"));
        }
        if (!string.IsNullOrEmpty(agent))
        {
            conditions.Add("agent_name = @agent");
            cmd.Parameters.AddWithValue("@agent", agent);
        }
        if (!string.IsNullOrEmpty(provider))
        {
            conditions.Add("provider = @provider");
            cmd.Parameters.AddWithValue("@provider", provider);
        }
        return conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}

public sealed record TokenUsageSummary(
    string AgentName,
    string Provider,
    string? Model,
    int RequestCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    double AvgDurationMs);

public sealed record TokenUsageDaily(
    string Date,
    int RequestCount,
    long TotalInputTokens,
    long TotalOutputTokens);
