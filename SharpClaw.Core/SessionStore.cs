using System.Text.Json;
using Npgsql;

namespace SharpClaw.Core;

/// <summary>
/// Persists conversation history and agent definitions in a PostgreSQL database.
/// On first use, the built-in agent definitions are seeded automatically.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new();

    private readonly NpgsqlDataSource _dataSource;

    public SessionStore(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        InitSchema();
    }

    private void InitSchema()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS agents (
                id SERIAL PRIMARY KEY,
                filename TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                backend TEXT NOT NULL DEFAULT 'anthropic',
                mcp_servers TEXT NOT NULL DEFAULT '[]',
                permission_policy TEXT NOT NULL DEFAULT '{}',
                system_prompt TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT NOT NULL,
                agent_file TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (session_id)
            );

            CREATE TABLE IF NOT EXISTS messages (
                id SERIAL PRIMARY KEY,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                FOREIGN KEY (session_id) REFERENCES sessions(session_id)
            );
            """;
        cmd.ExecuteNonQuery();

        SeedAgents(conn);
    }

    /// <summary>
    /// Seeds the built-in agent definitions. Uses INSERT … ON CONFLICT DO NOTHING
    /// so it is safe to run on every startup without overwriting user edits.
    /// </summary>
    private static void SeedAgents(NpgsqlConnection conn)
    {
        foreach (var seed in BuiltInAgents)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agents (filename, name, backend, mcp_servers, permission_policy, system_prompt)
                VALUES (@filename, @name, @backend, @mcp_servers, @permission_policy, @system_prompt)
                ON CONFLICT (filename) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("filename", seed.Filename);
            cmd.Parameters.AddWithValue("name", seed.Name);
            cmd.Parameters.AddWithValue("backend", seed.Backend);
            cmd.Parameters.AddWithValue("mcp_servers", JsonSerializer.Serialize(seed.McpServers, JsonOpts));
            cmd.Parameters.AddWithValue("permission_policy", JsonSerializer.Serialize(seed.PermissionPolicy, JsonOpts));
            cmd.Parameters.AddWithValue("system_prompt", seed.SystemPrompt);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Built-in agent seed data ──────────────────────────────────────────────

    private static readonly IReadOnlyList<AgentRecord> BuiltInAgents =
    [
        new AgentRecord(
            Filename: "coordinator.agent.md",
            Name: "Coordinator",
            Backend: "anthropic",
            McpServers: [],
            PermissionPolicy: new Dictionary<string, string>(),
            SystemPrompt: """"
                You are a routing coordinator. Given a user message and a list of available specialist agents, determine which agent is the best fit for the request.

                Reply with **only** a JSON object — no markdown fences, no extra text:
                ```
                { "agent": "<filename>.agent.md", "rewritten_prompt": "<clarified version of the user request>" }
                ```

                Rules:
                - Pick the single most relevant specialist agent.
                - Rewrite the prompt to be clear and actionable for the chosen specialist.
                - If no specialist is a good fit, return `{ "agent": null, "rewritten_prompt": null }`.
                - Do NOT explain your choice. Output the JSON object only.
                """"),

        new AgentRecord(
            Filename: "developer.agent.md",
            Name: "Developer",
            Backend: "anthropic",
            McpServers: ["filesystem"],
            PermissionPolicy: new Dictionary<string, string>
            {
                ["read_file"] = "auto_approve",
                ["list_directory"] = "auto_approve",
                ["list_allowed_directories"] = "auto_approve",
                ["search_files"] = "auto_approve",
                ["get_file_info"] = "auto_approve",
                ["write_file"] = "ask",
                ["create_directory"] = "ask",
                ["*"] = "ask",
            },
            SystemPrompt: """
                You are a software development assistant. You help with code reviews, writing
                and explaining code, Infrastructure-as-Code (Bicep, Terraform), CI/CD pipelines,
                and general software engineering tasks.

                When reviewing files, read them from the filesystem and provide specific,
                actionable feedback. When generating code, follow best practices for the
                relevant language or framework.
                """),

        new AgentRecord(
            Filename: "file-browser.agent.md",
            Name: "FileBrowser",
            Backend: "copilot",
            McpServers: ["filesystem"],
            PermissionPolicy: new Dictionary<string, string>
            {
                ["read_file"] = "auto_approve",
                ["list_directory"] = "auto_approve",
                ["list_allowed_directories"] = "auto_approve",
                ["search_files"] = "auto_approve",
                ["get_file_info"] = "auto_approve",
                ["*"] = "ask",
            },
            SystemPrompt: """
                You are a helpful file browser assistant. You can list, read, and search files
                on the local filesystem. Answer questions about file contents concisely.
                """),

        new AgentRecord(
            Filename: "homelab.agent.md",
            Name: "Homelab",
            Backend: "anthropic",
            McpServers: ["filesystem"],
            PermissionPolicy: new Dictionary<string, string>
            {
                ["read_file"] = "auto_approve",
                ["list_directory"] = "auto_approve",
                ["list_allowed_directories"] = "auto_approve",
                ["search_files"] = "auto_approve",
                ["get_file_info"] = "auto_approve",
                ["write_file"] = "ask",
                ["create_directory"] = "ask",
                ["*"] = "ask",
            },
            SystemPrompt: """
                You are a home lab infrastructure agent. You help manage Docker containers,
                docker-compose files, networking configs, and home automation stacks
                (e.g. Home Assistant, Zigbee2MQTT, Mosquitto, Grafana).

                When asked to perform actions on containers or services, look for the relevant
                docker-compose.yml or configuration files on the filesystem and provide the
                appropriate shell commands or file edits. Always confirm destructive actions
                before proceeding.
                """),

        new AgentRecord(
            Filename: "home-assistant.agent.md",
            Name: "HomeAssistant",
            Backend: "anthropic",
            McpServers: ["filesystem"],
            PermissionPolicy: new Dictionary<string, string>
            {
                ["read_file"] = "auto_approve",
                ["list_directory"] = "auto_approve",
                ["list_allowed_directories"] = "auto_approve",
                ["write_file"] = "ask",
                ["create_directory"] = "ask",
                ["*"] = "ask",
            },
            SystemPrompt: """
                You are a home lab automation agent with access to the local filesystem.
                When asked about files, use the available tools to retrieve real information.
                Help the user manage their home automation configuration files.
                """),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all agent definitions stored in the database.
    /// </summary>
    public IReadOnlyList<AgentRecord> ListAgents()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT filename, name, backend, mcp_servers, permission_policy, system_prompt FROM agents ORDER BY name";

        var agents = new List<AgentRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            agents.Add(ReadAgentRecord(reader));

        return agents;
    }

    /// <summary>
    /// Returns a single agent by its filename (e.g. "developer.agent.md"), or null if not found.
    /// </summary>
    public AgentRecord? GetAgent(string filename)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT filename, name, backend, mcp_servers, permission_policy, system_prompt FROM agents WHERE filename = @filename";
        cmd.Parameters.AddWithValue("filename", filename);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAgentRecord(reader) : null;
    }

    private static AgentRecord ReadAgentRecord(NpgsqlDataReader reader)
    {
        var mcpServers = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOpts) ?? [];
        var permPolicy = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4), JsonOpts)
            ?? new Dictionary<string, string>();
        return new AgentRecord(
            Filename: reader.GetString(0),
            Name: reader.GetString(1),
            Backend: reader.GetString(2),
            McpServers: mcpServers,
            PermissionPolicy: permPolicy,
            SystemPrompt: reader.GetString(5));
    }

    /// <summary>
    /// Loads an existing session, or returns null if the session doesn't exist.
    /// </summary>
    public ConversationHistory? Load(string sessionId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_file FROM sessions WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("sid", sessionId);

        var agentFile = cmd.ExecuteScalar() as string;
        if (agentFile is null)
            return null;

        var history = new ConversationHistory(sessionId, agentFile);

        using var msgCmd = conn.CreateCommand();
        msgCmd.CommandText = "SELECT role, content FROM messages WHERE session_id = @sid ORDER BY id";
        msgCmd.Parameters.AddWithValue("sid", sessionId);

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
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (session_id, agent_file) VALUES (@sid, @af)";
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("af", agentFile);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Appends a single message to the session in the database.
    /// </summary>
    public void Append(string sessionId, ChatMessage message)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO messages (session_id, role, content) VALUES (@sid, @role, @content)";
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("role", message.Role.ToString());
        cmd.Parameters.AddWithValue("content", message.Content);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _dataSource.Dispose();
}

