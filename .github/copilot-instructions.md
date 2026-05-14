# SharpClaw — Copilot Instructions

## Architecture

Single project: **`src/SharpClaw`** — a .NET 10 Web SDK app hosting all registries, execution engine, MCP server, Telegram integration, and HTTP endpoints.

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

Agents are defined in `src/SharpClaw/agents/*.agent.md` files. The filename (without extension) is the agent name. YAML frontmatter declares:

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

Implement `ITool` in `src/SharpClaw/Tools/`, register in DI in `Program.cs`, and add to the tool registry. Reference the tool name in agent frontmatter.

## Adding MCP Servers (External)

Add a JSON file to `src/SharpClaw/mcps/`:

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
dotnet run --project src/SharpClaw
```

Launch settings set `ASPNETCORE_ENVIRONMENT=Development` and `DOTNET_ENVIRONMENT=Development`. Service listens on `http://localhost:5100`.

## Documentation (Docusaurus)

The documentation site lives in `docs/` and is built with Docusaurus (TypeScript, classic preset).

**Every code change that affects behaviour, configuration, or public interfaces must have corresponding documentation updates.** Consider:

- New or changed features → update or create a page in `docs/docs/features/`
- New agents, tools, or MCP servers → update relevant feature page
- Configuration changes → update `docs/docs/features/scaffolding.md`
- Architecture changes → update `docs/docs/intro.md`
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
