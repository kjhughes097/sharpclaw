using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace SharpClaw.Core;

public sealed record StoredSession(string SessionId, string AgentSlug, DateTimeOffset CreatedAt, DateTimeOffset LastActivityAt);
public sealed record StoredEventLogItem(AgentEvent Event, ToolResultEvent? Result);

/// <summary>
/// Persists conversation history and agent definitions in a PostgreSQL database.
/// On first use, the built-in agent definitions are seeded automatically.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new();
    private const string AdeAgentId = "ade.agent.md";
    private const string LegacyCoordinatorId = "coordinator.agent.md";
    private static readonly string[] LegacySeededAgentIds =
    [
        "cody.agent.md",
        "developer.agent.md",
        "file-browser.agent.md",
        "homelab.agent.md",
        "home-assistant.agent.md",
    ];

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
                slug TEXT NOT NULL UNIQUE,
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
                agent_slug TEXT NOT NULL,
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

            CREATE TABLE IF NOT EXISTS session_event_logs (
                id SERIAL PRIMARY KEY,
                session_id TEXT NOT NULL,
                assistant_index INT NOT NULL,
                items JSONB NOT NULL DEFAULT '[]'::jsonb,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (session_id, assistant_index),
                FOREIGN KEY (session_id) REFERENCES sessions(session_id)
            );

            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'agents'
                      AND column_name = 'filename'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'agents'
                      AND column_name = 'slug'
                ) THEN
                    ALTER TABLE agents RENAME COLUMN filename TO slug;
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'sessions'
                      AND column_name = 'agent_file'
                ) AND NOT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_schema = current_schema()
                      AND table_name = 'sessions'
                      AND column_name = 'agent_slug'
                ) THEN
                    ALTER TABLE sessions RENAME COLUMN agent_file TO agent_slug;
                END IF;
            END $$;

            ALTER TABLE agents ADD COLUMN IF NOT EXISTS description TEXT NOT NULL DEFAULT '';
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS model TEXT NOT NULL DEFAULT '';
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT TRUE;
            ALTER TABLE mcps ADD COLUMN IF NOT EXISTS description TEXT NOT NULL DEFAULT '';
            ALTER TABLE mcps ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT TRUE;
            """;
        cmd.ExecuteNonQuery();

        SeedMcps(conn);
        MigrateBuiltInAgents(conn);
        SeedAgents(conn);
        MigratePermissionPolicies(conn);
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
                INSERT INTO agents (slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled)
                VALUES (@slug, @name, @description, @backend, @model, @mcp_servers, @permission_policy, @system_prompt, @is_enabled)
                ON CONFLICT (slug) DO UPDATE
                SET description = CASE
                        WHEN agents.description = '' THEN EXCLUDED.description
                        ELSE agents.description
                    END,
                    model = CASE
                        WHEN agents.model = '' THEN EXCLUDED.model
                        ELSE agents.model
                    END
                """;
            cmd.Parameters.AddWithValue("slug", seed.Slug);
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

    private static void MigrateBuiltInAgents(NpgsqlConnection conn)
    {
        var adeSeed = BuiltInAgents.Single();

        if (GetAgent(conn, LegacyCoordinatorId) is not null)
        {
            using var tx = conn.BeginTransaction();

            using var updateSessions = conn.CreateCommand();
            updateSessions.Transaction = tx;
            updateSessions.CommandText = "UPDATE sessions SET agent_slug = @new_slug WHERE agent_slug = @old_slug";
            updateSessions.Parameters.AddWithValue("new_slug", adeSeed.Slug);
            updateSessions.Parameters.AddWithValue("old_slug", LegacyCoordinatorId);
            updateSessions.ExecuteNonQuery();

            using var renameAgent = conn.CreateCommand();
            renameAgent.Transaction = tx;
            renameAgent.CommandText = """
                UPDATE agents
                SET slug = @new_slug,
                    name = @name,
                    description = @description,
                    system_prompt = @system_prompt
                WHERE slug = @old_slug
                  AND NOT EXISTS (SELECT 1 FROM agents WHERE slug = @new_slug)
                """;
            renameAgent.Parameters.AddWithValue("new_slug", adeSeed.Slug);
            renameAgent.Parameters.AddWithValue("name", adeSeed.Name);
            renameAgent.Parameters.AddWithValue("description", adeSeed.Description);
            renameAgent.Parameters.AddWithValue("system_prompt", adeSeed.SystemPrompt);
            renameAgent.Parameters.AddWithValue("old_slug", LegacyCoordinatorId);
            renameAgent.ExecuteNonQuery();

            using var deleteOldCoordinator = conn.CreateCommand();
            deleteOldCoordinator.Transaction = tx;
            deleteOldCoordinator.CommandText = "DELETE FROM agents WHERE slug = @old_slug AND EXISTS (SELECT 1 FROM agents WHERE slug = @new_slug)";
            deleteOldCoordinator.Parameters.AddWithValue("old_slug", LegacyCoordinatorId);
            deleteOldCoordinator.Parameters.AddWithValue("new_slug", adeSeed.Slug);
            deleteOldCoordinator.ExecuteNonQuery();

            tx.Commit();
        }

        using (var updateAde = conn.CreateCommand())
        {
            updateAde.CommandText = "UPDATE agents SET name = @name, description = @description, system_prompt = @system_prompt WHERE slug = @slug";
            updateAde.Parameters.AddWithValue("slug", adeSeed.Slug);
            updateAde.Parameters.AddWithValue("name", adeSeed.Name);
            updateAde.Parameters.AddWithValue("description", adeSeed.Description);
            updateAde.Parameters.AddWithValue("system_prompt", adeSeed.SystemPrompt);
            updateAde.ExecuteNonQuery();
        }

        foreach (var legacyAgentId in LegacySeededAgentIds)
            DeleteAgentAndSessions(conn, legacyAgentId);
    }

    private static AgentRecord? GetAgent(NpgsqlConnection conn, string slug)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
            FROM agents
            WHERE slug = @slug
            """;
        cmd.Parameters.AddWithValue("slug", slug);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadAgentRecord(reader) : null;
    }

    private static void DeleteAgentAndSessions(NpgsqlConnection conn, string slug)
    {
        using var tx = conn.BeginTransaction();

        using var deleteEventLogs = conn.CreateCommand();
        deleteEventLogs.Transaction = tx;
        deleteEventLogs.CommandText = "DELETE FROM session_event_logs WHERE session_id IN (SELECT session_id FROM sessions WHERE agent_slug = @slug)";
        deleteEventLogs.Parameters.AddWithValue("slug", slug);
        deleteEventLogs.ExecuteNonQuery();

        using var deleteMessages = conn.CreateCommand();
        deleteMessages.Transaction = tx;
        deleteMessages.CommandText = "DELETE FROM messages WHERE session_id IN (SELECT session_id FROM sessions WHERE agent_slug = @slug)";
        deleteMessages.Parameters.AddWithValue("slug", slug);
        deleteMessages.ExecuteNonQuery();

        using var deleteSessions = conn.CreateCommand();
        deleteSessions.Transaction = tx;
        deleteSessions.CommandText = "DELETE FROM sessions WHERE agent_slug = @slug";
        deleteSessions.Parameters.AddWithValue("slug", slug);
        deleteSessions.ExecuteNonQuery();

        using var deleteAgent = conn.CreateCommand();
        deleteAgent.Transaction = tx;
        deleteAgent.CommandText = "DELETE FROM agents WHERE slug = @slug";
        deleteAgent.Parameters.AddWithValue("slug", slug);
        deleteAgent.ExecuteNonQuery();

        tx.Commit();
    }

    /// <summary>
    /// Migrates legacy flat permission rules to namespaced MCP-aware rules when the target MCP can be inferred safely.
    /// </summary>
    private static void MigratePermissionPolicies(NpgsqlConnection conn)
    {
        using var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = """
            SELECT slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
            FROM agents
            ORDER BY created_at
            """;

        var agents = new List<AgentRecord>();
        using (var reader = selectCmd.ExecuteReader())
        {
            while (reader.Read())
                agents.Add(ReadAgentRecord(reader));
        }

        foreach (var agent in agents)
        {
            var migratedPolicy = MigratePermissionPolicy(agent.McpServers, agent.PermissionPolicy);
            if (PoliciesEqual(agent.PermissionPolicy, migratedPolicy))
                continue;

            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE agents SET permission_policy = @permission_policy WHERE slug = @slug";
            updateCmd.Parameters.AddWithValue("slug", agent.Slug);
            updateCmd.Parameters.AddWithValue("permission_policy", JsonSerializer.Serialize(migratedPolicy, JsonOpts));
            updateCmd.ExecuteNonQuery();
        }
    }

    private static IReadOnlyDictionary<string, string> MigratePermissionPolicy(
        IReadOnlyList<string> mcpServers,
        IReadOnlyDictionary<string, string> permissionPolicy)
    {
        var migrated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pattern, value) in permissionPolicy)
        {
            var normalizedPattern = NormalizePermissionPattern(pattern, mcpServers);
            if (!migrated.ContainsKey(normalizedPattern))
                migrated[normalizedPattern] = value;
        }

        return migrated;
    }

    private static string NormalizePermissionPattern(string pattern, IReadOnlyList<string> mcpServers)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*" || pattern.Contains('.', StringComparison.Ordinal))
            return pattern;

        if (mcpServers.Count == 1)
            return $"{mcpServers[0]}.{pattern}";

        if (mcpServers.Contains("filesystem", StringComparer.OrdinalIgnoreCase) && LooksLikeFilesystemPattern(pattern))
            return $"filesystem.{pattern}";

        return pattern;
    }

    private static bool LooksLikeFilesystemPattern(string pattern)
    {
        return pattern.StartsWith("read", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("list", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("search", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("get", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("write", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("create", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PoliciesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        if (left.Count != right.Count)
            return false;

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var otherValue) || !string.Equals(value, otherValue, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
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

    private static readonly IReadOnlyList<AgentRecord> BuiltInAgents =
    [
        new AgentRecord(
            Slug: AdeAgentId,
            Name: "Ade",
            Description: "A general assistant who helps directly, and hands work to a more suitable specialist when one is a better fit.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
            McpServers: [],
            PermissionPolicy: new Dictionary<string, string>(),
            SystemPrompt: """
                You are Ade, a general assistant and aide. Help the user directly whenever you can.

                If another specialist agent is clearly a better fit for the task, hand the work off to that agent by returning a routing decision. If no specialist is a better fit, keep the task yourself.

                Reply with **only** a JSON object — no markdown fences, no extra text:
                ```
                { "agent": "<agent-id>", "rewritten_prompt": "<clarified version of the user request>" }
                ```

                Rules:
                - If a specialist agent is a substantially better fit, pick the single most relevant specialist agent.
                - Rewrite the prompt to be clear and actionable for the chosen specialist.
                - If you can handle the task well yourself, return `{ "agent": null, "rewritten_prompt": null }`.
                - Do NOT explain your choice. Output the JSON object only.
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
            SELECT slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
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
    /// Returns a single agent by its slug (e.g. "developer.agent.md"), or null if not found.
    /// </summary>
    public AgentRecord? GetAgent(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled
            FROM agents
            WHERE slug = @slug
            """;
        cmd.Parameters.AddWithValue("slug", slug);

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
            INSERT INTO agents (slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled)
            VALUES (@slug, @name, @description, @backend, @model, @mcp_servers, @permission_policy, @system_prompt, @is_enabled)
            """;
        WriteAgentParameters(cmd, agent);
        cmd.ExecuteNonQuery();
    }

    public bool UpdateAgent(string slug, AgentRecord agent)
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
            WHERE slug = @slug
            """;
        WriteAgentParameters(cmd, agent with { Slug = slug });
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetAgentEnabled(string slug, bool isEnabled)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE agents SET is_enabled = @is_enabled WHERE slug = @slug";
        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("is_enabled", isEnabled);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyDictionary<string, int> GetSessionCountsByAgent()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_slug, COUNT(*)::INT FROM sessions GROUP BY agent_slug";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    public int CountSessionsForAgent(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*)::INT FROM sessions WHERE agent_slug = @slug";
        cmd.Parameters.AddWithValue("slug", slug);
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

            UpdateAgent(agent.Slug, agent with { McpServers = updatedServers });
            detachedAgents++;
        }

        return detachedAgents;
    }

    public IReadOnlyList<string> ListSessionIdsForAgent(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT session_id FROM sessions WHERE agent_slug = @slug ORDER BY created_at";
        cmd.Parameters.AddWithValue("slug", slug);

        var sessionIds = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sessionIds.Add(reader.GetString(0));

        return sessionIds;
    }

    public IReadOnlyList<StoredSession> ListSessions()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_id,
                   s.agent_slug,
                   s.created_at,
                   COALESCE(MAX(m.created_at), s.created_at) AS last_activity_at
            FROM sessions s
            LEFT JOIN messages m ON m.session_id = s.session_id
            GROUP BY s.session_id, s.agent_slug, s.created_at
            ORDER BY last_activity_at DESC, s.created_at DESC
            """;

        var sessions = new List<StoredSession>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new StoredSession(
                SessionId: reader.GetString(0),
                AgentSlug: reader.GetString(1),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(2),
                LastActivityAt: reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return sessions;
    }

    public int PurgeSessionsForAgent(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();

        using var countCmd = conn.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*)::INT FROM sessions WHERE agent_slug = @slug";
        countCmd.Parameters.AddWithValue("slug", slug);
        var count = (int)(countCmd.ExecuteScalar() ?? 0);

        using var msgCmd = conn.CreateCommand();
        msgCmd.Transaction = tx;
        msgCmd.CommandText = "DELETE FROM messages WHERE session_id IN (SELECT session_id FROM sessions WHERE agent_slug = @slug)";
        msgCmd.Parameters.AddWithValue("slug", slug);
        msgCmd.ExecuteNonQuery();

        using var eventLogCmd = conn.CreateCommand();
        eventLogCmd.Transaction = tx;
        eventLogCmd.CommandText = "DELETE FROM session_event_logs WHERE session_id IN (SELECT session_id FROM sessions WHERE agent_slug = @slug)";
        eventLogCmd.Parameters.AddWithValue("slug", slug);
        eventLogCmd.ExecuteNonQuery();

        using var sessionCmd = conn.CreateCommand();
        sessionCmd.Transaction = tx;
        sessionCmd.CommandText = "DELETE FROM sessions WHERE agent_slug = @slug";
        sessionCmd.Parameters.AddWithValue("slug", slug);
        sessionCmd.ExecuteNonQuery();

        tx.Commit();
        return count;
    }

    public bool DeleteAgent(string slug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agents WHERE slug = @slug";
        cmd.Parameters.AddWithValue("slug", slug);
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
            Slug: reader.GetString(0),
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
        cmd.Parameters.AddWithValue("slug", agent.Slug);
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
        cmd.CommandText = "SELECT agent_slug FROM sessions WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("sid", sessionId);

        var agentSlug = cmd.ExecuteScalar() as string;
        if (agentSlug is null)
            return null;

        var history = new ConversationHistory(sessionId, agentSlug);

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
    public void CreateSession(string sessionId, string agentSlug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (session_id, agent_slug) VALUES (@sid, @af)";
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("af", agentSlug);
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

    public void SaveEventLog(string sessionId, int assistantIndex, IReadOnlyList<StoredEventLogItem> items)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_event_logs (session_id, assistant_index, items)
            VALUES (@sid, @assistant_index, @items)
            ON CONFLICT (session_id, assistant_index) DO UPDATE
            SET items = EXCLUDED.items
            """;
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("assistant_index", assistantIndex);
        cmd.Parameters.Add("items", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(items, JsonOpts);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<IReadOnlyList<StoredEventLogItem>> LoadEventLogs(string sessionId)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT items FROM session_event_logs WHERE session_id = @sid ORDER BY assistant_index";
        cmd.Parameters.AddWithValue("sid", sessionId);

        var logs = new List<IReadOnlyList<StoredEventLogItem>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var items = JsonSerializer.Deserialize<List<StoredEventLogItem>>(reader.GetString(0), JsonOpts) ?? [];
            logs.Add(items);
        }

        return logs;
    }

    public void Dispose() => _dataSource.Dispose();
}

