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
    private const string AdeAgentId = "ade";

    private const string WorkspacePathSettingKey = "workspace_path";
    private readonly NpgsqlDataSource _dataSource;

    public static string DefaultWorkspacePath()
    {
        if (Directory.Exists("/workspace"))
            return "/workspace";

        if (Directory.Exists("/opt/sharpclaw/workspace"))
            return "/opt/sharpclaw/workspace";

        return Environment.CurrentDirectory;
    }

    public static string DefaultTelegramMappingStorePath()
    {
        if (Directory.Exists("/var/lib/sharpclaw"))
            return "/var/lib/sharpclaw/telegram-session-mappings.json";

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(baseDir, "sharpclaw", "telegram-session-mappings.json");
    }

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

            CREATE TABLE IF NOT EXISTS integration_settings (
                integration TEXT NOT NULL PRIMARY KEY,
                is_enabled BOOLEAN NOT NULL DEFAULT FALSE,
                bot_token TEXT NULL,
                allowed_user_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
                allowed_usernames JSONB NOT NULL DEFAULT '[]'::jsonb,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS backend_settings (
                backend TEXT NOT NULL PRIMARY KEY,
                is_enabled BOOLEAN NOT NULL DEFAULT FALSE,
                api_key TEXT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS auth_users (
                username TEXT NOT NULL PRIMARY KEY,
                password_hash TEXT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS token_usage (
                id SERIAL PRIMARY KEY,
                provider TEXT NOT NULL,
                agent_slug TEXT NOT NULL,
                usage_date DATE NOT NULL DEFAULT CURRENT_DATE,
                input_tokens BIGINT NOT NULL DEFAULT 0,
                output_tokens BIGINT NOT NULL DEFAULT 0,
                total_tokens BIGINT NOT NULL DEFAULT 0,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            CREATE INDEX IF NOT EXISTS idx_token_usage_provider_date
                ON token_usage (provider, usage_date);
            CREATE INDEX IF NOT EXISTS idx_token_usage_agent_date
                ON token_usage (agent_slug, usage_date);

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
            ALTER TABLE integration_settings ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE integration_settings ADD COLUMN IF NOT EXISTS bot_token TEXT NULL;
            ALTER TABLE integration_settings ADD COLUMN IF NOT EXISTS allowed_user_ids JSONB NOT NULL DEFAULT '[]'::jsonb;
            ALTER TABLE integration_settings ADD COLUMN IF NOT EXISTS allowed_usernames JSONB NOT NULL DEFAULT '[]'::jsonb;
            ALTER TABLE integration_settings ADD COLUMN IF NOT EXISTS mapping_store_path TEXT NULL;
            ALTER TABLE integration_settings ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE backend_settings ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE backend_settings ADD COLUMN IF NOT EXISTS api_key TEXT NULL;
            ALTER TABLE backend_settings ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE auth_users ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE app_settings ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();
            ALTER TABLE backend_settings ADD COLUMN IF NOT EXISTS daily_token_limit BIGINT NOT NULL DEFAULT 1000000;
            ALTER TABLE agents ADD COLUMN IF NOT EXISTS daily_token_limit BIGINT NULL;
            ALTER TABLE mcps ADD COLUMN IF NOT EXISTS url TEXT NULL;

            CREATE TABLE IF NOT EXISTS token_usage_alerts (
                id SERIAL PRIMARY KEY,
                provider TEXT NOT NULL,
                usage_date DATE NOT NULL,
                threshold INT NOT NULL,
                created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                UNIQUE (provider, usage_date, threshold)
            );
            """;
        cmd.ExecuteNonQuery();

        SeedMcps(conn);
        SeedAgents(conn);
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
                INSERT INTO mcps (slug, name, description, command, args, is_enabled, url)
                VALUES (@slug, @name, @description, @command, @args, @is_enabled, @url)
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
                    END,
                    url = CASE
                        WHEN mcps.url IS NULL THEN EXCLUDED.url
                        ELSE mcps.url
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

    // ── Built-in agent seed data ──────────────────────────────────────────────

    private static readonly IReadOnlyList<McpServerRecord> BuiltInMcps =
    [
        new McpServerRecord(
            Slug: "filesystem",
            Name: "Filesystem",
            Description: "Read and write files from allowed workspace directories.",
            Command: "npx",
            Args: ["-y", "@modelcontextprotocol/server-filesystem", "${WORKSPACE_PATH}"],
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

        new McpServerRecord(
            Slug: "duckduckgo",
            Name: "DuckDuckGo",
            Description: "Search the web and fetch page content using the Docker Catalog DuckDuckGo MCP server.",
            Command: "docker",
            Args: ["run", "-i", "--rm", "mcp/duckduckgo"],
            IsEnabled: true),

        new McpServerRecord(
            Slug: "knowledge-base",
            Name: "Knowledge Base",
            Description: "Read and write journal entries, daily notes, meeting notes, and personal notes in the configured knowledge base directory.",
            Command: "npx",
            Args: ["-y", "@modelcontextprotocol/server-filesystem", "${SHARPCLAW_KNOWLEDGE_BASE}"],
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
            McpServers: ["duckduckgo"],
            PermissionPolicy: new Dictionary<string, string>
            {
                { "duckduckgo.*", "auto_approve" },
            },
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

        new AgentRecord(
            Slug: "noah",
            Name: "Noah",
            Description: "Manages personal knowledge, work knowledge, meeting notes, and daily journal entries in Markdown using the knowledge-base MCP.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
            McpServers: ["knowledge-base", "duckduckgo"],
            PermissionPolicy: new Dictionary<string, string>
            {
                { "knowledge-base.read_*", "auto_approve" },
                { "knowledge-base.list_*", "auto_approve" },
                { "knowledge-base.search_*", "auto_approve" },
                { "knowledge-base.create_*", "auto_approve" },
                { "knowledge-base.write_*", "auto_approve" },
                { "knowledge-base.delete_*", "auto_approve" },
                { "duckduckgo.*", "auto_approve" },
                { "*", "ask" },
            },
            SystemPrompt: """
                You are Noah, a knowledgebase manager optimized for fast, reliable note-taking.

                Mission:
                - Keep the user's knowledge base organized, searchable, and up to date.
                - Manage personal notes, work knowledge, meeting notes, and daily journal entries.
                - Produce and maintain high-quality Markdown notes.

                Core behavior:
                - Always use the `knowledge-base` MCP for knowledgebase work.
                - Do not invent file paths or claim a note exists without checking via MCP tools.
                - Operate at speed while maintaining safety through careful planning and clear reasoning.

                Critical safety practice:
                - **Before deleting or overwriting a note, ALWAYS:**
                  1. Read the current content via MCP
                  2. Show the user exactly what will be lost
                  3. Ask for explicit confirmation with the user's exact action
                  4. Only proceed after confirmed agreement
                - You are trusted to execute create/write actions swiftly, but deletion/overwrite requires the user to see and approve.

                Markdown standards:
                - Write all note content in Markdown.
                - Use clear headings, concise sections, and actionable bullet lists.
                - Preferred structure for new notes:
                  - `# Title`
                  - `## Context`
                  - `## Notes`
                  - `## Actions`
                  - `## Follow-ups`
                - Use ISO dates (`YYYY-MM-DD`) for metadata and date references.

                Knowledge organization:
                - Classify requests into: personal, work, meeting (with date), or daily (with date).
                - Reuse existing notes when appropriate instead of creating duplicates.
                - Suggest a canonical path/filename if none is provided.
                - For meetings: capture attendees, agenda, decisions, action items with owners and due dates.
                - For dailies: capture priorities, progress, blockers, and reflections.

                Editing behavior:
                - For non-destructive edits (appending, clarifying): propose the change and execute swiftly.
                - For structural changes (reorganizing sections): read first, show the plan, get approval.
                - Preserve valuable existing content; append or revise surgically.
                - Summarize exactly what changed after edits.

                Response style:
                - Be concise, structured, and practical.
                - Ask clarification questions only when necessary.
                - When uncertain, state assumptions and proceed with the safest default.
                - Balance speed with respect for the user's accumulated knowledge.
                """,
            IsEnabled: true),

        new AgentRecord(
            Slug: "cody",
            Name: "Cody",
            Description: "Software architect and developer skilled in C#, TypeScript, and Python with deep expertise in design patterns and SOLID principles.",
            Backend: "copilot",
            Model: "claude-opus-4.6",
            McpServers: ["filesystem", "github", "duckduckgo"],
            PermissionPolicy: new Dictionary<string, string>
            {
                { "filesystem.read_*", "auto_approve" },
                { "filesystem.list_*", "auto_approve" },
                { "filesystem.search_*", "auto_approve" },
                { "filesystem.create_*", "auto_approve" },
                { "filesystem.write_*", "auto_approve" },
                { "filesystem.edit_*", "auto_approve" },
                { "filesystem.directory_*", "auto_approve" },
                { "filesystem.delete_*", "ask" },
                { "github.read_*", "auto_approve" },
                { "github.search_*", "auto_approve" },
                { "duckduckgo.*", "auto_approve" },
                { "builtin.read_file", "auto_approve" },
                { "builtin.write_file", "auto_approve" },
                { "builtin.run_command", "ask" },
                { "*", "ask" },
            },
            SystemPrompt: """
                You are Cody, a skilled software architect and developer. Your expertise spans C#, TypeScript, and Python with deep knowledge of design patterns and SOLID principles.

                Core principles:
                - **SOLID first**: Single Responsibility, Open/Closed, Liskov Substitution, Interface Segregation, Dependency Inversion.
                - Write code that is clean, maintainable, and testable.
                - Design for change; anticipate where tomorrow's requirements might diverge.
                - Balance pragmatism with architectural integrity; don't over-engineer.

                Primary languages:
                - **C#**: Full stack from ASP.NET Core APIs to desktop/console apps; async/await mastery; LINQ; dependency injection; Entity Framework.
                - **TypeScript**: Modern frontend/backend; React/Vue patterns; async patterns; strong typing discipline.
                - **Python**: Data processing, scripting, scientific computing, web frameworks (FastAPI, Django).

                Key practices:
                - Every class should have one reason to change.
                - Prefer composition over inheritance.
                - Depend on abstractions, not concretions.
                - Use interfaces to define contracts; implementations should be swappable.
                - Keep functions small, pure when possible, with clear names.
                - Write tests alongside code; aim for meaningful coverage.
                - Use proper error handling and logging; fail loudly, recover gracefully.
                - Document the "why" not the "what"—code shows what it does, comments explain reasoning.

                Design patterns you leverage:
                - Factory, Strategy, Observer, Decorator, Adapter, Repository, Dependency Injection
                - Event-driven architectures; async/await; reactive patterns where appropriate
                - Domain-driven design for complex business logic

                Code review mindset:
                - Read code as if you're the next maintainer.
                - Look for clarity, resilience, and adherence to principles.
                - Suggest improvements with reasoning; explain trade-offs.
                - Respect existing patterns in a codebase; don't innovate inconsistently.

                Workflow:
                1. **On reading code**: Understand the current design, its constraints, and its debt.
                2. **On writing code**: Propose the design approach first; build incrementally with tests.
                3. **On refactoring**: Show the before, explain the principle, show the after, explain the gain.
                4. **On deletion**: Read the code first, check for dependents, ask for confirmation.

                Communication style:
                - Be precise and technical; avoid fluff.
                - Explain trade-offs transparently: performance vs. readability, simplicity vs. extensibility.
                - Show code examples; let them speak louder than words.
                - When uncertain about a design choice, state your assumptions and propose alternatives.
                """,
            IsEnabled: true),

        new AgentRecord(
            Slug: "debbie",
            Name: "Debbie",
            Description: "A critical thinking partner who challenges ideas, finds gaps, and plays devil's advocate to strengthen your thinking.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
            McpServers: ["duckduckgo"],
            PermissionPolicy: new Dictionary<string, string>
            {
                { "duckduckgo.*", "auto_approve" },
            },
            SystemPrompt: """
                You are Debbie, a rigorous thinking partner and critical reviewer. Your role is to challenge ideas constructively, expose gaps, and improve thinking through intelligent scrutiny.

                Core mindset:
                - Assume nothing is perfect; there's always room for improvement.
                - Don't validate or agree for the sake of politeness.
                - Play devil's advocate respectfully—test ideas by trying to poke holes in them.
                - Help the user think stronger, not feel better.

                What you do:
                - **Question assumptions**: What's taken for granted? What would break if that assumption is wrong?
                - **Probe for gaps**: What's missing from this plan? What edge cases haven't been considered?
                - **Challenge the solution**: Is this the best approach? What alternatives weren't explored? Why was that rejected?
                - **Test requirements**: Are they complete? Are they contradictory? What happens if they change?
                - **Push on process**: Is the workflow sound? Who hasn't been consulted? What could go wrong?
                - **Examine trade-offs**: What's being sacrificed for this choice? Is the cost worth the benefit? Is there a better balance?

                Your style:
                - Be direct, not harsh. Disagreement is intellectual, not personal.
                - Ask questions, don't just declare problems.
                - When you spot a flaw, explain why it matters and what could result from ignoring it.
                - Offer alternatives or experiments when possible; don't just say "that won't work."
                - Acknowledge valid reasoning; credit good decisions before challenging the rest.
                - Use specific examples from what you're reviewing.

                What you don't do:
                - Don't be cynical or dismissive.
                - Don't criticize without constructive intent.
                - Don't override the user's judgment; help them make better decisions.
                - Don't demand perfection; help them understand risks and trade-offs.

                Engagement approach:
                - Start by reflecting back what you heard, so the user can correct misunderstandings.
                - Ask what the user is most uncertain about, then probe there first.
                - Organize feedback by severity: critical flaws first, then refinements.
                - Always end with a question or invitation to defend/revise.

                Remember:
                - A good challenge makes thinking clearer, not smaller.
                - The goal is a more robust idea, not a defeated user.
                - Respect expertise; challenge conclusions, not competence.
                """,
            IsEnabled: true),

        new AgentRecord(
            Slug: "remy",
            Name: "Remy",
            Description: "Helps you capture, organize, and manage reminders, todos, and shopping lists efficiently.",
            Backend: "anthropic",
            Model: "claude-haiku-4-5-20251001",
            McpServers: ["knowledge-base", "duckduckgo"],
            PermissionPolicy: new Dictionary<string, string>
            {
                { "knowledge-base.read_*", "auto_approve" },
                { "knowledge-base.list_*", "auto_approve" },
                { "knowledge-base.search_*", "auto_approve" },
                { "knowledge-base.create_*", "auto_approve" },
                { "knowledge-base.write_*", "auto_approve" },
                { "knowledge-base.delete_*", "auto_approve" },
                { "duckduckgo.*", "auto_approve" },
                { "*", "ask" },
            },
            SystemPrompt: """
                You are Remy, a task and reminder manager. Your mission is to get things out of the user's head and into organized, actionable systems.

                Core mission:
                - Capture todos, reminders, and shopping lists quickly and reliably.
                - Keep them organized by category, priority, and due date.
                - Help the user review, rearrange, and complete tasks.
                - Make your systems low-friction so nothing slips through.

                Task management style:
                - Todos have a clear description, priority (high/medium/low), and optional due date.
                - Group related todos together (e.g., "Home", "Work", "Personal", "Shopping").
                - Mark completed items with strikethrough or move to a "Done" section.
                - Archive rather than delete; preserve context for future reference.

                Shopping list best practices:
                - Organize by store section (Produce, Dairy, Meat, Pantry, etc.) for efficient shopping.
                - Include quantities and any special notes (organic, specific brand, etc.).
                - Mark items as purchased and clear them out after shopping.
                - Keep a "recurring items" list for staples you buy regularly.

                Reminders handling:
                - Record reminders with a clear trigger (date, time, or event-based).
                - Examples: "Remind me to call the dentist on 2026-04-15", "Remind me to review quarterly goals at month-end".
                - Review upcoming reminders proactively; ask the user if anything needs rescheduling.

                File organization:
                - Use the `knowledge-base` MCP to store these as Markdown files.
                - Suggested structure:
                  - `todos/todos.md` — master todo list, organized by category
                  - `reminders/upcoming.md` — active reminders with dates
                  - `shopping/shopping-list.md` — current shopping list by section
                  - `shopping/recurring-items.md` — staples to reorder regularly

                Workflow:
                1. **Capturing**: User says "remind me to..." or "add to my todo..." → capture immediately with context.
                2. **Organizing**: Suggest categories, priorities, due dates; ask if needed.
                3. **Reviewing**: Ask weekly/monthly: "What's done? What's blocked? What needs rescheduling?"
                4. **Cleaning up**: Archive completed items; remove stale reminders.

                Editing behavior:
                - For quick additions (append to list): propose and execute swiftly.
                - For reorganizing (priority changes, category shifts): read the file, show the plan, get approval.
                - For deletions: read the item, ask for confirmation.

                Communication style:
                - Be brisk and action-oriented; long discussions about task management are counter-productive.
                - Confirm captures: "Got it—I've added 'X' to your [category] with due date [date]."
                - Offer summaries: "You have 7 open todos: 2 high priority (due this week), 3 medium, 2 low."
                - When uncertain: "Where should this go? [category options]" or "When is this due?"

                Remember:
                - The goal is a clear mind and completed tasks, not a perfect system.
                - Regular review prevents pileup; suggest a weekly check-in.
                - Celebrate done items; they're progress.
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
            SELECT slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled, daily_token_limit
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
            SELECT slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled, daily_token_limit
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
            SELECT slug, name, description, command, args::text, is_enabled, url
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
            SELECT slug, name, description, command, args::text, is_enabled, url
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
            INSERT INTO mcps (slug, name, description, command, args, is_enabled, url)
            VALUES (@slug, @name, @description, @command, @args, @is_enabled, @url)
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
                is_enabled = @is_enabled,
                url = @url
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
            INSERT INTO agents (slug, name, description, backend, model, mcp_servers, permission_policy, system_prompt, is_enabled, daily_token_limit)
            VALUES (@slug, @name, @description, @backend, @model, @mcp_servers, @permission_policy, @system_prompt, @is_enabled, @daily_token_limit)
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
                is_enabled = @is_enabled,
                daily_token_limit = @daily_token_limit
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

    public TelegramIntegrationSettings GetTelegramIntegrationSettings()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT is_enabled, bot_token, allowed_user_ids::text, allowed_usernames::text, mapping_store_path
            FROM integration_settings
            WHERE integration = 'telegram'
            """;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return new TelegramIntegrationSettings(
                IsEnabled: false,
                BotToken: null,
                AllowedUserIds: [],
                AllowedUsernames: [],
                MappingStorePath: DefaultTelegramMappingStorePath());
        }

        var allowedUserIds = JsonSerializer.Deserialize<List<long>>(reader.GetString(2), JsonOpts) ?? [];
        var allowedUsernames = JsonSerializer.Deserialize<List<string>>(reader.GetString(3), JsonOpts) ?? [];
        var token = reader.IsDBNull(1) ? null : NormalizeOptionalString(reader.GetString(1));
        var mappingStorePath = reader.IsDBNull(4)
            ? DefaultTelegramMappingStorePath()
            : NormalizeOptionalString(reader.GetString(4)) ?? DefaultTelegramMappingStorePath();

        return new TelegramIntegrationSettings(
            IsEnabled: reader.GetBoolean(0),
            BotToken: token,
            AllowedUserIds: allowedUserIds,
            AllowedUsernames: allowedUsernames,
            MappingStorePath: mappingStorePath);
    }

    public void UpsertTelegramIntegrationSettings(TelegramIntegrationSettings settings)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO integration_settings (integration, is_enabled, bot_token, allowed_user_ids, allowed_usernames, mapping_store_path, updated_at)
            VALUES ('telegram', @is_enabled, @bot_token, @allowed_user_ids, @allowed_usernames, @mapping_store_path, NOW())
            ON CONFLICT (integration) DO UPDATE
            SET is_enabled = EXCLUDED.is_enabled,
                bot_token = EXCLUDED.bot_token,
                allowed_user_ids = EXCLUDED.allowed_user_ids,
                allowed_usernames = EXCLUDED.allowed_usernames,
                mapping_store_path = EXCLUDED.mapping_store_path,
                updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue("is_enabled", settings.IsEnabled);
        cmd.Parameters.AddWithValue("bot_token", (object?)NormalizeOptionalString(settings.BotToken) ?? DBNull.Value);
        cmd.Parameters.Add("allowed_user_ids", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(settings.AllowedUserIds, JsonOpts);
        cmd.Parameters.Add("allowed_usernames", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(settings.AllowedUsernames, JsonOpts);
        cmd.Parameters.AddWithValue("mapping_store_path", (object?)NormalizeOptionalString(settings.MappingStorePath) ?? DefaultTelegramMappingStorePath());
        cmd.ExecuteNonQuery();
    }

    public string GetWorkspacePath()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = @key";
        cmd.Parameters.AddWithValue("key", WorkspacePathSettingKey);

        var raw = cmd.ExecuteScalar() as string;
        var normalized = NormalizeOptionalString(raw);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var defaultPath = DefaultWorkspacePath();
        UpsertWorkspacePath(defaultPath);
        return defaultPath;
    }

    public void UpsertWorkspacePath(string workspacePath)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES (@key, @value, NOW())
            ON CONFLICT (key) DO UPDATE
            SET value = EXCLUDED.value,
                updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue("key", WorkspacePathSettingKey);
        cmd.Parameters.AddWithValue("value", NormalizeOptionalString(workspacePath) ?? DefaultWorkspacePath());
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<BackendIntegrationSettings> ListBackendIntegrationSettings()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT backend, is_enabled, api_key, updated_at, daily_token_limit
            FROM backend_settings
            ORDER BY backend
            """;

        var settings = new List<BackendIntegrationSettings>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            settings.Add(new BackendIntegrationSettings(
                Backend: reader.GetString(0),
                IsEnabled: reader.GetBoolean(1),
                ApiKey: reader.IsDBNull(2) ? null : NormalizeOptionalString(reader.GetString(2)),
                UpdatedAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                DailyTokenLimit: reader.GetInt64(4)));
        }

        return settings;
    }

    public BackendIntegrationSettings? GetBackendIntegrationSettings(string backend)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT backend, is_enabled, api_key, updated_at, daily_token_limit
            FROM backend_settings
            WHERE backend = @backend
            """;
        cmd.Parameters.AddWithValue("backend", backend);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new BackendIntegrationSettings(
            Backend: reader.GetString(0),
            IsEnabled: reader.GetBoolean(1),
            ApiKey: reader.IsDBNull(2) ? null : NormalizeOptionalString(reader.GetString(2)),
            UpdatedAt: reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            DailyTokenLimit: reader.GetInt64(4));
    }

    public void UpsertBackendIntegrationSettings(BackendIntegrationSettings settings)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO backend_settings (backend, is_enabled, api_key, daily_token_limit, updated_at)
            VALUES (@backend, @is_enabled, @api_key, @daily_token_limit, NOW())
            ON CONFLICT (backend) DO UPDATE
            SET is_enabled = EXCLUDED.is_enabled,
                api_key = EXCLUDED.api_key,
                daily_token_limit = EXCLUDED.daily_token_limit,
                updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue("backend", settings.Backend);
        cmd.Parameters.AddWithValue("is_enabled", settings.IsEnabled);
        cmd.Parameters.AddWithValue("api_key", (object?)NormalizeOptionalString(settings.ApiKey) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("daily_token_limit", settings.DailyTokenLimit);
        cmd.ExecuteNonQuery();
    }

    public bool HasAuthUsers()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM auth_users)";
        return (bool)(cmd.ExecuteScalar() ?? false);
    }

    public (string Username, string PasswordHash)? GetSingleAuthUser()
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT username, password_hash FROM auth_users ORDER BY created_at LIMIT 1";

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetString(0), reader.GetString(1));
    }

    public void CreateAuthUser(string username, string passwordHash)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO auth_users (username, password_hash, created_at, updated_at)
            VALUES (@username, @password_hash, NOW(), NOW())
            """;
        cmd.Parameters.AddWithValue("username", username);
        cmd.Parameters.AddWithValue("password_hash", passwordHash);
        cmd.ExecuteNonQuery();
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

    public bool DeleteSession(string sessionId)
    {
        using var conn = _dataSource.OpenConnection();
        using var tx = conn.BeginTransaction();

        using var countCmd = conn.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*)::INT FROM sessions WHERE session_id = @sid";
        countCmd.Parameters.AddWithValue("sid", sessionId);
        var exists = (int)(countCmd.ExecuteScalar() ?? 0) > 0;
        if (!exists)
            return false;

        using var eventLogCmd = conn.CreateCommand();
        eventLogCmd.Transaction = tx;
        eventLogCmd.CommandText = "DELETE FROM session_event_logs WHERE session_id = @sid";
        eventLogCmd.Parameters.AddWithValue("sid", sessionId);
        eventLogCmd.ExecuteNonQuery();

        using var msgCmd = conn.CreateCommand();
        msgCmd.Transaction = tx;
        msgCmd.CommandText = "DELETE FROM messages WHERE session_id = @sid";
        msgCmd.Parameters.AddWithValue("sid", sessionId);
        msgCmd.ExecuteNonQuery();

        using var sessionCmd = conn.CreateCommand();
        sessionCmd.Transaction = tx;
        sessionCmd.CommandText = "DELETE FROM sessions WHERE session_id = @sid";
        sessionCmd.Parameters.AddWithValue("sid", sessionId);
        sessionCmd.ExecuteNonQuery();

        tx.Commit();
        return true;
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
            IsEnabled: reader.GetBoolean(8),
            DailyTokenLimit: reader.IsDBNull(9) ? null : reader.GetInt64(9));
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
            IsEnabled: reader.GetBoolean(5),
            Url: reader.IsDBNull(6) ? null : reader.GetString(6));
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
        cmd.Parameters.AddWithValue("daily_token_limit", (object?)agent.DailyTokenLimit ?? DBNull.Value);
    }

    private static void WriteMcpParameters(NpgsqlCommand cmd, McpServerRecord mcp)
    {
        cmd.Parameters.AddWithValue("slug", mcp.Slug);
        cmd.Parameters.AddWithValue("name", mcp.Name);
        cmd.Parameters.AddWithValue("description", mcp.Description);
        cmd.Parameters.AddWithValue("command", mcp.Command);
        cmd.Parameters.Add("args", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(mcp.Args, JsonOpts);
        cmd.Parameters.AddWithValue("is_enabled", mcp.IsEnabled);
        cmd.Parameters.AddWithValue("url", (object?)mcp.Url ?? DBNull.Value);
    }

    private static string? NormalizeOptionalString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
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

    /// <summary>
    /// Updates the agent assignment for an existing session.
    /// </summary>
    public void UpdateSessionAgent(string sessionId, string agentSlug)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET agent_slug = @slug WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("slug", agentSlug);
        cmd.Parameters.AddWithValue("sid", sessionId);
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

    // ── Token Usage ─────────────────────────────────────────────────────────

    /// <summary>
    /// Records a token usage entry for the given provider and agent on the current date.
    /// </summary>
    public void RecordTokenUsage(string provider, string agentSlug, long inputTokens, long outputTokens)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO token_usage (provider, agent_slug, usage_date, input_tokens, output_tokens, total_tokens)
            VALUES (@provider, @agent_slug, CURRENT_DATE, @input_tokens, @output_tokens, @total_tokens)
            """;
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("agent_slug", agentSlug);
        cmd.Parameters.AddWithValue("input_tokens", inputTokens);
        cmd.Parameters.AddWithValue("output_tokens", outputTokens);
        cmd.Parameters.AddWithValue("total_tokens", inputTokens + outputTokens);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns true and inserts the alert record if this is the first time a
    /// threshold has been crossed for this provider on this date. Returns false
    /// if the alert was already recorded.
    /// </summary>
    public bool TryRecordThresholdAlert(string provider, DateOnly date, int threshold)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO token_usage_alerts (provider, usage_date, threshold)
            VALUES (@provider, @date, @threshold)
            ON CONFLICT (provider, usage_date, threshold) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("date", date);
        cmd.Parameters.AddWithValue("threshold", threshold);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Returns the total token usage for a given provider on a given date.
    /// </summary>
    public long GetDailyProviderTokenUsage(string provider, DateOnly date)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(total_tokens), 0)
            FROM token_usage
            WHERE provider = @provider AND usage_date = @date
            """;
        cmd.Parameters.AddWithValue("provider", provider);
        cmd.Parameters.AddWithValue("date", date);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Returns the total token usage for a given agent on a given date.
    /// </summary>
    public long GetDailyAgentTokenUsage(string agentSlug, DateOnly date)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(total_tokens), 0)
            FROM token_usage
            WHERE agent_slug = @agent_slug AND usage_date = @date
            """;
        cmd.Parameters.AddWithValue("agent_slug", agentSlug);
        cmd.Parameters.AddWithValue("date", date);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Returns daily provider usage summaries for all enabled providers for a given date.
    /// </summary>
    public IReadOnlyList<ProviderDailyUsage> GetProviderDailyUsageSummary(DateOnly date)
    {
        var backendSettings = ListBackendIntegrationSettings();
        var results = new List<ProviderDailyUsage>();

        foreach (var bs in backendSettings)
        {
            var usage = GetDailyProviderTokenUsage(bs.Backend, date);
            results.Add(new ProviderDailyUsage(bs.Backend, date, usage, bs.DailyTokenLimit));
        }

        return results;
    }

    /// <summary>
    /// Returns daily agent usage summaries for all agents for a given date.
    /// </summary>
    public IReadOnlyList<AgentDailyUsage> GetAgentDailyUsageSummary(DateOnly date)
    {
        var agents = ListAgents();
        var results = new List<AgentDailyUsage>();

        foreach (var agent in agents)
        {
            var usage = GetDailyAgentTokenUsage(agent.Slug, date);
            results.Add(new AgentDailyUsage(agent.Slug, date, usage, agent.DailyTokenLimit));
        }

        return results;
    }

    /// <summary>
    /// Returns token usage data points for charting, grouped by time bucket and agent.
    /// </summary>
    public IReadOnlyList<TokenUsageDataPoint> GetTokenUsageHistory(string period)
    {
        using var conn = _dataSource.OpenConnection();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = period switch
        {
            "day" => """
                SELECT to_char(created_at, 'HH24:00') AS bucket,
                       agent_slug,
                       COALESCE(SUM(total_tokens), 0)
                FROM token_usage
                WHERE usage_date = CURRENT_DATE
                GROUP BY bucket, agent_slug
                ORDER BY bucket, agent_slug
                """,
            "week" => """
                SELECT to_char(usage_date, 'YYYY-MM-DD') AS bucket,
                       agent_slug,
                       COALESCE(SUM(total_tokens), 0)
                FROM token_usage
                WHERE usage_date >= CURRENT_DATE - INTERVAL '6 days'
                GROUP BY bucket, agent_slug
                ORDER BY bucket, agent_slug
                """,
            _ => """
                SELECT to_char(usage_date, 'YYYY-MM-DD') AS bucket,
                       agent_slug,
                       COALESCE(SUM(total_tokens), 0)
                FROM token_usage
                WHERE usage_date >= CURRENT_DATE - INTERVAL '29 days'
                GROUP BY bucket, agent_slug
                ORDER BY bucket, agent_slug
                """,
        };

        var dataPoints = new List<TokenUsageDataPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            dataPoints.Add(new TokenUsageDataPoint(
                Bucket: reader.GetString(0),
                AgentSlug: reader.GetString(1),
                TotalTokens: Convert.ToInt64(reader.GetValue(2))));
        }

        return dataPoints;
    }

    public void Dispose() => _dataSource.Dispose();
}

