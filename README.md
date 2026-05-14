# SharpClaw

SharpClaw is a .NET 10 personal agent framework with multi-agent routing, Model Context Protocol (MCP) server support, and pluggable LLM backends (GitHub Copilot and Anthropic). It provides a unified registry-based architecture for agents, tools, MCP servers, and skills.

Inspired by [OpenClaw](https://github.com/openclaw/openclaw) and [GoClaw](https://github.com/nextlevelbuilder/goclaw).

![SharpClaw mascot](https://github.com/user-attachments/assets/0dd5321b-b058-4319-8904-c3b2a9bd9212)

## Overview

SharpClaw is built as a single .NET 10 Web SDK application that orchestrates agents, tools, and external services:

- **Agent Registry** — named `IAgent` instances resolved at runtime
- **Tool Registry** — named `ITool` implementations for agent capabilities
- **MCP Registry** — named MCP server definitions (Stdio or HTTP transports)
- **Skill Registry** — reusable prompt fragments injected into agent system prompts
- **AgentRunner** — unified execution engine dispatching to Copilot or Anthropic backends
- **Multi-agent routing** — agents can spawn sub-agents for task delegation
- **MCP bridging** — Anthropic provider connects to MCP servers as a client; Copilot SDK handles its own tool loop
- **No UI** — command-line and HTTP/REST interface only; designed for headless agent execution and orchestration

## Quick Start

```bash
dotnet build
dotnet run --project src/SharpClaw
```

The service listens on `http://localhost:5100` by default.

## Architecture

### Core Components

**`src/SharpClaw`** — A single .NET 10 Web SDK application hosting:

- **IAgentRegistry** — resolves named `IAgent` implementations
- **IToolRegistry** — resolves named `ITool` implementations  
- **IMcpRegistry** — resolves named MCP server definitions
- **ISkillRegistry** — resolves skill prompt fragments
- **AgentRunner** — unified execution engine that orchestrates agents, resolves tools/MCPs, and dispatches to LLM providers
- **RegistryWorker** — BackgroundService that populates all registries at startup from configuration and file scanning
- **CopilotProvider** — GitHub Copilot SDK backend (stateful sessions, SDK manages tool loop)
- **AnthropicProvider** — Anthropic C# SDK backend via `IChatClient` (stateless API, in-memory history, `UseFunctionInvocation()` handles tool loop, `McpToolBridge` connects MCP servers)

### Agents

Agents are defined as markdown files in `src/SharpClaw/agents/*.agent.md`. The filename (without extension) is the agent name. YAML frontmatter declares metadata:

```yaml
llm: copilot                   # 'copilot' (GitHub Copilot) or 'anthropic' (Anthropic)
model: claude-opus-4.6         # model passed to the provider
tools: [spawn_agent]           # ITool names; omit for all tools
mcp_servers: [memory]          # IMcpRegistry names; omit for all servers
skills: [coding-standards]     # Skill names to inject into system prompt
sub_agents: [ade]              # IAgent names available via spawn_agent tool
```

The markdown body becomes the system prompt. No code changes required to add a new agent — just create a new `.agent.md` file.

### Tools

Tools are `ITool` implementations registered in DI. They appear in the agent's capability list and are resolved by name from `IToolRegistry`. Reference a tool in agent frontmatter or let agents access all registered tools by default.

### MCP Servers

MCP server definitions are stored in `src/SharpClaw/mcps/` as JSON:

```json
{
  "name": "myserver",
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@some/mcp-server"]
}
```

Reference by name in agent frontmatter `mcp_servers`. No code changes required to add a new MCP.

### Skills

Skills are prompt fragments stored in `src/SharpClaw/skills/`. They are injected into an agent's system prompt based on the `skills` list in agent frontmatter. Use to share common instructions across agents without duplication.

## Adding Components

### Adding a New Agent

1. Create `src/SharpClaw/agents/{name}.agent.md`
2. Add YAML frontmatter and system prompt
3. Restart the application (RegistryWorker scans on startup)

### Adding a New Tool

1. Implement `ITool` in `src/SharpClaw/Tools/`
2. Register in DI in `Program.cs`
3. Add to `ToolRegistry` in `RegistryWorker`
4. Reference by name in agent frontmatter or let agents access all by default

### Adding a New MCP Server

1. Create a JSON file in `src/SharpClaw/mcps/`
2. Reference by name in agent frontmatter `mcp_servers`
3. Restart (RegistryWorker scans on startup)

## Coding Conventions

- **Primary constructors** for classes that don't need field-level validation
- **`sealed`** on all concrete service/tool/agent classes
- **`record`** for immutable data models; `record struct` for hot-path value types
- Nullable enabled everywhere — no `!` suppression without a comment
- `IReadOnlyList<T>` for exposed collections; `List<T>` only internally
- No `async` methods that don't `await` — use `Task.FromResult` or `Task.CompletedTask`

## Building & Running

### Prerequisites

- .NET 10 SDK

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project src/SharpClaw
```

The service listens on `http://localhost:5100` with `ASPNETCORE_ENVIRONMENT=Development` and `DOTNET_ENVIRONMENT=Development` from launch settings.

### Test

```bash
dotnet test
```

### Local Control Script

Use the root script to run common workflows from one command:

```bash
./sharpclaw.sh start    # SharpClaw + Grafana stack
./sharpclaw.sh stop     # SharpClaw + Grafana stack
./sharpclaw.sh restart  # SharpClaw + Grafana stack
./sharpclaw.sh status   # service + stack status
./sharpclaw.sh logs     # open Grafana logs UI filtered to service_name="SharpClaw"
./sharpclaw.sh logs service  # SharpClaw local process log (optional)
./sharpclaw.sh test     # dotnet test
./sharpclaw.sh docs     # docs dev server on http://localhost:3001
```

## LLM Providers

### Copilot Provider

- Wraps the GitHub Copilot SDK
- Stateful sessions managed by the SDK
- The SDK handles the tool loop internally
- Requires GitHub CLI login (`gh auth login`)
- Auth method: uses OAuth token from `gh` CLI; PATs (`ghp_`) are rejected

### Anthropic Provider

- Wraps the Anthropic C# SDK via `IChatClient`
- Stateless API calls with in-memory history managed by `AgentRunner`
- `UseFunctionInvocation()` handles the tool loop
- MCP servers are connected as clients via `McpToolBridge`
- Discovers MCP tools via `ListToolsAsync()` and exposes them as `AITool` instances
- MCP connections are per-session and disposed when the session ends
- Requires `Anthropic:ApiKey` in configuration (conditional registration when key is set)

## Documentation

Documentation for users and developers is available in the [docs/](docs/) directory and built with Docusaurus. Updates to behavior, configuration, or public interfaces should have corresponding documentation updates.

```bash
cd docs && npm run build   # Verify docs build cleanly
cd docs && npm start        # Dev server with hot reload
```

## Key Design Patterns

SharpClaw uses the following settled patterns:

- **Auth**: `UseLoggedInUser = true` in `CopilotClientOptions`. The Copilot SDK picks up the `gh` CLI OAuth token. PATs (`ghp_`) are rejected by the API.
- **MCP tool injection**: `MemoryMcpTools` uses constructor injection — `[FromServices]` does not exist in `ModelContextProtocol 1.2.0`. MCP tool classes must be non-static.
- **`null` MCP server list = all servers**: In `AgentRunner`, `McpServerNames = null` means give the agent every registered server, not none.
- **Anthropic registration conditional**: Only registered when a valid API key is configured in `Anthropic:ApiKey`.
- **MCP bridging for Anthropic**: `McpToolBridge` connects to MCP servers as a client using `ModelContextProtocol` SDK, discovers tools via `ListToolsAsync()`, and exposes them as `AITool` instances. MCP connections are per-session and disposed when the session ends.

## License

This project is licensed under the MIT License. See [LICENSE](/home/khughes/projects/sharpclaw/LICENSE).

## Known Limitations

- Copilot model selection in agent definitions is not yet enforced at runtime
- MCP process arguments must remain string-only (passed directly to stdio transports)

## Contributing

When making changes:

1. Follow the [coding conventions](#coding-conventions)
2. Update documentation in [docs/](docs/) for behavioral or interface changes
3. Ensure tests pass: `dotnet test`
4. Check that agents, tools, and MCP servers load correctly after your changes