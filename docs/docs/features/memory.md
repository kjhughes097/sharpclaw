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
| `EmbeddingService` | Generates 384-dimensional embeddings using a local ONNX model (all-MiniLM-L6-v2) with BertTokenizer (WordPiece) |
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
    "VocabPath": "models/vocab.txt",
    "DatabasePath": "data/semantic-memory.db",
    "TopK": 5,
    "MinScore": 0.3,
    "EmbeddingDimension": 384,
    "MaxContextTokens": 1500
  }
}
```

Set `Enabled: true` to activate. The ONNX model and `vocab.txt` files must be present (relative to app base directory). Run `scripts/download-embedding-model.sh` to fetch them.

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

## Semantic Memory (Phase 2) — Auto-Capture

Phase 2 adds automatic extraction of facts, decisions, preferences, and learnings from agent exchanges. After each successful LLM response, a background task uses a cheap model to analyse the exchange and store any noteworthy information as semantic memories.

### Architecture

| Component | Responsibility |
| --------- | -------------- |
| `MemoryExtractionService` | Calls a cheap LLM (Haiku) with the user prompt + agent response, parses extracted memories |
| `AgentInvoker` post-LLM hook | Fire-and-forget: triggers extraction after a successful response without blocking the user |

### How It Works

1. User sends a prompt → agent responds successfully
2. `AgentInvoker` fires a background task (non-blocking)
3. `MemoryExtractionService` sends the exchange to a cheap model with a structured extraction prompt
4. The model returns a JSON array of `{content, type}` objects
5. Each extracted memory is stored via `SemanticMemoryService.StoreAsync()` — which handles embedding generation and deduplication (>0.92 cosine = skip)

### Configuration

```json
{
  "SemanticMemory": {
    "Enabled": true,
    "ExtractionEnabled": true,
    "ExtractionModel": "claude-haiku-4-20250414",
    "ExtractionMaxTokens": 1024,
    "MinPromptLengthForExtraction": 20,
    "MinResponseLengthForExtraction": 50
  }
}
```

| Setting | Default | Description |
| ------- | ------- | ----------- |
| `ExtractionEnabled` | `true` | Toggle extraction on/off (within enabled semantic memory) |
| `ExtractionModel` | `claude-haiku-4-20250414` | Model used for extraction (should be cheap/fast) |
| `ExtractionMaxTokens` | `1024` | Max output tokens for extraction response |
| `MinPromptLengthForExtraction` | `20` | Skip extraction for very short prompts |
| `MinResponseLengthForExtraction` | `50` | Skip extraction for very short responses |

### Prerequisites

- `SemanticMemory:Enabled` must be `true`
- A valid `Anthropic:ApiKey` must be configured (extraction uses the Anthropic API)
- If either is missing, extraction is silently disabled

### Filtering

The extraction prompt instructs the LLM to:
- Only extract genuinely important, reusable information
- Skip transient details (timestamps, greetings, routine confirmations)
- Skip information only relevant to the immediate task
- Categorise each memory as fact/decision/preference/learning

### Graceful Degradation

- Extraction failures are logged as warnings and never affect the user response
- Fire-and-forget: the user response is returned immediately regardless of extraction status
- Short exchanges (below length thresholds) are skipped entirely

## Semantic Memory (Phase 3) — Maintenance & MCP Tools

Phase 3 adds trust decay, memory import, and explicit MCP tools for agents to interact with semantic memory directly.

### Trust Decay Worker

`MemoryDecayWorker` is a `BackgroundService` that runs every 7 days:

1. Applies a 0.95× decay multiplier to all memory trust scores
2. Prunes memories that fall below 0.1 (effectively forgotten)
3. Logs count of decayed and pruned memories

This ensures rarely-accessed memories gradually fade while frequently-recalled ones stay strong (boosted 5% on each recall, capped at 2.0).

### Memory Import

`MemoryImportService` imports existing file-based memory (`.md` files) into the semantic memory store:

- Splits content into paragraph-sized chunks (30–500 chars)
- Skips `audit.md` files (append-only logs, not factual memory)
- Deduplication via cosine similarity (>0.92 = skip)
- Available per-agent or for all agents at once

Trigger via the `semantic_memory_import` MCP tool or programmatically.

### MCP Tools

Four new MCP tools are exposed via `SemanticMemoryMcpTools`:

| Tool | Description |
| ---- | ----------- |
| `semantic_recall` | Search semantic memory by natural language query |
| `semantic_store` | Explicitly store a fact/decision/preference/learning |
| `semantic_memory_count` | Get count of stored memories (per-agent or total) |
| `semantic_memory_import` | Import .md files into semantic memory |

These tools are always registered but return helpful error messages when semantic memory is disabled.

### Full Configuration Reference

```json
{
  "SemanticMemory": {
    "Enabled": true,
    "ModelPath": "models/all-MiniLM-L6-v2.onnx",
    "VocabPath": "models/vocab.txt",
    "DatabasePath": "data/semantic-memory.db",
    "TopK": 5,
    "MinScore": 0.3,
    "EmbeddingDimension": 384,
    "MaxContextTokens": 1500,
    "ExtractionEnabled": true,
    "ExtractionModel": "claude-haiku-4-20250414",
    "ExtractionMaxTokens": 1024,
    "MinPromptLengthForExtraction": 20,
    "MinResponseLengthForExtraction": 50
  }
}
```
