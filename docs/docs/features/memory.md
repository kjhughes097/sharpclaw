---
sidebar_position: 10
---

# Memory

SharpClaw provides file-based persistent memory for agents, enabling them to store and recall information across conversations.

## MemoryService

```csharp
public sealed class MemoryService(IOptions<SharpClawOptions> options)
```

### Agent Memory

Each agent has a directory at `{WorkspacePath}/{agentName}/`:

| Method                             | Description                                      |
| ---------------------------------- | ------------------------------------------------ |
| `ReadFile(agent, file)`            | Reads a file from the agent's memory directory   |
| `WriteFile(agent, file, content)`  | Writes/overwrites a memory file                  |
| `AppendFile(agent, file, content)` | Appends to a memory file                         |
| `SearchMemory(agent, query)`       | Searches agent's memory files for matching lines |

### Knowledge Base

Shared across all agents at `{WorkspacePath}/knowledge/`:

| Method                           | Description                        |
| -------------------------------- | ---------------------------------- |
| `ReadKnowledge(file)`            | Reads from the knowledge directory |
| `WriteKnowledge(file, content)`  | Writes to the knowledge directory  |
| `AppendKnowledge(file, content)` | Appends to a knowledge file        |

### Path Safety

All file operations resolve paths relative to the workspace root. Attempts to traverse outside the workspace are blocked by path validation.

## MCP Exposure

Memory operations are exposed as MCP tools via `MemoryMcpTools`, allowing external clients to read/write agent memory:

```csharp
builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryMcpTools>();
```

This uses constructor injection (not `[FromServices]` — unsupported in ModelContextProtocol 1.2.0).

## Future Plans

- **MemorySnapshotJob**: Daily archival of memory state
- **LLM-based tagging**: Auto-generate tags for memory-index.md
- **SQLite session persistence**: Store conversation history durably
