---
sidebar_position: 3
---

# MCPs (Model Context Protocol)

SharpClaw integrates with external MCP servers as both a **client** (connecting to external servers) and a **server** (exposing its own tools via MCP).

## External MCP Servers (Client)

MCP server definitions are loaded from JSON files in `src/SharpClaw/mcps/`:

```json
{
  "name": "filesystem",
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@anthropic/mcp-filesystem"]
}
```

### Supported Transports

| Transport | Fields                   |
| --------- | ------------------------ |
| **Stdio** | `command`, `args`, `env` |
| **HTTP**  | `url`, `headers`         |

### Registry

`McpLoader` reads `*.json` files from the MCP directory and registers them in `IMcpRegistry`. The registry stores `McpServerDefinition` records:

```csharp
public record McpServerDefinition(
    string Name,
    string Transport,
    string? Command,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? Url,
    IReadOnlyDictionary<string, string>? Headers);
```

### Agent Binding

Agents reference MCP servers by name in frontmatter:

```yaml
mcp_servers: [filesystem, memory]
```

Omitting `mcp_servers` gives the agent access to **all** registered servers.

## Self-Hosted MCP Server

SharpClaw also exposes its own MCP endpoint via `ModelContextProtocol.AspNetCore`:

```csharp
builder.Services.AddMcpServer().WithHttpTransport().WithTools<MemoryMcpTools>();
app.MapMcp();
```

This allows external clients (e.g. VS Code, Claude Desktop) to connect to SharpClaw's memory tools.

## SDK Integration

`AgentRunner.ResolveMcpServers()` converts `McpServerDefinition` records to the Copilot SDK's `McpServerConfig` type (either `McpStdioServerConfig` or `McpHttpServerConfig`). The SDK expects an `IDictionary<string, McpServerConfig>`.

## Troubleshooting

When an agent reports an MCP server is unavailable, inspect the request path end-to-end before changing configuration.

### Log Checkpoints

Use `Information`-level logs at these points:

- `RegistryWorker.Reload()`
  - MCP registration events, e.g. `Registered MCP server {Name} (transport={Transport})`
- `AgentInvoker.InvokeAsync()`
  - Selected agent and declared MCP/tool counts for the conversation
- `AgentRunner.CreateSessionAsync()` / `ResolveMcpServers()`
  - Requested MCP names vs resolved names from `IMcpRegistry`
- `CopilotProvider.CreateSessionAsync()`
  - MCP set and transport details passed to Copilot SDK
  - Session creation failures with MCP context

If MCP names are requested, resolved, and passed to session creation, the next likely issue is prompt/tool naming mismatch rather than MCP registry wiring.

### Loki Queries

With the local stack, Loki is typically available at `http://127.0.0.1:3100`.

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

# Session/send failures
curl -sG 'http://127.0.0.1:3100/loki/api/v1/query_range' \
  --data-urlencode 'query={service_name="SharpClaw"} |= "Copilot session creation failed" or {service_name="SharpClaw"} |= "Copilot send failed" or {service_name="SharpClaw"} |= "Agent error:"' \
  --data-urlencode "start=$start_ns" \
  --data-urlencode "end=$end_ns" \
  --data-urlencode 'limit=300'
```

### Triage Order

1. Confirm MCP server loaded and registered.
2. Confirm agent requested MCP in frontmatter.
3. Confirm `AgentRunner` resolved the MCP.
4. Confirm `CopilotProvider` included it in session config.
5. If 1-4 pass, inspect tool naming in agent prompts (MCP tool names are commonly snake_case).
