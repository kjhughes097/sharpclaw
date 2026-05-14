---
sidebar_position: 9
---

# Workspace

The workspace is a file-system directory structure that provides persistent storage for agent memory, knowledge, and projects.

## WorkspaceInitialiser

Called on `ApplicationStarted` to create the directory structure:

```csharp
public sealed class WorkspaceInitialiser(
    IOptions<SharpClawOptions> options,
    IAgentRegistry agentRegistry,
    ILogger<WorkspaceInitialiser> logger)
```

### Directory Layout

```
{WorkspacePath}/
├── projects/          # Shared project workspace
├── knowledge/         # Shared knowledge files
├── {agent-name}/      # Per-agent memory directory
│   ├── memory.md      # Agent's persistent memory
│   └── audit.md       # Agent's audit log
└── ...
```

## Configuration

```json
{
  "SharpClaw": {
    "WorkspacePath": "/home/user/sharpclaw-workspace"
  }
}
```

If `WorkspacePath` is empty or not configured, workspace initialisation is skipped.

## Behaviour

- Creates the root directory and standard subdirectories
- Creates a directory for each registered agent
- Runs once at startup (idempotent — won't overwrite existing files)
- Logs a warning if path is not configured
