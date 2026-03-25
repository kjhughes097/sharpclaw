# SharpClaw

SharpClaw is a .NET 10 personal agent framework with a web UI, PostgreSQL-backed agent/session storage, Anthropic and GitHub Copilot backends, and Model Context Protocol support for tool execution.

It is inspired by [OpenClaw](https://github.com/openclaw/openclaw) and [GoClaw](https://github.com/nextlevelbuilder/goclaw), but is implemented as a native .NET stack with a React frontend.

![SharpClaw mascot](https://github.com/user-attachments/assets/0dd5321b-b058-4319-8904-c3b2a9bd9212)

## Overview

SharpClaw provides:

- A web chat interface for interacting with agents
- API-key login flow in the web UI when backend auth is enabled
- Database-backed storage for agent definitions, sessions, and message history
- Multiple agent backends:
	- Anthropic
	- GitHub Copilot SDK
- MCP tool execution with MCP-scoped permission policies
- 'Ade' an agent that is expert in routing to specialist agents
- Backend model discovery for Anthropic and Copilot-backed agents
- In-app agent management:
	- list agents
	- create agents
	- edit agents
	- enable/disable agents
	- safe delete with linked-session protection
- In-app MCP management with agent-to-MCP linking from both directions
- Session restore and safe session deletion from the sidebar
- Light/dark theme toggle and mobile-friendly navigation

## Quick Start

If you just want to get the stack running locally:

```bash
cp .env.example .env
docker compose up --build -d
```

Then browse to `http://localhost:8080`.

If you want to use a `.env` file, copy `.env.example` to `.env` and fill in the values for your environment.

## Current Functionality

### Chat UX

- New chats default to the `Ade` agent
- The selected agent can be changed from a dropdown beside the chat input before the first message is sent
- Once the first message is sent, the session is bound to that agent
- If the active session uses `Ade`, the backend routes the request to the best enabled specialist agent when one is a better fit
- Existing sessions are restored from PostgreSQL on reload
- Sessions can be deleted from the sidebar when they are not actively streaming
- The web client prompts for an API key when `SHARPCLAW_API_KEY` is configured on the backend
- The web UI includes a persisted light/dark theme toggle and responsive mobile navigation

### Agent Management

The UI includes a `Configure > Agents` screen where users can:

- Browse agents as cards
- Open a dedicated edit screen for an existing agent
- Create a new agent definition
- Configure:
	- backend
	- model
	- live backend model list lookup
	- description
	- system prompt
	- MCP servers
	- permission policy
- Enable or disable agents
- Delete agents with hard confirmation
- Purge linked sessions when deleting an agent that is already in use
- View linked-session counts per agent

### MCP Management

The UI includes a `Configure > MCPs` screen where users can:

- Browse and edit stored MCP definitions
- Create new MCP definitions with command and args
- Enable or disable MCPs globally
- See which agents are linked to an MCP
- Detach linked agents while deleting an MCP
- Link and unlink MCPs from either the MCP editor or the agent editor

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

- slug
- name
- brief description
- backend
- model
- MCP server list
- permission policy
- system prompt
- enabled/disabled state

Older flat permission rules are migrated on startup to MCP-scoped patterns when the target MCP can be inferred safely.

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
| `POSTGRES_DB` | Required PostgreSQL database name for the Docker Compose stack |
| `POSTGRES_USER` | Required PostgreSQL username for the Docker Compose stack and API container |
| `POSTGRES_PASSWORD` | Required PostgreSQL password for the Docker Compose stack and API container |
| `ANTHROPIC_API_KEY` | Required for Anthropic-backed agents |
| `GITHUB_COPILOT_TOKEN` | Copilot auth token used by the GitHub Copilot backend |
| `GITHUB_TOKEN` | Alternate token source checked by the Copilot backend |
| `SHARPCLAW_API_KEY` | Optional API key enforced on `/api/*` routes except SSE streams |
| `SHARPCLAW_DB_CONNECTION` | Required for non-Docker API runs unless `ConnectionStrings:DefaultConnection` is supplied another way |
| `SHARPCLAW_WORKSPACE` | Workspace path used by the backend and filesystem MCP tooling |
| `MCP_ALLOWED_DIRS` | Colon-delimited allowed directories for filesystem MCP access when `SHARPCLAW_WORKSPACE` is not set |

### Example `.env`

You can use a local `.env` file with Docker Compose:

```dotenv
POSTGRES_DB=sharpclaw
POSTGRES_USER=sharpclaw
POSTGRES_PASSWORD=change-me
ANTHROPIC_API_KEY=your-anthropic-key
GITHUB_COPILOT_TOKEN=your-copilot-token
SHARPCLAW_API_KEY=replace-me-if-you-want-api-auth
SHARPCLAW_WORKSPACE=/absolute/path/to/your/workspace
```

Notes:

- `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD` are required for Docker Compose runs and are used to configure both the PostgreSQL container and the API container's default connection string
- Direct `dotnet run` execution also requires a database connection via `SHARPCLAW_DB_CONNECTION` or `ConnectionStrings:DefaultConnection`; the API no longer falls back to a hard-coded local database credential set
- `ANTHROPIC_API_KEY` is required for Anthropic-backed agents
- `GITHUB_COPILOT_TOKEN` or `GITHUB_TOKEN` is required for Copilot-backed agents
- `SHARPCLAW_API_KEY` is optional, but recommended if you do not want an open local API
- `SHARPCLAW_WORKSPACE` should point at the directory you want the filesystem MCP server to expose

### Configuration Fallbacks

The API resolves configuration in this order where applicable:

- API key: `ApiKey` config value, then `SHARPCLAW_API_KEY`
- DB connection: `ConnectionStrings:DefaultConnection`, then `SHARPCLAW_DB_CONNECTION`
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

If you are iterating on the codebase rather than running the full stack through Docker Compose, use the sections below.

### Frontend Dependencies

Install frontend dependencies once before running the Vite dev server or local web builds:

```bash
cd SharpClaw.Web
npm ci
```

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
npm run build
```

For iterative frontend work:

```bash
cd SharpClaw.Web
npm run dev
```

## Development Workflow

Common commands during day-to-day development:

### Rebuild Only The Web Container

```bash
docker compose build web && docker compose up -d web
```

### Rebuild Only The API Container

```bash
docker compose build sharpclaw && docker compose up -d sharpclaw
```

### Rebuild The Full Stack

```bash
docker compose up --build -d
```

### Follow API And Web Logs

```bash
docker compose logs -f sharpclaw web
```

### Run A Frontend Production Build Locally

```bash
cd SharpClaw.Web
npm run build
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
- `GET /api/backends/{backend}/models`
	- returns live or cached models for `anthropic` or `copilot`
- `POST /api/agents`
- `PUT /api/agents/{slug}`
- `PATCH /api/agents/{slug}/enabled`
- `DELETE /api/agents/{slug}`
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
		"id": "ade.agent.md",
		"name": "Ade",
		"description": "A general assistant who helps directly and hands work to a better-fit specialist when needed.",
		"backend": "anthropic",
		"model": "claude-haiku-4-5-20251001",
		"mcpServers": [],
		"permissionPolicy": {},
		"systemPrompt": "You are Ade, a general assistant and aide...",
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
		"id": "file-browser.agent.md",
		"name": "FileBrowser",
		"description": "Searches, lists, and reads files from the local workspace.",
		"backend": "copilot",
		"model": "gpt-5.4",
		"mcpServers": ["filesystem"],
		"permissionPolicy": {
			"filesystem.read_*": "auto_approve",
			"filesystem.list_*": "auto_approve",
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
	"name": "Ops",
	"description": "Handles operations and service diagnostics.",
	"backend": "anthropic",
	"model": "claude-haiku-4-5-20251001",
	"mcpServers": ["filesystem"],
	"permissionPolicy": {
		"filesystem.read_*": "auto_approve",
		"filesystem.list_*": "auto_approve",
		"*": "ask"
	},
	"systemPrompt": "You are an operations assistant.",
	"isEnabled": true
}
```

#### `GET /api/backends/{backend}/models`

Returns available models for a backend. On upstream failure, the API may return a cached list with a warning.

Example response:

```json
{
	"models": [
		{
			"id": "claude-haiku-4-5-20251001",
			"displayName": "Claude Haiku 4.5"
		}
	],
	"source": "live",
	"cachedAt": null,
	"warning": null
}
```

#### `PATCH /api/agents/{slug}/enabled`

Enables or disables an existing agent.

Example request:

```json
{
	"isEnabled": false
}
```

#### `DELETE /api/agents/{slug}`

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

- `GET /api/sessions`
- `GET /api/sessions/{id}`
- `DELETE /api/sessions/{id}`
- `POST /api/sessions`
- `POST /api/sessions/{id}/messages`
- `GET /api/sessions/{id}/messages/{msgId}/stream`
- `POST /api/sessions/{id}/permissions/{requestId}`

#### `GET /api/sessions`

Returns persisted sessions for sidebar restoration, including messages and stored event logs.

#### `GET /api/sessions/{id}`

Returns a single persisted session payload.

#### `DELETE /api/sessions/{id}`

Deletes a session unless it is currently streaming. Streaming sessions return a conflict response instead.

#### `POST /api/sessions`

Creates a new chat session bound to a selected agent.

Example request:

```json
{
	"agentId": "ade.agent.md"
}
```

Example response:

```json
{
	"sessionId": "abc123def456",
	"persona": "Ade",
	"agentId": "ade.agent.md"
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
- `usage`
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

The database is automatically seeded with built-in agents on startup. The current built-in agent is:

- `ade.agent.md`

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

See [SharpClaw.RebuildHook/README.md](SharpClaw.RebuildHook/README.md).

## Development Notes

- TypeScript incremental build cache files are ignored via `*.tsbuildinfo`
- The web app builds to `SharpClaw.Web/dist/`
- The compose web container serves the built frontend through Nginx
- The API container installs Node.js and npm because some MCP servers are launched via `npx`

## License

This project is licensed under the MIT License. See [LICENSE](/home/khughes/projects/sharpclaw/LICENSE).

## Known Limitations

- Copilot-backed agent model selection is stored but not fully enforced at runtime
- SSE authentication is intentionally relaxed due to browser limitations around custom headers
- MCP process arguments are stored as JSON arrays and must remain string-only because they are passed directly to stdio transports

## Still To Do (not prioritized)

- [#7 Add OpenAI backend support](https://github.com/kjhughes097/sharpclaw/issues/7)
- [#8 Add OpenRouter backend support](https://github.com/kjhughes097/sharpclaw/issues/8)
- [#9 Refactor multi-class source files into one class per file](https://github.com/kjhughes097/sharpclaw/issues/9)
- [#10 Define a file storage hierarchy for generated artifacts](https://github.com/kjhughes097/sharpclaw/issues/10)
- [#11 Add Telegram channel integration](https://github.com/kjhughes097/sharpclaw/issues/11)
- [#12 Add a heartbeat agent or scheduled stuck-session monitor](https://github.com/kjhughes097/sharpclaw/issues/12)
- [#13 Add sample agent definitions and an importable starter set](https://github.com/kjhughes097/sharpclaw/issues/13)
- [#14 Refactor the permission system to reduce complexity](https://github.com/kjhughes097/sharpclaw/issues/14)
- [#15 Design a blue-green deployment strategy with automatic rollback](https://github.com/kjhughes097/sharpclaw/issues/15)
- [#16 Add scripts for local installation and non-Docker startup](https://github.com/kjhughes097/sharpclaw/issues/16)
- [#17 Create a sandbox API Docker image with additional development tools](https://github.com/kjhughes097/sharpclaw/issues/17)