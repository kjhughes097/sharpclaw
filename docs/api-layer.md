# API Layer

The SharpClaw API provides a comprehensive REST interface for managing agents, sessions, and system configuration. Built on ASP.NET Core, it follows RESTful principles and provides OpenAPI documentation for easy integration.

## API Architecture

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│                 │    │                 │    │                 │
│ HTTP Requests   │───►│ Controllers     │───►│ Services        │
│                 │    │                 │    │                 │
└─────────────────┘    └─────────────────┘    └─────────────────┘
                                │                        │
                                ▼                        ▼
                       ┌─────────────────┐    ┌─────────────────┐
                       │                 │    │                 │
                       │ DTOs & Models   │    │ SessionStore    │
                       │                 │    │                 │
                       └─────────────────┘    └─────────────────┘
```

## Controllers Overview

### AgentsController
**Route**: `/api/agents`, `/api/personas`
**Purpose**: Agent management and configuration

#### Key Endpoints

```http
GET /api/personas
# Returns enabled agents available for chat

GET /api/agents  
# Returns all agents with management details

POST /api/agents
# Create new agent from configuration

PUT /api/agents/{slug}
# Update existing agent configuration

DELETE /api/agents/{slug}
# Delete agent (with safety checks)

GET /api/backends/settings
# Get LLM provider configurations

PUT /api/backends/settings/{backend}
# Update provider settings and API keys
```

#### Agent Management Features
- **Safety checks** prevent deletion of agents with active sessions
- **Backend validation** ensures agents use enabled providers  
- **Permission policy validation** for tool access rules
- **Automatic model discovery** from enabled backends

### SessionsController
**Route**: `/api/sessions`
**Purpose**: Conversation management and execution

#### Key Endpoints

```http
GET /api/sessions
# List user's conversation sessions (includes archive status)

POST /api/sessions
# Create new session with specified agent

DELETE /api/sessions/{sessionId}
# Archive session (soft delete)

PUT /api/sessions/{sessionId}/archive
# Mark session as archived and generate knowledge summary

GET /api/sessions/{sessionId}/messages
# Get conversation history

POST /api/sessions/{sessionId}/messages
# Send message and stream response (SSE)

GET /api/sessions/{sessionId}/logs
# Get detailed execution logs for debugging

GET /api/sessions/{sessionId}/knowledge
# Get generated knowledge summary for archived session
```

#### Session Archiving & Knowledge Generation
Sessions can be archived to organize completed conversations. When archived:
- Session is marked with `is_archived = true` and `archived_at` timestamp
- Knowledge summary is automatically generated as Markdown file
- Summary includes conversation recap, extracted tags, and metadata  
- Files stored in workspace `knowledge/` folder with format: `YYYY-MM-DD-{shortId}-{title}.md`

#### Streaming Response Format
Messages endpoint supports Server-Sent Events for real-time streaming:

```typescript
// Event types streamed during agent execution
type AgentEvent = 
  | { type: "content", content: string }           // Response tokens
  | { type: "tool_call_start", toolCall: object }  // Tool execution begins
  | { type: "tool_call_end", result: object }      // Tool execution completes  
  | { type: "permission_request", request: object } // User approval needed
  | { type: "done" }                               // Turn completed
```

### AuthController
**Route**: `/api/auth`
**Purpose**: User authentication with JWT tokens

#### Authentication Flow

```http
POST /api/auth/login
Content-Type: application/json

{
  "username": "admin",
  "password": "password123"
}

# Response
{
  "token": "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9...",
  "username": "admin"
}
```

#### JWT Configuration
- **Algorithm**: HS256 with configurable secret
- **Expiration**: 7 days (configurable)
- **Claims**: Username and issued time
- **Validation**: Automatic via middleware

### McpsController  
**Route**: `/api/mcps`
**Purpose**: Model Context Protocol server management

#### MCP Management

```http
GET /api/mcps
# List all MCP servers (enabled and disabled)

POST /api/mcps
# Register new MCP server

PUT /api/mcps/{slug}
# Update MCP server configuration  

DELETE /api/mcps/{slug}
# Remove MCP server (with dependency checks)

POST /api/mcps/{slug}/test
# Test MCP server connectivity and tool discovery
```

### KnowledgeController
**Route**: `/api/knowledge`
**Purpose**: Knowledge base integration

```http
GET /api/knowledge
# Search knowledge base content

POST /api/knowledge/facts
# Add new facts to knowledge base

GET /api/knowledge/collections
# List available knowledge collections
```

### WorkspaceController
**Route**: `/api/workspace` 
**Purpose**: File system and workspace management

#### Workspace Operations
```http
GET /api/workspace/browse?path={path}
# Browse workspace directory contents with metadata
# Returns: files, folders, sizes, modification dates, permissions

GET /api/workspace/files/content?path={path}
# Read file contents securely (path validation enforced)

PUT /api/workspace/files/content
# Write file contents (with workspace boundary checks)

POST /api/workspace/git/clone
# Clone Git repository into workspace

GET /api/workspace/knowledge
# List generated knowledge files from archived sessions

GET /api/workspace/knowledge/{filename}
# Read specific knowledge file content
```

#### Security Features
- **Path Validation**: All file operations validate paths remain within workspace boundaries
- **Permission Checks**: File access respects underlying filesystem permissions
- **Safe Navigation**: Directory traversal attacks prevented through path normalization
- **Content Type Detection**: Proper MIME type handling for different file types

## Data Models (DTOs)

### Core DTOs

#### PersonaDto
```csharp
public sealed record PersonaDto(
    string Slug,
    string Name,
    string Description,
    string Backend,
    string Model
);
```

#### AgentDto  
```csharp
public sealed record AgentDto(
    string Slug,
    string Name,
    string Description,
    string Backend,
    string Model,
    List<string> McpServers,
    Dictionary<string, string> PermissionPolicy,
    bool IsEnabled,
    int SessionCount,
    DateTime CreatedAt
);
```

#### SessionDto
```csharp
public sealed record SessionDto(
    string SessionId,
    string AgentSlug,
    string AgentName,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    bool IsArchived
);
```

#### MessageDto
```csharp
public sealed record MessageDto(
    string Role,        // "user", "assistant", "system"
    string Content,
    DateTime CreatedAt
);
```

## Services Layer

### BackendSettingsService
**Purpose**: Manage LLM provider configurations

```csharp
public sealed class BackendSettingsService
{
    public List<BackendSettingsDto> ListSettings()
    public void UpdateSettings(string backend, UpdateBackendSettingsRequest request)
    public List<string> EnabledBackendNames()
    public bool IsBackendEnabled(string backend)
}
```

### BackendModelService  
**Purpose**: Discover available models from providers

```csharp
public sealed class BackendModelService
{
    public async Task<List<BackendModelDto>> GetModelsAsync(string backend)
    public async Task<List<BackendModelDto>> GetAllModelsAsync()
}
```

### SessionRuntimeService
**Purpose**: Orchestrate agent execution and conversation management

```csharp
public sealed class SessionRuntimeService
{
    public async Task<IAsyncEnumerable<AgentEvent>> StreamMessageAsync(
        string sessionId,
        string userMessage,
        CancellationToken cancellationToken = default)
        
    public async Task<AgentRunner?> GetOrCreateRunnerAsync(string sessionId)
}
```

### KnowledgeService
**Purpose**: Knowledge base integration and search

```csharp
public sealed class KnowledgeService  
{
    public async Task<List<KnowledgeResultDto>> SearchAsync(string query)
    public async Task AddFactAsync(string collection, string content)
}
```

## Middleware

### JwtAuthMiddleware
**Purpose**: JWT token validation and request authentication

```csharp
public sealed class JwtAuthMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // Extract JWT token from Authorization header
        // Validate token signature and expiration  
        // Set HttpContext.User for authorization
        // Call next middleware or return 401 Unauthorized
    }
}
```

**Protected Endpoints**: All endpoints except `/api/auth/login` and `/api/health`

## Error Handling

### Standardized Error Response
```csharp
public sealed record ErrorResponse(
    string Error,
    string? Details = null,
    Dictionary<string, string>? ValidationErrors = null
);
```

### Common HTTP Status Codes
- **200 OK**: Successful operation
- **400 Bad Request**: Validation errors or invalid input
- **401 Unauthorized**: Missing or invalid JWT token  
- **404 Not Found**: Resource doesn't exist
- **409 Conflict**: Operation conflicts with current state
- **500 Internal Server Error**: Unhandled server errors

### Validation Pipeline
```csharp
[HttpPost("agents")]
public async Task<IActionResult> CreateAgent([FromBody] CreateAgentRequest request)
{
    // 1. Model validation (automatic via attributes)
    if (!ModelState.IsValid)
        return BadRequest(new ErrorResponse("Validation failed", null, GetValidationErrors()));
        
    // 2. Business logic validation  
    if (!IsValidAgentSlug(request.Slug))
        return BadRequest(new ErrorResponse("Invalid agent slug"));
        
    // 3. Execute operation
    var agent = await store.CreateAgentAsync(request.ToAgentPersona());
    return Ok(ApiMapper.ToAgentDto(agent));
}
```

## OpenAPI Documentation

### Automatic Generation
- **Swagger/OpenAPI 3.0** specification generated automatically
- **Scalar UI** provides interactive API documentation  
- **Response type annotations** ensure accurate schemas

### Access Documentation
```bash
# Development
curl http://localhost:8080/openapi.json

# Interactive UI  
open http://localhost:8080/scalar/v1
```

## Security Features

### Authentication & Authorization
- **JWT-based authentication** for stateless operations
- **Middleware-based validation** on all protected endpoints
- **Configurable token expiration** and secret rotation

### Input Validation  
- **Model validation attributes** for automatic checks
- **SQL parameter sanitization** prevents injection attacks
- **File path validation** prevents directory traversal

### Tool Execution Security
- **Permission-gated tool calls** based on agent policies
- **MCP protocol sandboxing** for tool isolation  
- **Audit logging** for all tool executions

## Performance Considerations

### Database Optimization
- **Connection pooling** via NpgsqlDataSource
- **Parameterized queries** for prepared statement caching
- **Efficient pagination** for large result sets

### Streaming & Memory
- **Server-Sent Events** for real-time communication
- **Streaming JSON** for large responses
- **Memory-efficient file handling** for workspace operations

### Caching Strategy
- **Backend settings caching** to reduce database queries
- **Agent definition caching** for faster session startup
- **Model list caching** with TTL for provider APIs