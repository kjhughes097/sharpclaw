# SharpClaw

SharpClaw is a .NET 10 personal agent framework with multi-agent routing, Model Context Protocol (MCP) server support, and pluggable LLM backends (GitHub Copilot and Anthropic). It provides a unified registry-based architecture for agents, tools, MCP servers, and skills.

Inspired by [OpenClaw](https://github.com/openclaw/openclaw) and [GoClaw](https://github.com/nextlevelbuilder/goclaw).

![SharpClaw mascot](https://github.com/user-attachments/assets/0dd5321b-b058-4319-8904-c3b2a9bd9212)

## Overview

SharpClaw is built as two projects:

- **`SharpClaw/`** — .NET 10 Web SDK backend hosting all registries, execution engine, MCP server, Telegram integration, web chat API, scheduling, and HTTP endpoints.
- **`SharpClaw.Web/`** — Vite 9 + React 19 + TypeScript + MUI v9 frontend. Dev server on port 5173 proxies `/api` to the backend on port 5100. Production builds output to `SharpClaw/wwwroot/`.

Key capabilities:

- **Agent Registry** — named `IAgent` instances resolved at runtime from `.agent.md` files
- **Tool Registry** — named `ITool` implementations for agent capabilities
- **MCP Registry** — named MCP server definitions (Stdio or HTTP transports)
- **Skill Registry** — reusable prompt fragments injected into agent system prompts
- **AgentRunner** — unified execution engine dispatching to Copilot or Anthropic backends
- **Multi-agent routing** — agents can spawn sub-agents for task delegation
- **MCP bridging** — Anthropic provider connects to MCP servers as a client; Copilot SDK handles its own tool loop
- **Lazy MCP loading** — defer MCP connections until first use to save tokens
- **Telegram integration** — agents accessible via Telegram bot with bidirectional message fan-out
- **Web UI** — full management UI for agents, tools, MCPs, tasks, projects, and live chat
- **WebSocket chat** — real-time streaming responses via WebSocket
- **Scheduling** — cron-based task execution with result delivery to Telegram or Web
- **Service management** — start, monitor, and proxy external processes/Docker Compose services
- **Project & ticket tracking** — lightweight JSON-based project management
- **Auditing & transcripts** — full conversation history and audit trail

## Quick Start

```bash
dotnet build
dotnet run --project SharpClaw
```

The service listens on `http://localhost:5100` by default.

For the Web UI in development:

```bash
cd SharpClaw.Web && npm run dev   # Dev server with HMR on :5173
```

## Architecture

### Core Components

**`SharpClaw/`** — .NET 10 Web SDK application hosting:

- **IAgentRegistry** — resolves named `IAgent` implementations
- **IToolRegistry** — resolves named `ITool` implementations
- **IMcpRegistry** — resolves named MCP server definitions
- **ISkillRegistry** — resolves skill prompt fragments
- **AgentRunner** — unified execution engine that orchestrates agents, resolves tools/MCPs, and dispatches to LLM providers
- **RegistryWorker** — BackgroundService that populates all registries at startup from configuration and file scanning
- **CopilotProvider** — GitHub Copilot SDK backend (stateful sessions, SDK manages tool loop)
- **AnthropicProvider** — Anthropic C# SDK backend via `IChatClient` (stateless API, in-memory history, `UseFunctionInvocation()` handles tool loop, `McpToolBridge` connects MCP servers)

### Agents

Agents are defined as markdown files in `SharpClaw/agents/*.agent.md`. The filename (without extension) is the agent name. YAML frontmatter declares metadata:

```yaml
llm: copilot                   # 'copilot' (GitHub Copilot) or 'anthropic' (Anthropic)
model: claude-opus-4.6         # model passed to the provider
description: Short description # shown in UI and tools
tools: [spawn_agent]           # ITool names; omit for all tools
mcp_servers: [memory]          # IMcpRegistry names; omit for all servers
lazy_mcps: [playwright]        # MCP names to lazy-load for this agent (overrides global flag)
skills: [coding-standards]     # Skill names to inject into system prompt
sub_agents: [ade]              # IAgent names available via spawn_agent tool
telegram_chat_id: 123456789    # Telegram chat ID for this agent's conversations
```

The markdown body becomes the system prompt. No code changes required to add a new agent — just create a new `.agent.md` file.

### Tools

Tools are `ITool` implementations registered in DI. They appear in the agent's capability list and are resolved by name from `IToolRegistry`. Reference a tool in agent frontmatter or let agents access all registered tools by default.

### MCP Servers

MCP server definitions are stored in `SharpClaw/mcps/` as JSON:

```json
{
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@some/mcp-server"],
  "lazy": true
}
```

Reference by name in agent frontmatter `mcp_servers`. No code changes required to add a new MCP.

#### Lazy Loading

When `"lazy": true` is set in the MCP JSON config, the server is not connected at session start. Instead, a lightweight activation tool is provided that connects on first use. This saves input tokens for expensive MCP servers (e.g., Playwright adds ~5k tokens per request).

**Per-agent override:** Add `lazy_mcps` to agent frontmatter to control which MCPs are lazy for that specific agent. When specified, only those listed MCPs are lazy — all others load eagerly regardless of the global flag. When omitted, the global `lazy` flag on each MCP definition is used.

### Skills

Skills are prompt fragments stored in `SharpClaw/skills/`. They are injected into an agent's system prompt based on the `skills` list in agent frontmatter. Use to share common instructions across agents without duplication.

### Services

External services (separate processes) are defined in `SharpClaw/services/*.json`:

```json
{
  "name": "my-service",
  "project": "../path/to/project",
  "runtime": "dotnet",
  "port": 5150,
  "autoStart": false,
  "healthCheck": { "type": "http", "path": "/health" },
  "depends": ["my-db"]
}
```

The `ServiceRunner` BackgroundService manages lifecycle, health checks, dependency ordering (topological sort), and Docker Compose integration. Supports `dotnet`, `node`, and `docker-compose` runtimes.

## Web UI

The frontend (`SharpClaw.Web/`) provides:

- **Agent list** — view all agents with descriptions, edit in Monaco editor
- **Live chat** — WebSocket-based chat with any agent, markdown rendering
- **MCP browser** — view registered MCP servers and their available tools
- **Tool list** — view all registered tools
- **Task scheduler** — view and manage cron-scheduled tasks
- **Project & tickets** — manage projects and tickets through the UI
- **Configuration** — view runtime configuration

```bash
cd SharpClaw.Web && npm run dev    # Dev server with HMR on :5173
cd SharpClaw.Web && npm run build  # Production build → SharpClaw/wwwroot/
```

## Telegram Integration

SharpClaw integrates with Telegram as a chat interface. Agents can have a `telegram_chat_id` in their frontmatter to bind to a specific chat. Messages flow bidirectionally — Telegram messages invoke agents, and agent responses are sent back to Telegram.

A `ChannelFanOutService` broadcasts messages across all connected channels (Web, Telegram), so conversations are visible in both UIs.

Configuration requires a valid bot token in `Telegram:BotToken`. Registration is conditional — Telegram features are only active when a valid token is configured.

## Scheduling

Agents can create cron-scheduled tasks via the `schedule_task` tool. Tasks execute on schedule and deliver results via Telegram or Web depending on the originating channel.

```
schedule_task(prompt: "Check the weather", cron: "0 8 * * *")
```

Use the `.cron` command to list active schedules, or `.cancel <id>` to remove one.

## Commands

Interactive commands are available in chat (prefixed with `.`):

| Command | Description |
|---------|-------------|
| `.help` | Show available commands |
| `.new` | Start a fresh agent session |
| `.switch <agent>` | Switch to a different agent |
| `.restart` | Restart SharpClaw (with build check) |
| `.reload` | Reload all registries |
| `.cron` | List scheduled cron jobs |
| `.schedules` | List scheduled tasks |
| `.cancel <id>` | Cancel a scheduled task |
| `.lsa` | List agents |
| `.lst` | List tools |
| `.lsm` | List MCP servers |
| `.lsmt` | List MCP tools |
| `.lss` | List managed services |
| `.ticket` | Ticket management |
| `.ping` | Connectivity check |

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/agents` | List agents |
| GET | `/api/agents/activity` | Agent activity stats |
| POST | `/api/agents` | Create/update agent |
| POST | `/api/chat/{agentName}` | Send message (HTTP) |
| GET | `/api/chat/{agentName}` | Get chat history |
| WS | `/api/chat/ws/{agentName}` | WebSocket chat |
| GET | `/api/tools` | List tools |
| GET | `/api/tasks` | List scheduled tasks |
| GET/POST | `/api/projects` | Project CRUD |
| GET/POST | `/api/projects/{id}/tickets` | Ticket CRUD |
| GET | `/api/config` | Runtime configuration |

## Adding Components

### Adding a New Agent

1. Create `SharpClaw/agents/{name}.agent.md`
2. Add YAML frontmatter and system prompt
3. Restart the application (RegistryWorker scans on startup)

### Adding a New Tool

1. Implement `ITool` in `SharpClaw/Tools/`
2. Register in DI in `Program.cs`
3. Add to `ToolRegistry` in `RegistryWorker`
4. Reference by name in agent frontmatter or let agents access all by default

### Adding a New MCP Server

1. Create a JSON file in `SharpClaw/mcps/`
2. Reference by name in agent frontmatter `mcp_servers`
3. Restart (RegistryWorker scans on startup)

### Adding a New Service

1. Create a JSON file in `SharpClaw/services/`
2. Define runtime, port, health check, and optional dependencies
3. Restart (ServiceRunner picks up definitions on startup)

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
- Node.js 22+ (for Web UI)

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project SharpClaw
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
./sharpclaw.sh web      # Web UI dev server
./sharpclaw.sh web-build # Production build of Web UI
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

Full documentation is available in the [docs/](docs/) directory, built with Docusaurus:

```bash
cd docs && npm run build   # Verify docs build cleanly
cd docs && npm start        # Dev server with hot reload
```

## Key Design Patterns

- **Auth**: `UseLoggedInUser = true` in `CopilotClientOptions`. The Copilot SDK picks up the `gh` CLI OAuth token. PATs (`ghp_`) are rejected by the API.
- **MCP tool injection**: `MemoryMcpTools` uses constructor injection — `[FromServices]` does not exist in `ModelContextProtocol 1.2.0`. MCP tool classes must be non-static.
- **`null` MCP server list = all servers**: In `AgentRunner`, `McpServerNames = null` means give the agent every registered server, not none.
- **Anthropic registration conditional**: Only registered when a valid API key is configured in `Anthropic:ApiKey`.
- **Telegram registration conditional**: Only registered when a valid bot token is configured (not empty, not placeholder).
- **MCP bridging for Anthropic**: `McpToolBridge` connects to MCP servers as a client using `ModelContextProtocol` SDK, discovers tools via `ListToolsAsync()`, and exposes them as `AITool` instances. MCP connections are per-session and disposed when the session ends.
- **Channel fan-out**: Web and Telegram share the same `AgentSessionRegistry`. Messages are fanned out to all connected channels via `ChannelFanOutService`.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).

## Contributing

When making changes:

1. Follow the [coding conventions](#coding-conventions)
2. Update documentation in [docs/](docs/) for behavioral or interface changes
3. Ensure tests pass: `dotnet test`
4. Check that agents, tools, and MCP servers load correctly after your changes