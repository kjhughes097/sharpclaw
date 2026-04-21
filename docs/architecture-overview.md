# Architecture Overview

SharpClaw is built as a modular, multi-component system designed for personal AI agent management with enterprise-level security and extensibility.

## System Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   React Web UI │    │  .NET 10 Web API │    │   PostgreSQL    │
│                 │◄──►│                  │◄──►│    Database     │
│  - Chat UI      │    │  - Agent Runtime │    │  - Sessions     │
│  - File Browser │    │  - MCP Manager   │    │  - Messages     │
│  - Settings     │    │  - Streaming     │    │  - Agents       │
└─────────────────┘    └──────────────────┘    └─────────────────┘
                                │
                                ▼
                       ┌──────────────────┐
                       │  Backend Providers│
                       │  - Anthropic     │
                       │  - OpenAI        │
                       │  - OpenRouter    │
                       │  - GitHub Copilot│
                       └──────────────────┘
                                │
                                ▼
                       ┌──────────────────┐
                       │  MCP Servers     │
                       │  - File Tools    │
                       │  - Web Search    │
                       │  - Custom Tools  │
                       └──────────────────┘
```

## Core Components

### 1. Web API (`SharpClaw.Api`)

**Purpose**: Central coordination hub for all agent operations and client requests.

**Key Services**:
- `SessionRuntimeService` - Manages active agent conversations and streaming
- `BackendRegistry` - Coordinates multiple LLM providers
- `KnowledgeService` - Handles session archiving and knowledge extraction
- `AuthService` - JWT-based authentication and user management

**Architecture Patterns**:
- **Dependency Injection**: All services registered as singletons for performance
- **Streaming Architecture**: Server-Sent Events for real-time message delivery
- **Stateless Design**: Session state persisted in database, not memory

### 2. Core Framework (`SharpClaw.Core`)

**Purpose**: Shared business logic and abstractions for agent execution.

**Key Components**:
- `AgentRunner` - Orchestrates agent execution with MCP tool integration
- `SessionStore` - PostgreSQL data access layer with schema management
- `IAgentBackend` - Abstraction for LLM provider integration
- `PermissionGate` - Security layer for tool execution approval

**Design Decisions**:
- **Provider Pattern**: Unified interface across different LLM backends
- **Permission-Based Security**: Tool execution requires explicit approval policies
- **Database-First**: PostgreSQL schema automatically created and migrated

### 3. Backend Providers

**Supported LLMs**:
- **`SharpClaw.Anthropic`** - Claude models via Anthropic API
- **`SharpClaw.OpenAI`** - GPT models via OpenAI API  
- **`SharpClaw.OpenRouter`** - Multiple models via OpenRouter proxy
- **`SharpClaw.Copilot`** - GitHub Copilot integration

**Common Interface**:
```csharp
public interface IAgentBackend
{
    Task<AgentResponse> ProcessAsync(AgentRequest request, CancellationToken ct);
    IAsyncEnumerable<AgentEvent> ProcessStreamingAsync(AgentRequest request, CancellationToken ct);
}
```

### 4. Agent System

**Agent Definition**: Agents are defined in markdown files with YAML frontmatter:

```yaml
---
name: Cody
description: Senior software architect and full-stack developer
backend: anthropic
model: claude-haiku-4-5-20251001
mcpServers:
  - filesystem
  - duckduckgo
permissionPolicy:
  filesystem.read_file: auto_approve
  duckduckgo.*: auto_approve
isEnabled: true
---
# Agent system prompt content...
```

**Agent Routing**: The `ade` agent acts as a dispatcher, routing requests to specialist agents based on the task requirements.

### 5. MCP Integration

**Model Context Protocol**: Standardized interface for tool execution with security controls.

**MCP Server Management**:
- Dynamic server registration and lifecycle management
- Permission policies control tool access per agent
- Sandboxed execution environment for security

**Tool Categories**:
- **File System**: Read/write workspace files with path validation
- **Web Search**: DuckDuckGo integration for information retrieval
- **Custom Tools**: Extensible framework for domain-specific tools

## Data Architecture

### Session Lifecycle

1. **Creation**: User starts conversation → new session record created
2. **Execution**: Messages exchanged → stored in messages table
3. **Tool Usage**: MCP tools executed → events logged in session_event_logs
4. **Archiving**: Session completed → archived with knowledge summary generated

### Knowledge Management

**Session Archiving**:
- Completed sessions automatically archived with `is_archived = true`
- Knowledge summaries generated and stored in workspace `knowledge/` folder
- Markdown format for searchability and version control integration

**Workspace Integration**:
- Secure file browser with path traversal protection
- Agent file access restricted to workspace directory tree
- File operations logged and tracked for audit purposes

## Security Architecture

### Authentication & Authorization

**JWT-Based Auth**:
- Stateless authentication using signed JSON Web Tokens
- Configurable token expiration and refresh policies
- User management with password hashing (bcrypt)

**Permission System**:
```csharp
public enum ToolPermission
{
    Deny,           // Block tool execution
    AutoApprove,    // Execute without prompt
    RequireApproval // Prompt user for approval
}
```

### Tool Security

**MCP Sandboxing**:
- Each MCP server runs in isolated process
- File system access restricted to workspace directory
- Network access controlled per tool and agent

**Path Validation**:
- All file operations validated against workspace root
- Path traversal attacks prevented with canonical path resolution
- Access logging for audit and debugging

## Performance Architecture

### Database Optimization

**Connection Management**:
- `NpgsqlDataSource` connection pooling for efficient resource usage
- Prepared statements for common queries
- Database schema migrations handled automatically

**Query Patterns**:
- Efficient session listing with agent metadata joins
- Event log storage using JSONB for flexible schema
- Indexed token usage tracking for analytics

### Streaming Performance

**Real-Time Communication**:
- Server-Sent Events for low-latency message streaming  
- Chunked response processing for large model outputs
- Connection management for handling multiple concurrent sessions

## Configuration Architecture

### Environment-Based Config

**Docker Compose Integration**:
```yaml
SHARPCLAW_DB_CONNECTION: "Host=db;Database=sharpclaw;..."
ANTHROPIC_API_KEY: "sk-..."
OPENAI_API_KEY: "sk-..."
```

**Flexible Deployment**:
- Environment variable override support
- Docker secrets integration for production
- Development vs production configuration profiles

### Runtime Configuration

**Dynamic Settings**: Backend API keys, integration settings, and workspace paths configurable via API without restart.

**Database-Stored Config**: App settings, integration toggles, and user preferences persisted in PostgreSQL.

## Extension Points

### Adding New Backends

1. Implement `IAgentBackendProvider` interface
2. Register in dependency injection container
3. Add configuration UI in React frontend
4. Backend automatically available to all agents

### Custom MCP Servers

1. Implement MCP protocol specification
2. Register server definition in database
3. Configure agent permission policies
4. Tools immediately available to authorized agents

### New Agent Types

1. Create markdown definition in `agents/` folder
2. Define MCP servers and permission policies
3. Agent automatically loaded and available
4. Routing logic in `ade` agent updated as needed

This architecture provides a solid foundation for personal AI agent management while maintaining the flexibility to extend and customize for specific use cases.