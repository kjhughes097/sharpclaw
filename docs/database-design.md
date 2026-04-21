# Database Design

SharpClaw uses PostgreSQL as its primary data store with a schema designed for conversation management, agent configuration, and extensible metadata storage.

## Schema Overview

The database schema is automatically created and migrated by the `SessionStore` class on application startup, ensuring consistency across deployments.

```sql
-- Core conversation data
sessions → messages → session_event_logs

-- Agent and tool configuration  
agents ↔ mcps (many-to-many via agent configuration)

-- System configuration
auth_users, app_settings, integration_settings, backend_settings

-- Analytics and monitoring
token_usage, heartbeat data
```

## Core Tables

### Sessions Table

**Purpose**: Tracks individual conversation sessions between users and agents.

```sql
CREATE TABLE sessions (
    session_id TEXT NOT NULL PRIMARY KEY,
    agent_slug TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_activity_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    is_archived BOOLEAN NOT NULL DEFAULT FALSE,
    archived_at TIMESTAMPTZ NULL
);
```

**Key Design Decisions**:
- **Text-based session IDs** for URL-friendly identifiers
- **Agent slug reference** (not foreign key) for flexibility in agent management
- **Archiving support** with timestamps for session lifecycle management
- **Activity tracking** for session timeout and cleanup policies

**Usage Patterns**:
- Sessions created on first user message to an agent
- `last_activity_at` updated on every message exchange
- Archiving triggered manually or via automated policies
- Archived sessions retain full history but are marked as complete

### Messages Table

**Purpose**: Stores the conversational message history for each session.

```sql
CREATE TABLE messages (
    id SERIAL PRIMARY KEY,
    session_id TEXT NOT NULL,
    role TEXT NOT NULL,        -- 'user' | 'assistant' | 'system'
    content TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);
```

**Design Rationale**:
- **Simple message storage** with role-based categorization
- **Referential integrity** ensures messages belong to valid sessions
- **Chronological ordering** via auto-incrementing ID and timestamp
- **Flexible content storage** supports text, markdown, and future rich content

**Message Roles**:
- `user` - Human input messages
- `assistant` - Agent responses and tool outputs  
- `system` - Internal system messages and notifications

### Session Event Logs Table

**Purpose**: Detailed logging of agent execution events, tool calls, and internal operations.

```sql
CREATE TABLE session_event_logs (
    id SERIAL PRIMARY KEY,
    session_id TEXT NOT NULL,
    assistant_index INT NOT NULL,     -- Message sequence number
    items JSONB NOT NULL DEFAULT '[]'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (session_id, assistant_index),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);
```

**Event Storage Design**:
- **JSONB format** for flexible event data structure
- **Assistant indexing** links events to specific agent responses
- **Structured logging** enables debugging and analytics
- **Unique constraints** prevent duplicate event logs

**Event Types Stored**:
```json
{
  "events": [
    {
      "type": "tool_call",
      "tool": "filesystem.read_file", 
      "args": { "path": "/workspace/file.txt" },
      "timestamp": "2024-01-15T10:30:00Z"
    },
    {
      "type": "permission_request",
      "tool": "filesystem.write_file",
      "status": "approved",
      "timestamp": "2024-01-15T10:31:00Z"
    }
  ]
}
```

## Configuration Tables

### Agents Table

**Purpose**: Defines available AI agents with their configuration and capabilities.

```sql
CREATE TABLE agents (
    id SERIAL PRIMARY KEY,
    slug TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    backend TEXT NOT NULL DEFAULT 'anthropic',  -- Backend provider
    model TEXT NOT NULL DEFAULT '',             -- Specific model name
    mcp_servers TEXT NOT NULL DEFAULT '[]',     -- JSON array of MCP server slugs
    permission_policy TEXT NOT NULL DEFAULT '{}', -- JSON permission rules
    system_prompt TEXT NOT NULL,               -- Agent instructions
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Configuration Design**:
- **Slug-based identification** for URL-friendly agent references
- **JSON configuration fields** for flexible metadata storage
- **Backend abstraction** supports multiple LLM providers
- **Permission policies** define tool access rules per agent

**Example Agent Configuration**:
```json
{
  "slug": "cody",
  "name": "Cody", 
  "backend": "anthropic",
  "model": "claude-haiku-4-5-20251001",
  "mcp_servers": ["filesystem", "duckduckgo"],
  "permission_policy": {
    "filesystem.read_file": "auto_approve",
    "filesystem.write_file": "require_approval",
    "duckduckgo.*": "auto_approve"
  }
}
```

### MCP Servers Table

**Purpose**: Defines available Model Context Protocol servers for tool execution.

```sql
CREATE TABLE mcps (
    id SERIAL PRIMARY KEY,
    slug TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    command TEXT NOT NULL,                    -- Executable command
    args JSONB NOT NULL DEFAULT '[]'::jsonb, -- Command arguments
    is_enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**MCP Server Configuration**:
- **Command-based execution** with configurable arguments
- **JSONB args storage** for complex parameter passing
- **Enable/disable toggles** for server management
- **Slug-based references** from agent configurations

**Example MCP Server**:
```json
{
  "slug": "filesystem",
  "name": "File System Tools",
  "command": "npx",
  "args": ["@modelcontextprotocol/server-filesystem", "/workspace"],
  "is_enabled": true
}
```

## Authentication & Settings

### Auth Users Table

**Purpose**: User authentication with hashed passwords for web interface access.

```sql
CREATE TABLE auth_users (
    username TEXT NOT NULL PRIMARY KEY,
    password_hash TEXT NOT NULL,             -- bcrypt hashed
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Security Design**:
- **bcrypt password hashing** with configurable work factor
- **Username-based authentication** (no email requirement)
- **Timestamp tracking** for account management
- **Simple schema** suitable for personal use scenarios

### App Settings Table

**Purpose**: Dynamic application configuration stored in the database.

```sql
CREATE TABLE app_settings (
    key TEXT NOT NULL PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

**Configuration Keys**:
- `workspace_path` - Root directory for file operations
- `heartbeat_enabled` - Health monitoring toggle
- `heartbeat_interval_seconds` - Health check frequency
- `heartbeat_stuck_threshold_seconds` - Timeout detection
- `heartbeat_auto_cleanup_enabled` - Automatic cleanup toggle

### Integration Settings Table

**Purpose**: Configuration for external integrations (Telegram, etc.).

```sql
CREATE TABLE integration_settings (
    integration TEXT NOT NULL PRIMARY KEY,   -- 'telegram'
    is_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    bot_token TEXT NULL,                     -- Encrypted token storage
    allowed_user_ids JSONB NOT NULL DEFAULT '[]'::jsonb,
    allowed_usernames JSONB NOT NULL DEFAULT '[]'::jsonb,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

### Backend Settings Table

**Purpose**: LLM provider API configuration and credentials.

```sql
CREATE TABLE backend_settings (
    backend TEXT NOT NULL PRIMARY KEY,      -- 'anthropic' | 'openai' | etc.
    is_enabled BOOLEAN NOT NULL DEFAULT FALSE,
    api_key TEXT NULL,                      -- Encrypted API key storage
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

## Analytics Tables

### Token Usage Table

**Purpose**: Track LLM API usage for cost monitoring and analytics.

```sql
CREATE TABLE token_usage (
    id SERIAL PRIMARY KEY,
    provider TEXT NOT NULL,                 -- Backend provider name
    agent_slug TEXT NOT NULL,              -- Which agent used tokens
    usage_date DATE NOT NULL DEFAULT CURRENT_DATE,
    input_tokens BIGINT NOT NULL DEFAULT 0,
    output_tokens BIGINT NOT NULL DEFAULT 0, 
    total_tokens BIGINT NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_token_usage_provider_date ON token_usage (provider, usage_date);
CREATE INDEX idx_token_usage_agent_date ON token_usage (agent_slug, usage_date);
```

**Usage Analytics Design**:
- **Daily aggregation** for cost tracking and reporting
- **Provider-specific tracking** for multi-LLM usage analysis
- **Agent-level attribution** for usage optimization
- **Efficient indexing** for dashboard queries and reporting

## Schema Migration Strategy

### Automatic Migration

The `SessionStore` constructor includes migration logic for schema evolution:

```sql
-- Handle column renames for backward compatibility
ALTER TABLE agents ADD COLUMN IF NOT EXISTS description TEXT NOT NULL DEFAULT '';
ALTER TABLE agents ADD COLUMN IF NOT EXISTS model TEXT NOT NULL DEFAULT '';
ALTER TABLE agents ADD COLUMN IF NOT EXISTS is_enabled BOOLEAN NOT NULL DEFAULT TRUE;

-- Add new archiving capabilities
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS is_archived BOOLEAN NOT NULL DEFAULT FALSE;
ALTER TABLE sessions ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ NULL;
```

**Migration Philosophy**:
- **Additive changes only** - new columns added with sensible defaults
- **Backward compatibility** - existing data remains valid
- **Automatic execution** - no manual migration steps required
- **Idempotent operations** - safe to run multiple times

### Data Consistency

**Referential Integrity**:
- Foreign key constraints ensure data consistency
- Cascade rules prevent orphaned records
- Validation at application and database layers

**JSON Validation**:
- JSONB fields validated at application layer before storage
- Schema validation for complex configuration objects
- Default values prevent null reference exceptions

## Performance Considerations

### Indexing Strategy

**Primary Access Patterns**:
- Session lookup by ID (primary key)
- Message chronological ordering (auto-increment + timestamp)
- Token usage aggregation by date/provider (composite indexes)
- Agent lookup by slug (unique constraint)

**Query Optimization**:
- Connection pooling via `NpgsqlDataSource`
- Prepared statements for frequent queries
- JSONB indexing for configuration searches (future enhancement)

### Storage Efficiency

**Data Types**:
- `TEXT` for variable-length strings (efficient in PostgreSQL)
- `JSONB` for structured data with query capabilities
- `TIMESTAMPTZ` for timezone-aware timestamps
- `BIGINT` for token counters supporting large values

**Growth Management**:
- Archival strategy for old sessions and messages
- Token usage aggregation to prevent unbounded growth
- Event log rotation policies (configurable)

This database design provides a robust foundation for SharpClaw's conversation management while maintaining flexibility for future enhancements and integrations.