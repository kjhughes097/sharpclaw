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
    TranscriptService transcriptService,
    ILogger<AgentInvoker> logger)
```

### Flow

1. **Command check** — `CommandRouter.TryExecuteAsync()` handles dot-prefixed commands
2. **Transcript request log** — writes a JSONL request entry via `TranscriptService`
3. **Agent resolution** — looks up the session's current agent in the registry
4. **Audit** — logs the request via `AuditService`
5. **Skill injection** — builds system prompt with skill content appended
6. **Session management** — creates a Copilot session on first message, reuses thereafter
7. **Execution** — sends prompt via `AgentRunner.SendAsync()`
8. **Transcript response log** — writes a JSONL response entry via `TranscriptService`
9. **Publish** — writes the response to the session's message bus

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
