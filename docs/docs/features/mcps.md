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
