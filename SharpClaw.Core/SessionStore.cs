using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

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
                description TEXT NOT NULL DEFAULT '',
                backend TEXT NOT NULL DEFAULT 'anthropic',
                model TEXT NOT NULL DEFAULT '',
                mcp_servers TEXT NOT NULL DEFAULT '[]',
                permission_policy TEXT NOT NULL DEFAULT '{}',
                system_prompt TEXT NOT NULL,
                is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS mcps (
                id SERIAL PRIMARY KEY,
                slug TEXT NOT NULL UNIQUE,
                name TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                command TEXT NOT NULL,
                args JSONB NOT NULL DEFAULT '[]'::jsonb,
                is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
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

            ALTER TABLE agents ADD COLUMN IF NOT EXISTS description TEXT NOT NULL DEFAULT '';
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS model TEXT NOT NULL DEFAULT '';
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT TRUE;
            ALTER TABLE mcps ADD COLUMN IF NOT EXISTS description TEXT NOT NULL DEFAULT '';
            ALTER TABLE mcps ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT TRUE;
            """;
        cmd.ExecuteNonQuery();

        SeedMcps(conn);
        SeedAgents(conn);
        EnsureCodyAgent(conn);
    }

    /// <summary>
    /// Seeds the built-in MCP server definitions without overwriting user edits.
    /// </summary>
    private static void SeedMcps(NpgsqlConnection conn)
    {
        foreach (var seed in BuiltInMcps)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO mcps (slug, name, description, command, args, is_enabled)
                VALUES (@slug, @name, @description, @command, @args, @is_enabled)
                ON CONFLICT (slug) DO UPDATE
                SET description = CASE
                        WHEN mcps.description = '' THEN EXCLUDED.description
                        ELSE mcps.description
                    END,
                    command = CASE
                        WHEN mcps.command = '' THEN EXCLUDED.command
                        ELSE mcps.command
                    END,
                    args = CASE
                        WHEN mcps.args = '[]'::jsonb THEN EXCLUDED.args
                        ELSE mcps.args
                    END
                """;
            WriteMcpParameters(cmd, seed);
            cmd.ExecuteNonQuery();
        }
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
                INSERT INTO agents (filename, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled)
                VALUES (@filename, @name, @description, @backend, @model, @mcp_servers, @permission_policy, @system_prompt, @is_enabled)
                ON CONFLICT (filename) DO UPDATE
                SET description = CASE
                        WHEN agents.description = '' THEN EXCLUDED.description
                        ELSE agents.description
                    END,
                    model = CASE
                        WHEN agents.model = '' THEN EXCLUDED.model
                        ELSE agents.model
                    END
                """;
            cmd.Parameters.AddWithValue("filename", seed.Filename);
            cmd.Parameters.AddWithValue("name", seed.Name);
            cmd.Parameters.AddWithValue("description", seed.Description);
            cmd.Parameters.AddWithValue("backend", seed.Backend);
            cmd.Parameters.AddWithValue("model", seed.Model);
            cmd.Parameters.AddWithValue("mcp_servers", JsonSerializer.Serialize(seed.McpServers, JsonOpts));
            cmd.Parameters.AddWithValue("permission_policy", JsonSerializer.Serialize(seed.PermissionPolicy, JsonOpts));
            cmd.Parameters.AddWithValue("system_prompt", seed.SystemPrompt);
            cmd.Parameters.AddWithValue("is_enabled", seed.IsEnabled);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Ensures there is at least one Cody agent and that any Cody agent has access to GitHub.
    /// </summary>
    private static void EnsureCodyAgent(NpgsqlConnection conn)
    {
        using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = """
            SELECT filename, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
            FROM agents
            WHERE LOWER(name) = LOWER(@name) OR filename = @filename
            ORDER BY created_at
            """;
        selectCmd.Parameters.AddWithValue("name", CodyAgentSeed.Name);
        selectCmd.Parameters.AddWithValue("filename", CodyAgentSeed.Filename);

        var matches = new List<AgentRecord>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
                matches.Add(ReadAgentRecord(reader));
        }

        if (matches.Count == 0)
        {
            using var insertCmd = conn.CreateCommand();
            insertCmd.CommandText = """
                INSERT INTO agents (filename, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled)
                VALUES (@filename, @name, @description, @backend, @model, @mcp_servers, @permission_policy, @system_prompt, @is_enabled)
                ON CONFLICT (filename) DO NOTHING
                """;
            WriteAgentParameters(insertCmd, CodyAgentSeed);
            insertCmd.ExecuteNonQuery();
            return;
        }

        foreach (var agent in matches)
        {
            if (agent.McpServers.Contains("github", StringComparer.OrdinalIgnoreCase))
                continue;

            var updatedMcpServers = agent.McpServers.ToList();
            updatedMcpServers.Add("github");

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE agents SET mcp_servers = @mcp_servers WHERE filename = @filename";
            updateCmd.Parameters.AddWithValue("filename", agent.Filename);
            updateCmd.Parameters.AddWithValue("mcp_servers", JsonSerializer.Serialize(updatedMcpServers, JsonOpts));
            updateCmd.ExecuteNonQuery();
        }
    }

    // ── Built-in agent seed data ──────────────────────────────────────────────

    private static readonly IReadOnlyList<McpServerRecord> BuiltInMcps =
    [
        new McpServerRecord(
            Slug: "filesystem",
            Name: "Filesystem",
            Description: "Read and write files from allowed workspace directories.",
            Command: "npx",
            Args: ["-y", "@modelcontextprotocol/server-filesystem", "${SHARPCLAW_ALLOWED_DIRS}"],
            IsEnabled: true),

        new McpServerRecord(
            Slug: "sqlite",
            Name: "SQLite",
            Description: "Inspect and query SQLite databases.",
            Command: "npx",
            Args: ["-y", "@anthropic/mcp-server-sqlite"],
            IsEnabled: true),

        new McpServerRecord(
            Slug: "github",
            Name: "GitHub",
            Description: "Interact with GitHub repositories, issues, and pull requests.",
            Command: "npx",
            Args: ["-y", "@modelcontextprotocol/server-github"],
            IsEnabled: true),
    ];

    private static readonly AgentRecord CodyAgentSeed = new(
        Filename: "cody.agent.md",
        Name: "Cody",
        Description: "Builds software with filesystem and GitHub context.",
        Backend: "copilot",
        Model: "gpt-5.4",
        McpServers: ["filesystem", "github"],
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
            You are Cody, a software engineering agent with access to the local workspace
            and GitHub tooling. Use the available MCP tools to inspect files, understand
            repository state, and help deliver code changes safely.
            """,
        IsEnabled: true);

    private static readonly IReadOnlyList<AgentRecord> BuiltInAgents =
    [
        new AgentRecord(
            Filename: "coordinator.agent.md",
            Name: "Coordinator",
            Description: "Routes a user request to the best specialist agent.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
            McpServers: [],
            PermissionPolicy: new Dictionary<string, string>(),
            SystemPrompt: """
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
                """,
            IsEnabled: true),

        new AgentRecord(
            Filename: "developer.agent.md",
            Name: "Developer",
            Description: "Handles software engineering, code review, and delivery tasks.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
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
                """,
            IsEnabled: true),

        new AgentRecord(
            Filename: "file-browser.agent.md",
            Name: "FileBrowser",
            Description: "Searches, lists, and reads files from the local workspace.",
            Backend: "copilot",
            Model: "gpt-5.4",
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
                """,
            IsEnabled: true),

        new AgentRecord(
            Filename: "homelab.agent.md",
            Name: "Homelab",
            Description: "Supports homelab infrastructure and container operations.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
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
                """,
            IsEnabled: true),

        new AgentRecord(
            Filename: "home-assistant.agent.md",
            Name: "HomeAssistant",
            Description: "Manages Home Assistant and automation configuration files.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
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
                """,
            IsEnabled: true),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all agent definitions stored in the database.
    /// </summary>
    public IReadOnlyList<AgentRecord> ListAgents(bool includeDisabled = true)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT filename, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
            FROM agents
            WHERE @include_disabled OR is_enabled = TRUE
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("include_disabled", includeDisabled);

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
        cmd.CommandText = """
            SELECT filename, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
            FROM agents
            WHERE filename = @filename
            """;
        cmd.Parameters.AddWithValue("filename", filename);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAgentRecord(reader) : null;
    }

    /// <summary>
    /// Returns all MCP definitions stored in the database.
    /// </summary>
    public IReadOnlyList<McpServerRecord> ListMcps(bool includeDisabled = true)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT slug, name, description, command, args::text, is_enabled
            FROM mcps
            WHERE @include_disabled OR is_enabled = TRUE
            ORDER BY name
            """;
        cmd.Parameters.AddWithValue("include_disabled", includeDisabled);

        var mcps = new List<McpServerRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            mcps.Add(ReadMcpRecord(reader));

        return mcps;
    }

    /// <summary>
    /// Resolves a list of MCP slugs to stored definitions, preserving the input order.
    /// </summary>
    public IReadOnlyList<McpServerRecord> ListMcpsBySlug(IEnumerable<string> slugs, bool includeDisabled = true)
    {
        var requested = slugs
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Select(slug => slug.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (requested.Count == 0)
            return [];

        var lookup = ListMcps(includeDisabled)
            .ToDictionary(mcp => mcp.Slug, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<McpServerRecord>();
        foreach (var slug in requested)
        {
            if (lookup.TryGetValue(slug, out var record))
                resolved.Add(record);
        }

        return resolved;
    }

    /// <summary>
    /// Returns a single MCP definition by slug, or null if not found.
    /// </summary>
    public McpServerRecord? GetMcp(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT slug, name, description, command, args::text, is_enabled
            FROM mcps
            WHERE slug = @slug
            """;
        cmd.Parameters.AddWithValue("slug", slug);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadMcpRecord(reader) : null;
    }

    public void CreateMcp(McpServerRecord mcp)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mcps (slug, name, description, command, args, is_enabled)
            VALUES (@slug, @name, @description, @command, @args, @is_enabled)
            """;
        WriteMcpParameters(cmd, mcp);
        cmd.ExecuteNonQuery();
    }

    public bool UpdateMcp(string slug, McpServerRecord mcp)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mcps
            SET name = @name,
                description = @description,
                command = @command,
                args = @args,
                is_enabled = @is_enabled
            WHERE slug = @slug
            """;
        WriteMcpParameters(cmd, mcp with { Slug = slug });
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetMcpEnabled(string slug, bool isEnabled)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE mcps SET is_enabled = @is_enabled WHERE slug = @slug";
        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("is_enabled", isEnabled);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void CreateAgent(AgentRecord agent)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agents (filename, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled)
            VALUES (@filename, @name, @description, @backend, @model, @mcp_servers, @permission_policy, @system_prompt, @is_enabled)
            """;
        WriteAgentParameters(cmd, agent);
        cmd.ExecuteNonQuery();
    }

    public bool UpdateAgent(string filename, AgentRecord agent)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agents
            SET name = @name,
                description = @description,
                backend = @backend,
                model = @model,
                mcp_servers = @mcp_servers,
                permission_policy = @permission_policy,
                system_prompt = @system_prompt,
                is_enabled = @is_enabled
            WHERE filename = @filename
            """;
        WriteAgentParameters(cmd, agent with { Filename = filename });
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetAgentEnabled(string filename, bool isEnabled)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agents SET is_enabled = @is_enabled WHERE filename = @filename";
        cmd.Parameters.AddWithValue("filename", filename);
        cmd.Parameters.AddWithValue("is_enabled", isEnabled);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyDictionary<string, int> GetSessionCountsByAgent()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_file, COUNT(*)::INT FROM sessions GROUP BY agent_file";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    public int CountSessionsForAgent(string filename)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*)::INT FROM sessions WHERE agent_file = @filename";
        cmd.Parameters.AddWithValue("filename", filename);
        return (int)(cmd.ExecuteScalar() ?? 0);
    }

    public IReadOnlyDictionary<string, int> GetAgentCountsByMcp()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in ListAgents())
        {
            foreach (var slug in agent.McpServers.Distinct(StringComparer.OrdinalIgnoreCase))
                counts[slug] = counts.GetValueOrDefault(slug, 0) + 1;
        }

        return counts;
    }

    public int CountAgentsForMcp(string slug)
    {
        return GetAgentCountsByMcp().GetValueOrDefault(slug, 0);
    }

    public int DetachMcpFromAgents(string slug)
    {
        var detachedAgents = 0;
        foreach (var agent in ListAgents())
        {
            if (!agent.McpServers.Contains(slug, StringComparer.OrdinalIgnoreCase))
                continue;

            var updatedServers = agent.McpServers
                .Where(server => !string.Equals(server, slug, StringComparison.OrdinalIgnoreCase))
                .ToList();

            UpdateAgent(agent.Filename, agent with { McpServers = updatedServers });
            detachedAgents++;
        }

        return detachedAgents;
    }

    public IReadOnlyList<string> ListSessionIdsForAgent(string filename)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id FROM sessions WHERE agent_file = @filename ORDER BY created_at";
        cmd.Parameters.AddWithValue("filename", filename);

        var sessionIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sessionIds.Add(reader.GetString(0));

        return sessionIds;
    }

    public int PurgeSessionsForAgent(string filename)
    {
        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();

        using var countCmd = conn.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*)::INT FROM sessions WHERE agent_file = @filename";
        countCmd.Parameters.AddWithValue("filename", filename);
        var count = (int)(countCmd.ExecuteScalar() ?? 0);

        using var msgCmd = conn.CreateCommand();
        msgCmd.Transaction = tx;
        msgCmd.CommandText = "DELETE FROM messages WHERE session_id IN (SELECT session_id FROM sessions WHERE agent_file = @filename)";
        msgCmd.Parameters.AddWithValue("filename", filename);
        msgCmd.ExecuteNonQuery();

        using var sessionCmd = conn.CreateCommand();
        sessionCmd.Transaction = tx;
        sessionCmd.CommandText = "DELETE FROM sessions WHERE agent_file = @filename";
        sessionCmd.Parameters.AddWithValue("filename", filename);
        sessionCmd.ExecuteNonQuery();

        tx.Commit();
        return count;
    }

    public bool DeleteAgent(string filename)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agents WHERE filename = @filename";
        cmd.Parameters.AddWithValue("filename", filename);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteMcp(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mcps WHERE slug = @slug";
        cmd.Parameters.AddWithValue("slug", slug);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static AgentRecord ReadAgentRecord(NpgsqlDataReader reader)
    {
        var mcpServers = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), JsonOpts) ?? [];
        var permPolicy = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(6), JsonOpts)
            ?? new Dictionary<string, string>();
        return new AgentRecord(
            Filename: reader.GetString(0),
            Name: reader.GetString(1),
            Description: reader.GetString(2),
            Backend: reader.GetString(3),
            Model: reader.GetString(4),
            McpServers: mcpServers,
            PermissionPolicy: permPolicy,
            SystemPrompt: reader.GetString(7),
            IsEnabled: reader.GetBoolean(8));
    }

    private static McpServerRecord ReadMcpRecord(NpgsqlDataReader reader)
    {
        var args = JsonSerializer.Deserialize<List<string>>(reader.GetString(4), JsonOpts) ?? [];
        return new McpServerRecord(
            Slug: reader.GetString(0),
            Name: reader.GetString(1),
            Description: reader.GetString(2),
            Command: reader.GetString(3),
            Args: args,
            IsEnabled: reader.GetBoolean(5));
    }

    private static void WriteAgentParameters(NpgsqlCommand cmd, AgentRecord agent)
    {
        cmd.Parameters.AddWithValue("filename", agent.Filename);
        cmd.Parameters.AddWithValue("name", agent.Name);
        cmd.Parameters.AddWithValue("description", agent.Description);
        cmd.Parameters.AddWithValue("backend", agent.Backend);
        cmd.Parameters.AddWithValue("model", agent.Model);
        cmd.Parameters.AddWithValue("mcp_servers", JsonSerializer.Serialize(agent.McpServers, JsonOpts));
        cmd.Parameters.AddWithValue("permission_policy", JsonSerializer.Serialize(agent.PermissionPolicy, JsonOpts));
        cmd.Parameters.AddWithValue("system_prompt", agent.SystemPrompt);
        cmd.Parameters.AddWithValue("is_enabled", agent.IsEnabled);
    }

    private static void WriteMcpParameters(NpgsqlCommand cmd, McpServerRecord mcp)
    {
        cmd.Parameters.AddWithValue("slug", mcp.Slug);
        cmd.Parameters.AddWithValue("name", mcp.Name);
        cmd.Parameters.AddWithValue("description", mcp.Description);
        cmd.Parameters.AddWithValue("command", mcp.Command);
        cmd.Parameters.Add("args", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(mcp.Args, JsonOpts);
        cmd.Parameters.AddWithValue("is_enabled", mcp.IsEnabled);
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

