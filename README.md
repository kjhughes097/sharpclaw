# SharpClaw

SharpClaw is a .NET 8 personal agent framework with a web UI, PostgreSQL-backed agent/session storage, Anthropic and GitHub Copilot backends, and Model Context Protocol support for tool execution.

It is inspired by [OpenClaw](https://github.com/openclaw/openclaw) and [GoClaw](https://github.com/nextlevelbuilder/goclaw), but is implemented as a native .NET stack with a React frontend.

![SharpClaw mascot](https://github.com/user-attachments/assets/0dd5321b-b058-4319-8904-c3b2a9bd9212)

## Overview

SharpClaw provides:

- A web chat interface for interacting with agents
- Database-backed storage for agent definitions, sessions, and message history
- Multiple agent backends:
	- Anthropic
	- GitHub Copilot SDK
- MCP tool execution with permission policies
- Coordinator-based routing to specialist agents
- In-app agent management:
	- list agents
	- create agents
	- edit agents
	- enable/disable agents
	- safe delete with linked-session protection

## Quick Start

If you just want to get the stack running locally:

```bash
cp .env.example .env
docker compose up --build -d
open http://localhost:8080
```

If you want to use a `.env` file, copy `.env.example` to `.env` and fill in the values for your environment.

## Current Functionality

### Chat UX

- New chats default to the `Coordinator` agent
- The selected agent can be changed from a dropdown beside the chat input before the first message is sent
- Once the first message is sent, the session is bound to that agent
- If the active session uses the `Coordinator`, the backend routes the request to the best enabled specialist agent

### Agent Management

The UI includes a `Configure > Agents` screen where users can:

- Browse agents as cards
- Open a dedicated edit screen for an existing agent
- Create a new agent definition
- Configure:
	- backend
	- model
	- description
	- system prompt
	- MCP servers
	- permission policy
- Enable or disable agents
- Delete agents with hard confirmation
- Purge linked sessions when deleting an agent that is already in use
- Manage a DB-backed MCP registry from the UI
- Enable or disable MCPs globally
- Detach MCPs from linked agents during deletion

### Tooling and MCP

SharpClaw now stores MCP definitions in PostgreSQL and resolves them at runtime from the database. A fresh database is seeded with these MCPs:

- `filesystem`
- `sqlite`
- `github`

The filesystem MCP server is constrained to allowed directories resolved from:

1. `SHARPCLAW_WORKSPACE`
2. `MCP_ALLOWED_DIRS`
3. the current user's home directory

## Architecture

### Main Components

- `SharpClaw.Api`: ASP.NET Core backend API and runtime host
- `SharpClaw.Core`: shared agent runtime, persistence, permissions, routing, and MCP integration
- `SharpClaw.Copilot`: GitHub Copilot SDK backend implementation
- `SharpClaw.Web`: React + Vite frontend
- `SharpClaw.RebuildHook`: optional local webhook service for Docker Compose rebuilds

### Data Model

The PostgreSQL store persists:

- agent definitions
- MCP definitions
- chat sessions
- message history

Agent definitions currently include:

- filename
- name
- brief description
- backend
- model
- MCP server list
- permission policy
- system prompt
- enabled/disabled state

## Project Structure

```text
.
├── Dockerfile
├── docker-compose.yml
├── README.md
├── SharpClaw.slnx
├── SharpClaw.Api/
├── SharpClaw.Copilot/
├── SharpClaw.Core/
├── SharpClaw.RebuildHook/
├── SharpClaw.Web/
└── workspace/
```

### Folder Notes

- `SharpClaw.Api/`
	- Minimal API backend
	- Serves health, session, streaming, permissions, and agent-management endpoints
- `SharpClaw.Core/`
	- `AgentRunner`
	- `SessionStore`
	- `CoordinatorAgent`
	- permission gates
	- MCP server registry
- `SharpClaw.Copilot/`
	- GitHub Copilot backend integration via `GitHub.Copilot.SDK`
- `SharpClaw.Web/`
	- React frontend
	- Nginx config for proxying `/api/` to the backend in Docker
- `SharpClaw.RebuildHook/`
	- Separate service for webhook-triggered Docker Compose rebuild workflows
- `workspace/`
	- Default mounted workspace path for filesystem MCP access in Docker

## Environment Variables

### Required or Commonly Used

| Variable | Purpose |
|---|---|
| `ANTHROPIC_API_KEY` | Required for Anthropic-backed agents |
| `GITHUB_COPILOT_TOKEN` | Copilot auth token used by the GitHub Copilot backend |
| `GITHUB_TOKEN` | Alternate token source checked by the Copilot backend |
| `SHARPCLAW_API_KEY` | Optional API key enforced on `/api/*` routes except SSE streams |
| `SHARPCLAW_DB_CONNECTION` | Optional PostgreSQL connection string override |
| `SHARPCLAW_WORKSPACE` | Workspace path used by the backend and filesystem MCP tooling |
| `MCP_ALLOWED_DIRS` | Colon-delimited allowed directories for filesystem MCP access when `SHARPCLAW_WORKSPACE` is not set |

### Example `.env`

You can use a local `.env` file with Docker Compose:

```dotenv
ANTHROPIC_API_KEY=your-anthropic-key
GITHUB_COPILOT_TOKEN=your-copilot-token
SHARPCLAW_API_KEY=replace-me-if-you-want-api-auth
SHARPCLAW_WORKSPACE=/absolute/path/to/your/workspace
```

Notes:

- `ANTHROPIC_API_KEY` is required for Anthropic-backed agents
- `GITHUB_COPILOT_TOKEN` or `GITHUB_TOKEN` is required for Copilot-backed agents
- `SHARPCLAW_API_KEY` is optional, but recommended if you do not want an open local API
- `SHARPCLAW_WORKSPACE` should point at the directory you want the filesystem MCP server to expose

### Configuration Fallbacks

The API resolves configuration in this order where applicable:

- API key: `ApiKey` config value, then `SHARPCLAW_API_KEY`
- DB connection: `ConnectionStrings:DefaultConnection`, then `SHARPCLAW_DB_CONNECTION`, then a local default PostgreSQL connection string
- Workspace: `SHARPCLAW_WORKSPACE`, then current working directory

## Running the Project

### Recommended: Docker Compose

This is the easiest way to run the full stack.

```bash
docker compose up --build -d
```

This starts:

- `postgres`
- `sharpclaw` API container
- `web` container

### Exposed Entry Point

The web container is published on:

- `http://localhost:8080`

The Nginx container proxies `/api/` requests to the backend service internally.

### Running With a `.env` File

Docker Compose automatically reads `.env` from the repository root.

Example:

```bash
docker compose up --build -d
docker compose logs -f sharpclaw web
```

## Local Development

### Backend

Build the solution:

```bash
dotnet build SharpClaw.slnx
```

Run the API directly:

```bash
dotnet run --project SharpClaw.Api
```

### Frontend

```bash
cd SharpClaw.Web
npm ci
npm run build
```

For iterative frontend work:

```bash
cd SharpClaw.Web
npm ci
npm run dev
```

## API Surface

### Health

- `GET /api/health`

Example response:

```json
{
	"status": "ok",
	"service": "SharpClaw"
}
```

### Agents

- `GET /api/personas`
	- returns enabled agents for chat selection
- `GET /api/agents`
	- returns all agents with linked session counts
- `POST /api/agents`
- `PUT /api/agents/{filename}`
- `PATCH /api/agents/{filename}/enabled`
- `DELETE /api/agents/{filename}`
	- blocks if sessions exist unless `purgeSessions=true`

### MCPs

- `GET /api/mcps`
	- returns all MCPs with linked agent counts
- `POST /api/mcps`
- `PUT /api/mcps/{slug}`
- `PATCH /api/mcps/{slug}/enabled`
- `DELETE /api/mcps/{slug}`
	- blocks if agents reference it unless `detachAgents=true`

#### `GET /api/mcps`

Returns all stored MCP definitions, including disabled entries and linked agent counts.

Example response:

```json
[
	{
		"slug": "github",
		"name": "GitHub",
		"description": "Interact with GitHub repositories, issues, and pull requests.",
		"command": "npx",
		"args": ["-y", "@modelcontextprotocol/server-github"],
		"isEnabled": true,
		"linkedAgentCount": 1
	}
]
```

#### `GET /api/personas`

Returns only enabled agents intended for chat selection.

Example response:

```json
[
	{
		"file": "coordinator.agent.md",
		"name": "Coordinator",
		"description": "Routes a user request to the best specialist agent.",
		"backend": "anthropic",
		"model": "claude-haiku-4-5-20251001",
		"mcpServers": [],
		"permissionPolicy": {},
		"systemPrompt": "You are a routing coordinator...",
		"isEnabled": true
	}
]
```

#### `GET /api/agents`

Returns all stored agents, including disabled agents and linked session counts.

Example response:

```json
[
	{
		"file": "file-browser.agent.md",
		"name": "FileBrowser",
		"description": "Searches, lists, and reads files from the local workspace.",
		"backend": "copilot",
		"model": "gpt-5.4",
		"mcpServers": ["filesystem"],
		"permissionPolicy": {
			"read_file": "auto_approve",
			"*": "ask"
		},
		"systemPrompt": "You are a helpful file browser assistant...",
		"isEnabled": true,
		"sessionCount": 0
	}
]
```

#### `POST /api/agents`

Creates a new agent definition.

Example request:

```json
{
	"file": "ops.agent.md",
	"name": "Ops",
	"description": "Handles operations and service diagnostics.",
	"backend": "anthropic",
	"model": "claude-haiku-4-5-20251001",
	"mcpServers": ["filesystem"],
	"permissionPolicy": {
		"read_file": "auto_approve",
		"*": "ask"
	},
	"systemPrompt": "You are an operations assistant.",
	"isEnabled": true
}
```

#### `PATCH /api/agents/{filename}/enabled`

Enables or disables an existing agent.

Example request:

```json
{
	"isEnabled": false
}
```

#### `DELETE /api/agents/{filename}`

Deletes an agent. If the agent has linked sessions, deletion is blocked unless `purgeSessions=true` is supplied.

Conflict response example:

```json
{
	"error": "Agent 'file-browser.agent.md' has 4 linked session(s). Re-run delete with purgeSessions=true to delete those sessions first.",
	"linkedSessionCount": 4,
	"requiresSessionPurge": true
}
```

### Sessions and Streaming

- `POST /api/sessions`
- `POST /api/sessions/{id}/messages`
- `GET /api/sessions/{id}/messages/{msgId}/stream`
- `POST /api/sessions/{id}/permissions/{requestId}`

#### `POST /api/sessions`

Creates a new chat session bound to a selected agent.

Example request:

```json
{
	"persona": "coordinator.agent.md"
}
```

Example response:

```json
{
	"sessionId": "abc123def456",
	"persona": "Coordinator"
}
```

#### `POST /api/sessions/{id}/messages`

Queues a user message for processing and returns a message stream identifier.

Example request:

```json
{
	"message": "Review the Docker setup and suggest improvements."
}
```

Example response:

```json
{
	"sessionId": "abc123def456",
	"messageId": "89ab12cd"
}
```

#### `GET /api/sessions/{id}/messages/{msgId}/stream`

Returns an SSE stream of agent events such as:

- `token`
- `tool_call`
- `tool_result`
- `permission_request`
- `status`
- `done`

#### `POST /api/sessions/{id}/permissions/{requestId}`

Resolves a pending permission request.

Example request:

```json
{
	"allow": true
}
```

## Authentication Behavior

- If `SHARPCLAW_API_KEY` is not set, API routes are effectively open
- If it is set, clients must send `X-Api-Key`
- SSE stream endpoints are exempt because browser `EventSource` cannot send custom headers

## Built-In Agents

The database is automatically seeded with built-in agents on startup. Current seeded agents include:

- `coordinator.agent.md`
- `developer.agent.md`
- `file-browser.agent.md`
- `homelab.agent.md`
- `home-assistant.agent.md`

These seeds are inserted safely and do not overwrite user-edited definitions.

## Backend Notes

### Anthropic Backend

- Uses the configured model from the stored agent definition
- Requires `ANTHROPIC_API_KEY`

### Copilot Backend

- Uses `GitHub.Copilot.SDK`
- Checks `GITHUB_TOKEN` first, then `GITHUB_COPILOT_TOKEN`
- Uses the configured workspace directory for tool context
- The stored `model` field is currently persisted and shown in the UI, but Copilot model selection is not yet enforced explicitly by the runtime

## Rebuild Hook

`SharpClaw.RebuildHook` is an optional companion service for accepting local webhook requests and triggering Docker Compose rebuilds.

See:

- [SharpClaw.RebuildHook/README.md](SharpClaw.RebuildHook/README.md)

## Development Notes

- TypeScript incremental build cache files are ignored via `*.tsbuildinfo`
- The web app builds to `SharpClaw.Web/dist/`
- The compose web container serves the built frontend through Nginx
- The API container installs Node.js and npm because some MCP servers are launched via `npx`

## Known Limitations

- Copilot-backed agent model selection is stored but not fully enforced at runtime
- SSE authentication is intentionally relaxed due to browser limitations around custom headers
- MCP process arguments are stored as JSON arrays and must remain string-only because they are passed directly to stdio transports
