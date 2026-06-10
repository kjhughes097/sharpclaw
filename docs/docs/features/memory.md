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

## Semantic Memory (Phase 1)

Semantic memory adds vector-based recall to the existing file-based system. When enabled, the `AgentInvoker` automatically retrieves relevant stored memories before each LLM call and prepends them as context.

### Architecture

| Component | Responsibility |
| --------- | -------------- |
| `EmbeddingService` | Generates 384-dimensional embeddings using a local ONNX model (all-MiniLM-L6-v2) |
| `SemanticMemoryStore` | SQLite-backed storage with FTS5 keyword search and brute-force cosine similarity (sqlite-vec optional) |
| `SemanticMemoryService` | Orchestrates store + recall + deduplication + trust scoring |
| `AgentInvoker` hook | Pre-LLM recall injection — recalled context is prepended to the user prompt |

### Configuration

Add to `appsettings.json`:

```json
{
  "SemanticMemory": {
    "Enabled": true,
    "ModelPath": "models/all-MiniLM-L6-v2.onnx",
    "TokenizerPath": "models/tokenizer.json",
    "DatabasePath": "data/semantic-memory.db",
    "TopK": 5,
    "MinScore": 0.3,
    "EmbeddingDimension": 384,
    "MaxContextTokens": 1500
  }
}
```

Set `Enabled: true` to activate. The ONNX model file must be present at `ModelPath` (relative to app base directory).

### Memory Types

Stored memories have a type: `Fact`, `Decision`, `Preference`, or `Learning`.

### Trust Scoring

- Each memory starts with a trust score of 1.0
- Recalled memories get a 5% boost (capped at 2.0)
- Weekly decay (×0.95) prunes unused memories below 0.1

### Deduplication

Before storing, cosine similarity >0.92 against existing memories triggers a skip (prevents redundant entries).

### Graceful Degradation

- If `Enabled: false` (default), no semantic memory services are registered
- If the ONNX model file is missing, startup fails with a clear error
- If sqlite-vec is unavailable, falls back to brute-force cosine similarity
- If recall fails at runtime, the prompt passes through unchanged
