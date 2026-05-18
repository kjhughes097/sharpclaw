---
sidebar_position: 2
---

# Agents

Agents are the core abstraction in SharpClaw. Each agent is an LLM persona defined by a markdown file with YAML frontmatter.

## File Format

Agents live in `src/SharpClaw/agents/` as `{name}.agent.md` files:

```markdown
---
llm: copilot
description: A helpful coding assistant
model: claude-sonnet-4
tools: [spawn_agent, skill_executor]
mcp_servers: [filesystem, memory]
skills: [coding-standards]
sub_agents: [deb]
---

You are Ade, a senior software engineer...
```

The filename (without `.agent.md`) becomes the agent name.

## Frontmatter Fields

| Field         | Type     | Description                                      |
| ------------- | -------- | ------------------------------------------------ |
| `llm`         | string   | LLM provider: `copilot` (default) or `anthropic` |
| `description` | string   | Human-readable agent description                 |
| `model`       | string   | LLM model identifier (e.g. `claude-sonnet-4`)    |
| `tools`       | string[] | Tool names to expose (omit = all tools)          |
| `mcp_servers` | string[] | MCP server names (omit = all servers)            |
| `skills`      | string[] | Skill names to inject into system prompt         |
| `sub_agents`  | string[] | Agent names available via `spawn_agent` tool     |

## Loading

`AgentLoader` scans the agents directory at startup. `AgentDefinitionParser` handles the YAML frontmatter parsing (supports both inline `[a, b]` and multi-line `- item` list formats).

Agents are registered in `IAgentRegistry` — a `ConcurrentDictionary` keyed by name (case-insensitive).

## Adding a New Agent

1. Create `src/SharpClaw/agents/{name}.agent.md`
2. Add YAML frontmatter with desired model/tools/MCPs
3. Write the system prompt as the markdown body
4. Restart the service (or send `.reload`)

No code changes required.

## Agent Interface

```csharp
public interface IAgent
{
    string Name { get; }
    string? Description { get; }
    string? Llm { get; }
    string? Model { get; }
    string? SystemPrompt { get; }
    IReadOnlyList<string> ToolNames { get; }
    IReadOnlyList<string> McpNames { get; }
    IReadOnlyList<string> SkillNames { get; }
    IReadOnlyList<string> SubAgentNames { get; }
}
```

## Current Agents

| Agent     | Model             | Role                          | Sub-agents            |
| --------- | ----------------- | ----------------------------- | --------------------- |
| **ade**   | claude-sonnet-4.5 | General assistant and router  | cody, fin, myles, deb |
| **cody**  | claude-opus-4.6   | Software engineering          | ade                   |
| **fin**   | claude-sonnet-4.5 | Finance and economics         | ade                   |
| **myles** | claude-sonnet-4.5 | Running and endurance sports  | ade                   |
| **deb**   | claude-sonnet-4.5 | Debate and critical reasoning | ade                   |

All current agents get the same workspace file tools (`workspace_read`, `workspace_write`) for working inside their own folder under `{WorkspacePath}/{agent-name}/`. Most agents use the base MCP servers (`memory`, `playwright`), while Ade also includes `anthropic_admin` for organization usage and spend interrogation.

### Memory Integration

Each agent's system prompt includes instructions for the memory MCP tools:

- `MemoryRead(agentName, file)` — read from the agent's private memory directory
- `MemoryWrite(agentName, file, content, mode)` — write to agent memory (`append` or `replace`)
- `MemorySearch(agentName, query)` — search agent memory
- `KnowledgeRead(file)` / `KnowledgeWrite(file, content, mode)` — shared knowledge

Agents maintain a `state.md` (working state, updated freely) and `notes.md` (persistent, requires user confirmation) in their workspace directory. Shared facts live in `knowledge/`.

Uploaded files are saved under `{WorkspacePath}/{agent-name}/uploads/`. Any agent can use `workspace_read` / `workspace_write` to inspect or maintain files in its own workspace folder, and Fin is explicitly prompted to use them for stock-tracking CSV and spreadsheet workflows.
