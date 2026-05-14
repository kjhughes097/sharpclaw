---
sidebar_position: 5
---

# Execution

The execution layer connects agents to LLM providers via a provider abstraction. `AgentRunner` is the orchestrator that resolves tools and MCP servers, then delegates to the appropriate `ILlmProvider`.

## Provider Architecture

SharpClaw supports multiple LLM providers through the `ILlmProvider` interface:

```csharp
public interface ILlmProvider
{
    string ProviderName { get; }
    Task<ILlmSession> CreateSessionAsync(LlmSessionRequest request, CancellationToken ct);
    Task<AgentRunResult> SendAsync(ILlmSession session, string prompt, CancellationToken ct);
}
```

Agents specify their provider via the `llm` frontmatter field. If omitted, `copilot` is used.

### CopilotProvider

Wraps the GitHub Copilot SDK (`CopilotClient`). Sessions are stateful — the SDK manages conversation history and tool calling automatically via `SendAndWaitAsync`.

- Auth: `UseLoggedInUser = true` (picks up the `gh` CLI OAuth token)
- MCP servers are passed as `McpServerConfig` dictionaries to the SDK
- Thread-safe lazy `CopilotClient` initialisation

### AnthropicProvider

Uses the official Anthropic C# SDK via `IChatClient` integration with `UseFunctionInvocation()` middleware.

- Sessions are stateless — conversation history is managed in-memory per session
- Tool calling loop is handled by the `IChatClient` function invocation middleware
- MCP servers are bridged via `McpToolBridge` (connects as MCP client, discovers tools, exposes as `AITool`)
- Conditionally registered — only available when `Anthropic:ApiKey` is configured

## AgentRunner

A singleton that resolves tools and MCP servers, then dispatches to the appropriate LLM provider:

```csharp
public sealed class AgentRunner(
    IToolRegistry toolRegistry,
    IMcpRegistry mcpRegistry,
    ISkillRegistry skillRegistry,
    IEnumerable<ILlmProvider> providers,
    SchedulingContextAccessor schedulingContextAccessor,
    ILogger<AgentRunner> logger)
```

### Key Methods

| Method               | Purpose                                                               |
| -------------------- | --------------------------------------------------------------------- |
| `CreateSessionAsync` | Resolves tools/MCPs, builds `LlmSessionRequest`, creates via provider |
| `SendAsync`          | Sends a prompt to an active session via the provider                  |
| `RunAsync`           | One-shot: creates session, sends prompt, returns result               |

### Provider Dispatch

`AgentRunner` resolves the provider by matching `request.Llm` to `ILlmProvider.ProviderName` (case-insensitive). If no provider matches, it throws `InvalidOperationException`.

### Tool Resolution

`ResolveTools()` maps agent tool names → `ITool` instances → `AIFunction` wrappers via `ToolAIFunctionAdapter`.

If an agent has no tool restrictions (empty list), all registered tools are provided.

### MCP Server Resolution

`ResolveMcpServers()` maps server names → `McpServerDefinition` records. The provider-specific conversion (e.g. to Copilot SDK `McpServerConfig`) happens inside each provider.

## McpToolBridge

For non-Copilot providers, `McpToolBridge` connects to MCP servers as a client using the `ModelContextProtocol` SDK:

- Supports both `stdio` and `http` transports
- Discovers tools via `ListToolsAsync()` — returned as `McpClientTool` (implements `AITool`)
- MCP connections are per-session and disposed when the session ends

## CopilotClient

- Auth: `UseLoggedInUser = true` (picks up the `gh` CLI OAuth token)
- Thread-safe lazy initialisation with `Lock`
- Singleton lifetime — one client per service instance

## SpawnAgentTool

A built-in tool that allows one agent to invoke another:

```csharp
public sealed class SpawnAgentTool(IAgentRegistry agentRegistry, AgentRunner runner) : ITool
```

The sub-agent runs in a fresh session and returns its response as the tool result.

## ToolAIFunctionAdapter

Wraps `ITool` as `AIFunction` for both the Copilot SDK and Anthropic `IChatClient`. Maps `ITool.Parameters` to the schema expected by `Microsoft.Extensions.AI`.
