# Agent System

The SharpClaw agent system provides a flexible, secure, and extensible framework for running AI assistants. This document details the architecture, lifecycle, and key components of the agent execution engine.

## Agent Architecture

```
┌─────────────────┐
│   Agent Runner  │
│                 │
├─────────────────┤
│ • Agent Persona │
│ • MCP Clients   │
│ • Tool Schemas  │
│ • Backend       │
│ • Permissions   │
└─────────────────┘
         │
         ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│                 │    │                 │    │                 │
│ Permission Gate │◄──►│ Tool Dispatcher │◄──►│ MCP Servers     │
│                 │    │                 │    │                 │
└─────────────────┘    └─────────────────┘    └─────────────────┘
```

## Core Components

### AgentPersona
**Purpose**: Defines the agent's identity, capabilities, and configuration

```csharp
public sealed record AgentPersona(
    string Slug,                    // Unique identifier (e.g., "cody")
    string Name,                    // Display name
    string Description,             // Human-readable description
    string Backend,                 // LLM provider (anthropic, openai, etc.)
    string Model,                   // Specific model name
    IReadOnlyList<string> McpServers, // Assigned MCP tool servers
    IReadOnlyDictionary<string, ToolPermission> PermissionPolicy, // Tool permissions
    string SystemPrompt,            // Core behavior instructions
    bool IsEnabled                  // Whether agent is active
);
```

**Key Features**:
- **Immutable design** for thread safety
- **Type-safe configuration** prevents runtime errors
- **Flexible permission system** with granular control
- **Backend abstraction** allows provider switching

### AgentRunner
**Purpose**: Orchestrates the complete agent lifecycle and execution

**Responsibilities**:
1. **Initialization**:
   - Connect to configured MCP servers
   - Build unified tool schema
   - Create permission gate with agent policies
   - Initialize backend provider
   
2. **Execution**:
   - Stream conversation turns with real-time events
   - Coordinate tool execution with permission checking
   - Handle backend-specific conversation protocols
   
3. **Cleanup**:
   - Properly dispose MCP connections
   - Clean up backend resources

```csharp
public sealed class AgentRunner : IAsyncDisposable
{
    // Core lifecycle methods
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    public IAsyncEnumerable<AgentEvent> StreamAsync(...)
    public async ValueTask DisposeAsync()
}
```

## Agent Definition Format

Agents are defined using Markdown files with YAML front matter:

```markdown
---
name: Cody
description: Software architect and developer
backend: copilot
model: claude-opus-4.6
mcpServers:
  - filesystem
  - github
  - duckduckgo
permissionPolicy:
  filesystem.read_*: auto_approve
  filesystem.write_*: ask
  github.*: auto_approve
  duckduckgo.*: auto_approve
  "*": ask
isEnabled: true
---

You are Cody, a skilled software architect and developer.
Your expertise spans C#, TypeScript, and Python...
```

### Configuration Options

#### Backend Selection
- **anthropic**: Claude models via Anthropic API
- **openai**: GPT models via OpenAI API  
- **openrouter**: Multi-model access via OpenRouter
- **copilot**: GitHub Copilot SDK integration

#### Permission Policies
- **auto_approve**: Execute tool without confirmation
- **ask**: Request user permission before execution
- **deny**: Explicitly block tool usage

Pattern matching supports wildcards:
- `filesystem.read_*` - All filesystem read operations
- `github.*` - All GitHub operations
- `*` - Default fallback for unspecified tools

## Agent Lifecycle

### 1. Loading Phase
```csharp
// Load agent definition from markdown
var persona = await AgentPersona.LoadFromFileAsync("agents/cody.md");

// Create runner with MCP servers and backend factory
var runner = new AgentRunner(
    persona,
    mcpServers,
    backendFactory,
    workspacePath,
    logger
);
```

### 2. Initialization Phase
```csharp
// Connect MCP servers and prepare tools
await runner.InitializeAsync();

// Agent is now ready for conversations
var tools = runner.Tools; // Available tool schemas
```

### 3. Execution Phase
```csharp
// Stream conversation turn with real-time events
await foreach (var agentEvent in runner.StreamAsync(
    systemPrompt: persona.SystemPrompt,
    tools: runner.Tools,
    history: conversationHistory,
    toolDispatcher: toolDispatcher))
{
    switch (agentEvent.EventType)
    {
        case AgentEventType.ContentDelta:
            // Stream response tokens to user
            break;
        case AgentEventType.ToolCallStart:
            // Tool execution beginning
            break;
        case AgentEventType.PermissionRequest:
            // User approval needed
            break;
    }
}
```

### 4. Cleanup Phase
```csharp
// Automatic cleanup of resources
await runner.DisposeAsync();
```

## Tool Integration

### MCP Server Connection
Each agent can connect to multiple MCP (Model Context Protocol) servers:

```csharp
// MCP servers provide tools through protocol
var mcpServers = new[]
{
    new McpServerRecord("filesystem", "File system operations", "mcp-filesystem", []),
    new McpServerRecord("github", "GitHub integration", "mcp-github", ["--token", token])
};

// Tools automatically aggregated from all connected servers
var allTools = await AggregateToolsFromServers(mcpServers);
```

### Permission Gate
Tool execution is controlled by configurable permission policies:

```csharp
public sealed class PermissionGate
{
    // Synchronous permission checking
    public ToolPermissionResult CheckPermission(string toolName)
    
    // Asynchronous permission with user interaction
    public async Task<ToolPermissionResult> CheckPermissionAsync(string toolName)
}
```

Permission results:
- **Granted**: Execute immediately
- **Denied**: Block execution
- **PendingApproval**: Wait for user confirmation

### Tool Dispatcher
Coordinates tool execution with permission checking:

```csharp
Func<ToolCall, CancellationToken, Task<ToolCallResult>> toolDispatcher = 
    async (toolCall, ct) =>
    {
        // 1. Check permissions
        var permission = await permissionGate.CheckPermissionAsync(toolCall.Function);
        
        if (permission.IsGranted)
        {
            // 2. Find appropriate MCP client
            var client = FindMcpClientForTool(toolCall.Function);
            
            // 3. Execute tool via MCP protocol
            var result = await client.CallToolAsync(toolCall.Function, toolCall.Arguments, ct);
            
            return new ToolCallResult(result.Content);
        }
        
        return new ToolCallResult($"Permission denied for {toolCall.Function}");
    };
```

## Event Streaming

Agents communicate progress through real-time event streaming:

```csharp
public enum AgentEventType
{
    ContentDelta,           // Token being generated
    ToolCallStart,         // Tool execution beginning  
    ToolCallEnd,           // Tool execution completed
    PermissionRequest,     // User approval needed
    Error,                 // Error occurred
    Done                   // Turn completed
}
```

### Event Processing
```csharp
await foreach (var agentEvent in runner.StreamAsync(...))
{
    switch (agentEvent)
    {
        case { EventType: AgentEventType.ContentDelta, Content: var text }:
            await stream.WriteAsync(text); // Real-time text streaming
            break;
            
        case { EventType: AgentEventType.ToolCallStart, ToolCall: var call }:
            logger.LogInformation("Executing tool: {Tool}", call.Function);
            break;
            
        case { EventType: AgentEventType.PermissionRequest, PermissionRequest: var req }:
            var approved = await PromptUserAsync(req.ToolName);
            req.Respond(approved ? ToolPermissionResult.Granted : ToolPermissionResult.Denied);
            break;
    }
}
```

## Error Handling

### Graceful Degradation
- **MCP server failures**: Continue with available tools
- **Permission denials**: Return informative error messages
- **Backend errors**: Retry with exponential backoff
- **Tool execution failures**: Log and continue conversation

### Recovery Strategies
```csharp
try
{
    await runner.InitializeAsync();
}
catch (McpServerConnectionException ex)
{
    logger.LogWarning("MCP server {Server} unavailable: {Error}", ex.ServerName, ex.Message);
    // Continue with available servers
}
catch (BackendInitializationException ex)
{
    logger.LogError("Backend {Backend} failed to initialize: {Error}", ex.Backend, ex.Message);
    // Fall back to default backend or fail gracefully
}
```

## Performance Considerations

### Connection Pooling
- MCP clients reused across conversation turns
- Backend connections cached and pooled
- Database connections managed by SessionStore

### Memory Management
- Conversation history truncation for large sessions
- Streaming responses to minimize memory usage
- Proper disposal of resources via IAsyncDisposable

### Concurrency
- Multiple agents can run simultaneously
- Thread-safe session management
- Async/await throughout for non-blocking operations

## Security Model

### Sandboxed Execution
- MCP servers run in isolated processes
- File system access limited by tool permissions
- Network access controlled via MCP protocol

### Permission Enforcement
- Tool calls validated against agent permission policy
- User confirmation required for sensitive operations
- Audit trail for all tool executions

### Input Validation
- Tool arguments validated by MCP protocol
- System prompt injection protection
- Database queries parameterized for safety

## Agent Routing

SharpClaw includes a special routing agent (Ade) that can delegate to specialist agents:

```markdown
---
name: Ade
description: General assistant who routes to specialists when appropriate
backend: anthropic
model: claude-haiku-4-5-20251001
---

If another specialist agent is clearly a better fit for the task, 
hand the work off by returning a routing decision:

{ "agent": "cody", "rewritten_prompt": "Create a REST API for..." }
```

This enables:
- **Intelligent task routing** based on agent expertise
- **Seamless user experience** with automatic delegation
- **Specialized agent capabilities** without user knowledge of internals