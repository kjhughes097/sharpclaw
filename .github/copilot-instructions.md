# SharpClaw — Copilot Instructions

## Architecture

Two projects at the repo root:
- **`SharpClaw/`** — .NET 10 Web SDK app hosting all registries, execution engine, MCP server, Telegram integration, web chat API, and HTTP endpoints.
- **`SharpClaw.Web/`** — Vite 9 + React 19 + TypeScript + MUI v9 frontend. Dev server on port 5173 proxies `/api` to the backend on port 5100. Production builds output to `SharpClaw/wwwroot/`.

Key registries (all singleton):
- `IAgentRegistry` — named `IAgent` instances
- `IToolRegistry` — named `ITool` instances
- `IMcpRegistry` — named `McpServerDefinition` entries (Stdio or Http)
- `ISkillRegistry` — named skill prompt fragments

`AgentRunner` is the execution engine. It resolves tools and MCP servers from the registries, builds a `LlmSessionRequest`, and dispatches to the appropriate `ILlmProvider` based on the agent's `llm` field. It is shared across all agents.

Two providers are available:
- `CopilotProvider` — wraps the GitHub Copilot SDK (stateful sessions, SDK manages tool loop)
- `AnthropicProvider` — wraps the Anthropic C# SDK via `IChatClient` (stateless API, in-memory history, `UseFunctionInvocation()` handles tool loop, `McpToolBridge` bridges MCP servers)

`RegistryWorker` (BackgroundService) populates all registries at startup from config and file scanning.

## Agent Loading

Agents are defined in `SharpClaw/agents/*.agent.md` files. The filename (without extension) is the agent name. YAML frontmatter declares:

```yaml
llm: copilot                   # LLM provider: 'copilot' (default) or 'anthropic'
model: claude-opus-4.6       # model passed to the provider
tools: [spawn_agent]          # ITool names; omit for all tools
mcp_servers: [memory]         # IMcpRegistry names; omit for all servers
skills: [coding-standards]    # Skill names to inject into system prompt
sub_agents: [ade]             # IAgent names available via spawn_agent tool
```

The body is the system prompt.

To add a new agent: create `agents/{name}.agent.md`. No code changes needed.

## Adding Tools

Implement `ITool` in `SharpClaw/Tools/`, register in DI in `Program.cs`, and add to the tool registry. Reference the tool name in agent frontmatter.

## Adding MCP Servers (External)

Add a JSON file to `SharpClaw/mcps/`:

```json
{
  "name": "myserver",
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@some/mcp-server"]
}
```

Reference by name in agent frontmatter `mcp_servers`. No code changes needed.

## Coding Conventions

- **Primary constructors** for classes that don't need field-level validation
- **`sealed`** on all concrete service/tool/agent classes
- **`record`** for immutable data models; `record struct` for hot-path value types
- Nullable enabled everywhere — no `!` suppression without a comment
- `IReadOnlyList<T>` for exposed collections; `List<T>` only internally
- No `async` methods that don't `await` — use `Task.FromResult` or `Task.CompletedTask`

## Build & Test

```bash
dotnet build
dotnet test
dotnet run --project SharpClaw
```

Launch settings set `ASPNETCORE_ENVIRONMENT=Development` and `DOTNET_ENVIRONMENT=Development`. Service listens on `http://localhost:5100`.

### Web UI

```bash
cd SharpClaw.Web && npm run dev    # Dev server with HMR on :5173
cd SharpClaw.Web && npm run build  # Production build → SharpClaw/wwwroot/
```

Or via the helper script:

```bash
sc web       # Dev server
sc web-build # Production build
```

## MCP Troubleshooting (Logging + Loki)

When an agent reports an MCP server is "not available", use structured logs first. Add or verify `Information`-level logs at these checkpoints:

- `RegistryWorker.Reload()`
  - MCP registration events (`Registered MCP server {Name} (transport={Transport})`)
- `AgentInvoker.InvokeAsync()`
  - Chosen agent and declared MCP/tool counts for the conversation session
- `AgentRunner.CreateSessionAsync()` / `ResolveMcpServers()`
  - Requested MCP names vs resolved MCP names from `IMcpRegistry`
- `CopilotProvider.CreateSessionAsync()`
  - Final MCP set and transport details passed to Copilot SDK (`McpServers`)
  - Session creation failures with MCP context

If these logs show the MCP is requested and passed through, the issue is often prompt/tool-name mismatch rather than registry wiring.

### Loki Query Workflow

Use Loki directly (default in local stack is `http://127.0.0.1:3100`) and filter by `service_name="SharpClaw"`.

```bash
start_ns=$(date -u -d '2 hours ago' +%s%N)
end_ns=$(date -u +%s%N)

# MCP registration and resolution
curl -sG 'http://127.0.0.1:3100/loki/api/v1/query_range' \
  --data-urlencode 'query={service_name="SharpClaw"} |= "Registered MCP server" or {service_name="SharpClaw"} |= "Resolved MCP servers"' \
  --data-urlencode "start=$start_ns" \
  --data-urlencode "end=$end_ns" \
  --data-urlencode 'limit=300'

# Copilot session MCP payload
curl -sG 'http://127.0.0.1:3100/loki/api/v1/query_range' \
  --data-urlencode 'query={service_name="SharpClaw"} |= "Copilot session setup" or {service_name="SharpClaw"} |= "MCP "' \
  --data-urlencode "start=$start_ns" \
  --data-urlencode "end=$end_ns" \
  --data-urlencode 'limit=300'

# Errors around session/send path
curl -sG 'http://127.0.0.1:3100/loki/api/v1/query_range' \
  --data-urlencode 'query={service_name="SharpClaw"} |= "Copilot session creation failed" or {service_name="SharpClaw"} |= "Copilot send failed" or {service_name="SharpClaw"} |= "Agent error:"' \
  --data-urlencode "start=$start_ns" \
  --data-urlencode "end=$end_ns" \
  --data-urlencode 'limit=300'
```

### Fast Triage Order

1. Confirm MCP server loaded and registered.
2. Confirm agent requested MCP in frontmatter.
3. Confirm `AgentRunner` resolved it from registry.
4. Confirm `CopilotProvider` passed it into session config.
5. If 1-4 pass, inspect tool naming in agent prompts (MCP tool names are typically snake_case).

## Documentation (Docusaurus)

The documentation site lives in `docs/` and is built with Docusaurus (TypeScript, classic preset).

**MANDATORY: Every code change that affects behaviour, configuration, or public interfaces MUST include documentation updates in the same task. Do not consider work complete until docs are updated and `cd docs && npm run build` passes.** This includes:

- New or changed features → update or create a page in `docs/docs/features/`
- New agents, tools, or MCP servers → update relevant feature page
- Configuration changes → update `docs/docs/features/scaffolding.md`
- Architecture changes → update `docs/docs/intro.md`
- New API endpoints → update `docs/docs/features/web-ui.md`
- New sidebar entries → add to `docs/sidebars.ts`

Documentation pages use standard Markdown with `sidebar_position` frontmatter for ordering.

```bash
cd docs && npm run build   # Verify docs build cleanly
cd docs && npm start        # Dev server with hot reload
```

## Settled Patterns — Don't Change Without Reason

- **Auth**: `UseLoggedInUser = true` in `CopilotClientOptions`. The Copilot SDK picks up the `gh` CLI OAuth token. PATs (`ghp_`) are rejected by the API.
- **MCP tool injection**: `MemoryMcpTools` uses constructor injection — `[FromServices]` does not exist in `ModelContextProtocol 1.2.0`. MCP tool classes must be non-static.
- **Telegram `MessageOrigin` clash**: `using ChannelMessageOrigin = SharpClaw.Models.MessageOrigin` alias in `TelegramService.cs`.
- **`null` MCP server list = all servers**: In `AgentRunner`, `McpServerNames = null` means give the agent every registered server, not none.
- **Telegram registration conditional**: Only registered when a valid bot token is configured (not empty, not placeholder).
- **Anthropic registration conditional**: Only registered when a valid API key is configured in `Anthropic:ApiKey`.
- **MCP bridging for Anthropic**: `McpToolBridge` connects to MCP servers as a client using `ModelContextProtocol` SDK, discovers tools via `ListToolsAsync()`, and exposes them as `AITool` instances. MCP connections are per-session and disposed when the session ends.
- **Web chat shares Telegram path**: `ChatEndpoints` uses the same `AgentSessionRegistry` + `AgentInvoker` flow as Telegram. Channel key is `web:{agentName}`. Messages appear in transcripts and both UIs.
- **Command responses use markdown**: `PingCommand` and other command responses use markdown (not Telegram HTML) for cross-UI compatibility.
