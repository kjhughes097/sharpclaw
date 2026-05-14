---
slug: /
sidebar_position: 1
---

# SharpClaw

SharpClaw is an AI agent orchestration service built on **.NET 10** and the **GitHub Copilot SDK**. It loads agents from markdown files, routes conversations through Telegram or HTTP, and provides a persistent workspace with memory and auditing.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│  SharpClaw Service (.NET 10 Web App)                │
├─────────────┬───────────────┬───────────────────────┤
│  Telegram   │  HTTP/SSE     │  MCP Server (self)    │
├─────────────┴───────────────┴───────────────────────┤
│  AgentInvoker  →  CommandRouter  →  AgentRunner     │
├─────────────────────────────────────────────────────┤
│  Registries: Agent │ Tool │ MCP │ Skill             │
├─────────────────────────────────────────────────────┤
│  Sessions  │  Memory  │  Auditing  │  Workspace     │
└─────────────────────────────────────────────────────┘
```

## Quick Start

```bash
# Build
dotnet build

# Run
dotnet run --project src/SharpClaw

# Test
dotnet test
```

The service listens on `http://localhost:5100` by default.

## Key Concepts

| Concept        | Description                                                        |
| -------------- | ------------------------------------------------------------------ |
| **Agent**      | An LLM persona defined by a `.agent.md` file with YAML frontmatter |
| **Tool**       | A callable function exposed to agents via the Copilot SDK          |
| **MCP Server** | External Model Context Protocol server (Stdio or HTTP)             |
| **Skill**      | A reusable prompt fragment injected into agent system prompts      |
| **Session**    | A conversation channel linking a user to an agent                  |
| **Command**    | A dot-prefixed shortcut (`.ping`, `.switch`, `.help`)              |
