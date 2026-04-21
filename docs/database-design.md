# Database Design

SharpClaw uses PostgreSQL as its primary data store, providing persistent storage for agents, conversations, configuration, and system metadata. This document details the database schema design and the reasoning behind key decisions.

## Schema Overview

The database consists of several core tables organized around the main entities:

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│   agents    │    │  sessions   │    │  messages   │
│             │◄───│             │◄───│             │
│ • slug (PK) │    │ • session_id│    │ • id (PK)   │
│ • name      │    │ • agent_slug│    │ • role      │
│ • backend   │    │ • created_at│    │ • content   │
│ • model     │    └─────────────┘    └─────────────┘
│ • system_   │
│   prompt    │
└─────────────┘
```

## Core Tables

### agents
**Purpose**: Stores agent definitions and configurations

```sql
CREATE TABLE agents (
    id SERIAL PRIMARY KEY,
    slug TEXT NOT NULL UNIQUE,              -- Agent identifier (e.g., "cody", "ade")
    name TEXT NOT NULL,                     -- Human-readable name
    description TEXT NOT NULL DEFAULT '',   -- Agent description
    backend TEXT NOT NULL DEFAULT 'anthropic', -- LLM provider
    model TEXT NOT NULL DEFAULT '',         -- Specific model name
    mcp_servers TEXT NOT NULL DEFAULT '[]', -- JSON array of MCP servers
    permission_policy TEXT NOT NULL DEFAULT '{}', -- JSON permission rules
    system_prompt TEXT NOT NULL,            -- Agent's system prompt
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Key Design Decisions**:
- `slug` as natural key for API references (more readable than numeric IDs)
- `mcp_servers` as JSON array for flexibility in MCP assignments
- `permission_policy` as JSON for complex permission rules
- `system_prompt` as TEXT to support large prompts

### sessions
**Purpose**: Conversation threads between users and agents

```sql
CREATE TABLE sessions (
    session_id TEXT NOT NULL,               -- UUID session identifier
    agent_slug TEXT NOT NULL,               -- References agents.slug
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_archived BOOLEAN NOT NULL DEFAULT FALSE,  -- Archive status flag
    archived_at TIMESTAMPTZ NULL,          -- Timestamp when archived
    PRIMARY KEY (session_id)
);
```

**Key Design Decisions**:
- UUID session IDs for security and uniqueness
- Direct reference to agent slug for efficient lookups
- No explicit user_id (single-user system currently)
- **Archive support**: `is_archived` flag with optional `archived_at` timestamp for session lifecycle management
- **Knowledge integration**: Archived sessions can generate knowledge summaries

### messages
**Purpose**: Individual messages within conversations

```sql
CREATE TABLE messages (
    id SERIAL PRIMARY KEY,
    session_id TEXT NOT NULL,               -- References sessions.session_id
    role TEXT NOT NULL,                     -- "user", "assistant", "system"
    content TEXT NOT NULL,                  -- Message content
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);
```

**Key Design Decisions**:
- `role` follows OpenAI/Anthropic message format conventions
- Foreign key constraint ensures referential integrity
- Simple TEXT content (complex formatting handled at application layer)

## Supporting Tables

### mcps (Model Context Protocol Servers)
**Purpose**: Registry of available MCP tool servers

```sql
CREATE TABLE mcps (
    id SERIAL PRIMARY KEY,
    slug TEXT NOT NULL UNIQUE,              -- MCP server identifier
    name TEXT NOT NULL,                     -- Human-readable name
    description TEXT NOT NULL DEFAULT '',   -- Server description
    command TEXT NOT NULL,                  -- Executable command
    args JSONB NOT NULL DEFAULT '[]'::jsonb, -- Command arguments as JSON
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Key Design Decisions**:
- JSONB for command arguments (efficient JSON operations)
- Separate from agents table for many-to-many relationship
- `command` and `args` allow dynamic MCP server launching

### session_event_logs
**Purpose**: Detailed execution logs for debugging and analysis

```sql
CREATE TABLE session_event_logs (
    id SERIAL PRIMARY KEY,
    session_id TEXT NOT NULL,
    assistant_index INT NOT NULL,           -- Which assistant turn
    items JSONB NOT NULL DEFAULT '[]'::jsonb, -- Event log items
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (session_id, assistant_index),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);
```

**Key Design Decisions**:
- JSONB for flexible event storage (tools calls, permissions, etc.)
- `assistant_index` tracks multiple agent turns within a session
- Unique constraint prevents duplicate logs for same turn

### backend_settings
**Purpose**: LLM provider configuration and API keys

```sql
CREATE TABLE backend_settings (
    backend TEXT NOT NULL PRIMARY KEY,      -- Provider name (anthropic, openai, etc.)
    is_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    api_key TEXT NULL,                      -- Encrypted API key
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Key Design Decisions**:
- Provider name as primary key (fixed set of providers)
- `is_enabled` allows selective provider activation
- API keys stored encrypted (application-level encryption)

### auth_users
**Purpose**: User authentication for web interface

```sql
CREATE TABLE auth_users (
    username TEXT NOT NULL PRIMARY KEY,
    password_hash TEXT NOT NULL,            -- bcrypt hashed password
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Key Design Decisions**:
- Username as primary key (single-user focused system)
- bcrypt password hashing for security
- Simple table structure (no roles/permissions yet)

### app_settings
**Purpose**: Application configuration key-value store

```sql
CREATE TABLE app_settings (
    key TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Key Design Decisions**:
- Generic key-value store for application settings
- TEXT values (JSON parsing at application layer when needed)
- Used for workspace path, heartbeat settings, etc.

### token_usage
**Purpose**: Track LLM API usage for cost monitoring

```sql
CREATE TABLE token_usage (
    id SERIAL PRIMARY KEY,
    provider TEXT NOT NULL,                 -- LLM provider name
    agent_slug TEXT NOT NULL,              -- Which agent used tokens
    usage_date DATE NOT NULL DEFAULT CURRENT_DATE,
    input_tokens BIGINT NOT NULL DEFAULT 0,
    output_tokens BIGINT NOT NULL DEFAULT 0,
    total_tokens BIGINT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Key Design Decisions**:
- Separate tracking by provider and agent
- Daily aggregation via `usage_date`
- BIGINT for large token counts

## Indexes and Performance

### Automatic Indexes
- Primary keys automatically indexed
- Unique constraints create indexes

### Recommended Additional Indexes
```sql
-- Session lookup by agent
CREATE INDEX idx_sessions_agent_slug ON sessions(agent_slug);

-- Message ordering within sessions
CREATE INDEX idx_messages_session_created ON messages(session_id, created_at);

-- Token usage aggregation
CREATE INDEX idx_token_usage_provider_date ON token_usage(provider, usage_date);

-- Event log lookup
CREATE INDEX idx_event_logs_session ON session_event_logs(session_id);
```

## Data Integrity

### Foreign Key Constraints
- `messages.session_id` → `sessions.session_id`
- `session_event_logs.session_id` → `sessions.session_id`

### Business Logic Constraints
- Agent slugs must be unique and URL-safe
- Session IDs must be UUIDs
- Message roles must be valid ("user", "assistant", "system")
- Backend settings must reference valid provider names

## JSON Data Patterns

### Agent MCP Servers
```json
["filesystem", "github", "duckduckgo"]
```

### Permission Policies
```json
{
  "filesystem.read_*": "auto_approve",
  "filesystem.write_*": "ask",
  "github.*": "auto_approve",
  "*": "deny"
}
```

### Event Log Items
```json
[
  {
    "type": "tool_call",
    "tool": "filesystem",
    "function": "read_file",
    "args": {"path": "/workspace/file.txt"}
  },
  {
    "type": "tool_result", 
    "content": "File contents...",
    "status": "success"
  }
]
```

## Migration Strategy

### Schema Evolution
- Use PostgreSQL `CREATE TABLE IF NOT EXISTS` for backward compatibility
- Add new columns with `ALTER TABLE` in future versions
- JSON fields provide schema flexibility without migrations

### Data Migration
- Built-in agent seeding from markdown files
- Default settings initialization
- Graceful handling of missing configuration

## Backup and Maintenance

### Backup Strategy
- Regular PostgreSQL dumps for full backup
- Point-in-time recovery via WAL archiving
- Configuration backup includes environment variables

### Maintenance Tasks
- Regular vacuum/analyze for performance
- Token usage cleanup (retention policies)
- Session archival for old conversations
- Event log rotation for disk space management