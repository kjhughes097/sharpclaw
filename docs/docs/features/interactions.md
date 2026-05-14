---
sidebar_position: 7
---

# Interactions

The interactions layer orchestrates the flow from user input to agent response. `AgentInvoker` is the central coordinator.

## AgentInvoker

```csharp
public sealed class AgentInvoker(
    IAgentRegistry agentRegistry,
    ISkillRegistry skillRegistry,
    AgentRunner runner,
    CommandRouter commandRouter,
    AuditService auditService,
    ILogger<AgentInvoker> logger)
```

### Flow

1. **Command check** — `CommandRouter.TryExecuteAsync()` handles dot-prefixed commands
2. **Agent resolution** — looks up the session's current agent in the registry
3. **Audit** — logs the request via `AuditService`
4. **Skill injection** — builds system prompt with skill content appended
5. **Session management** — creates a Copilot session on first message, reuses thereafter
6. **Execution** — sends prompt via `AgentRunner.SendAsync()`
7. **Publish** — writes the response to the session's message bus

### Return Value

```csharp
public async Task<(string? SwitchedTo, string? ResponseText)> InvokeAsync(
    AgentSession session, string prompt, CancellationToken ct)
```

Returns the switched-to agent name (if a `.switch` command was used) and the response text.

## Message Publishing

Both inbound and outbound messages are published to the session's `Channel<AgentMessage>` for real-time streaming to connected clients.

## Error Handling

If the agent isn't found in the registry, a bracketed error message is returned. If the LLM call fails, the error is wrapped in `AgentRunResult.Fail()`.
