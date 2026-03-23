using Microsoft.Data.Sqlite;

namespace SharpClaw.Core;

/// <summary>
/// Persists conversation history to a local SQLite database.
/// Each session is identified by a string ID and stores messages in order.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly SqliteConnection _conn;

    public SessionStore(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT NOT NULL,
                agent_file TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (session_id)
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (session_id) REFERENCES sessions(session_id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Loads an existing session, or returns null if the session doesn't exist.
    /// </summary>
    public ConversationHistory? Load(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT agent_file FROM sessions WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var agentFile = cmd.ExecuteScalar() as string;
        if (agentFile is null)
            return null;

        var history = new ConversationHistory(sessionId, agentFile);

        using var msgCmd = _conn.CreateCommand();
        msgCmd.CommandText = "SELECT role, content FROM messages WHERE session_id = @sid ORDER BY id";
        msgCmd.Parameters.AddWithValue("@sid", sessionId);

        using var reader = msgCmd.ExecuteReader();
        while (reader.Read())
        {
            var role = Enum.Parse<ChatRole>(reader.GetString(0), ignoreCase: true);
            var content = reader.GetString(1);
            history.AddRange([new ChatMessage(role, content)]);
        }

        return history;
    }

    /// <summary>
    /// Creates a new session record. Call before appending messages.
    /// </summary>
    public void CreateSession(string sessionId, string agentFile)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (session_id, agent_file) VALUES (@sid, @af)";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@af", agentFile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Appends a single message to the session in the database.
    /// </summary>
    public void Append(string sessionId, ChatMessage message)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO messages (session_id, role, content) VALUES (@sid, @role, @content)";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@role", message.Role.ToString());
        cmd.Parameters.AddWithValue("@content", message.Content);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
