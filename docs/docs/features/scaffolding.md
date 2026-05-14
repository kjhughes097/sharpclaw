---
sidebar_position: 1
---

# Scaffolding

SharpClaw is a single .NET 10 project using the Web SDK.

## Project Structure

```
src/SharpClaw/
├── Program.cs                 # DI, middleware, endpoints
├── SharpClaw.csproj           # Web SDK, packages, content items
├── agents/                    # *.agent.md files (loaded at startup)
├── mcps/                      # MCP server JSON configs
├── skills/                    # *.skill.md prompt fragments
├── Abstractions/              # Interfaces (IAgent, ITool, etc.)
├── Registry/                  # ConcurrentDictionary-backed registries
├── Loading/                   # AgentLoader, McpLoader, SkillLoader, parser
├── Execution/                 # AgentRunner, SpawnAgentTool, ToolAIFunctionAdapter
├── Models/                    # Records: AgentDefinition, AgentRunRequest, etc.
├── Sessions/                  # AgentSession, AgentSessionRegistry
├── Commands/                  # ICommand implementations (.ping, .switch, .help)
├── Interactions/              # AgentInvoker (orchestrates command→agent flow)
├── Telegram/                  # TelegramService (BackgroundService), router
├── Memory/                    # MemoryService (per-agent file-based memory)
├── Workspace/                 # WorkspaceInitialiser
├── Auditing/                  # AuditService (markdown audit log)
├── Mcp/                       # Self-hosted MCP server tools (MemoryMcpTools)
├── Tools/                     # Custom ITool implementations
└── Configuration/             # Options classes (SharpClawOptions, etc.)
```

## Key Packages

| Package                   | Purpose                      |
| ------------------------- | ---------------------------- |
| `GitHub.Copilot.SDK`      | LLM access via Copilot API   |
| `Anthropic`               | LLM access via Anthropic API |
| `Microsoft.Extensions.AI` | IChatClient abstraction      |
| `ModelContextProtocol`    | MCP client/server support    |
| `Telegram.Bot`            | Telegram integration         |
| `Microsoft.Data.Sqlite`   | Future session persistence   |
| `OpenTelemetry.*`         | Traces, metrics, logs → OTLP |

## Configuration

Configuration is in `appsettings.json` / `appsettings.Development.json`:

```json
{
  "SharpClaw": {
    "AgentsDirectory": "agents",
    "McpDirectory": "mcps",
    "SkillsDirectory": "skills",
    "WorkspacePath": "/home/user/sharpclaw-workspace",
    "ChatHistoryLimit": 50,
    "DefaultAgent": "ade"
  },
  "Telegram": {
    "BotToken": "YOUR_BOT_TOKEN_HERE",
    "AllowedUsers": ["username"],
    "DefaultAgent": "ade"
  },
  "OpenTelemetry": {
    "Endpoint": "http://localhost:4318"
  },
  "Anthropic": {
    "ApiKey": "",
    "DefaultModel": "claude-sonnet-4-20250514",
    "MaxTokens": 8192
  }
}
```

## NuGet Restore

A project-level `NuGet.Config` with `<clear />` ensures only nuget.org is used, avoiding issues with user-level package sources.

## Local Control Script

The repository root includes `sharpclaw.sh` for common development operations:

```bash
./sharpclaw.sh start    # Starts SharpClaw + Docker Compose observability stack
./sharpclaw.sh stop     # Stops SharpClaw + Docker Compose observability stack
./sharpclaw.sh restart  # Restarts SharpClaw + Docker Compose observability stack
./sharpclaw.sh status   # Shows SharpClaw + Docker Compose observability status
./sharpclaw.sh logs     # Opens Grafana logs UI (Explore), filtered to service_name="SharpClaw"
./sharpclaw.sh logs service  # Shows SharpClaw local service log (optional)
./sharpclaw.sh test     # Runs dotnet test from repo root
./sharpclaw.sh docs     # Starts Docusaurus docs server on http://localhost:3001 (docs/)
```
