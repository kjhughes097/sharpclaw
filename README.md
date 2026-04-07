# SharpClaw

SharpClaw is a .NET 10 personal agent framework with a web UI, PostgreSQL-backed agent/session storage, Anthropic, OpenAI, OpenRouter, and GitHub Copilot backends, and Model Context Protocol support for tool execution.

It is inspired by [OpenClaw](https://github.com/openclaw/openclaw) and [GoClaw](https://github.com/nextlevelbuilder/goclaw), but is implemented as a native .NET stack with a React frontend.

![SharpClaw mascot](https://github.com/user-attachments/assets/0dd5321b-b058-4319-8904-c3b2a9bd9212)

## Overview

SharpClaw provides:

- A web chat interface for interacting with agents
- Username/password login flow in the web UI with JWT-based auth
- Database-backed storage for agent definitions, sessions, and message history
- Multiple agent backends:
	- Anthropic
	- OpenAI
	- OpenRouter
	- GitHub Copilot SDK
- MCP tool execution with MCP-scoped permission policies
- 'Ade' an agent that is expert in routing to specialist agents
- Backend model discovery for Anthropic, OpenAI, OpenRouter, and Copilot-backed agents
- In-app agent management:
	- list agents
	- create agents
	- edit agents
	- enable/disable agents
	- safe delete with linked-session protection
- In-app backend management:
	- providers start disabled by default
	- enable/disable each provider explicitly
	- store and rotate provider API keys in PostgreSQL
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
- The web client supports one-time setup of a single admin user and then requires login
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

### Backend Management

The UI includes a `Configure > Backends` screen where users can:

- See all registered LLM backends
- Enable or disable each backend independently
- Save API keys directly into the database
- Rotate or clear stored keys without editing environment variables

On a fresh database, all backends start disabled with no keys stored.

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
- `duckduckgo`
- `knowledge-base`

The seeded `github` MCP starts disabled by default. It requires `GITHUB_PERSONAL_ACCESS_TOKEN` in the API runtime environment before you enable it.

The seeded `duckduckgo` MCP starts enabled by default and is launched with `docker run -i --rm mcp/duckduckgo`. Docker must be installed and available on the API host for DuckDuckGo-backed web search to work.

The seeded `knowledge-base` MCP also starts disabled by default. It resolves to `~/knowledge` for non-Docker runs, `/knowledge` in Docker Compose, and `/var/lib/sharpclaw/knowledge` for systemd service installs.

The filesystem MCP server is constrained to the workspace path, resolved from:

1. **Workspace path** (database-backed, configurable via `Configure > Settings` in the UI or the `/api/settings/app` endpoint)
2. The current user's home directory (fallback when no workspace is set)

The workspace path is persisted in PostgreSQL and can be updated at runtime via the REST API without restarting the service. It defaults to `/workspace` (Docker) or `/opt/sharpclaw/workspace` (service install) on a fresh database.

## Architecture

### Main Components

- `SharpClaw.Api`: ASP.NET Core backend API and runtime host
- `SharpClaw.Core`: shared agent runtime, persistence, permissions, routing, and MCP integration
- `SharpClaw.Copilot`: GitHub Copilot SDK backend implementation
- `SharpClaw.OpenAI`: OpenAI Chat Completions backend implementation
- `SharpClaw.OpenRouter`: OpenRouter backend implementation (OpenAI-compatible API)
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
├── SharpClaw.Anthropic/
├── SharpClaw.Api/
├── SharpClaw.Copilot/
├── SharpClaw.Core/
├── SharpClaw.OpenAI/
├── SharpClaw.OpenRouter/
├── SharpClaw.RebuildHook/
├── SharpClaw.Web/
└── workspace/
```

### Folder Notes

- `SharpClaw.Api/`
	- Minimal API backend
	- Serves health, session, streaming, permissions, and agent-management endpoints
- `SharpClaw.Anthropic/`
	- Anthropic backend integration via the `Anthropic` NuGet package
- `SharpClaw.Core/`
	- `AgentRunner`
	- `SessionStore`
	- `CoordinatorAgent`
	- permission gates
	- MCP server registry
- `SharpClaw.Copilot/`
	- GitHub Copilot backend integration via `GitHub.Copilot.SDK`
- `SharpClaw.OpenAI/`
	- OpenAI backend integration via `OpenAI` NuGet package
- `SharpClaw.OpenRouter/`
	- OpenRouter backend integration; uses the OpenAI-compatible Chat Completions API at `https://openrouter.ai/api/v1`
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
| `GITHUB_PERSONAL_ACCESS_TOKEN` | Optional GitHub personal access token used by the GitHub MCP server when that MCP is enabled |
| `SHARPCLAW_JWT_SECRET` | Required JWT signing secret used for API authentication |
| `SHARPCLAW_DB_CONNECTION` | Required for non-Docker API runs unless `ConnectionStrings:DefaultConnection` is supplied another way |
| `SHARPCLAW_WORKSPACE` | Host-side path mounted into the Docker container as `/workspace` (Docker Compose and service installs only; not read by the API directly) |
| `SHARPCLAW_API_URL` | Optional Telegram worker override for API base URL (defaults to `http://127.0.0.1:5000`) |
| `SHARPCLAW_API_TOKEN` | Required Telegram worker bearer token for API authentication |

### Example `.env`

You can use a local `.env` file with Docker Compose:

```dotenv
POSTGRES_DB=sharpclaw
POSTGRES_USER=sharpclaw
POSTGRES_PASSWORD=change-me
SHARPCLAW_JWT_SECRET=replace-with-a-random-secret-at-least-32-characters-long
GITHUB_PERSONAL_ACCESS_TOKEN=
```

Notes:

- `POSTGRES_DB`, `POSTGRES_USER`, and `POSTGRES_PASSWORD` are required for Docker Compose runs and are used to configure both the PostgreSQL container and the API container's default connection string
- Direct `dotnet run` execution also requires a database connection via `SHARPCLAW_DB_CONNECTION` or `ConnectionStrings:DefaultConnection`; the API no longer falls back to a hard-coded local database credential set
- `SHARPCLAW_JWT_SECRET` is required whenever the API runs; use a random string with at least 32 characters
- `GITHUB_PERSONAL_ACCESS_TOKEN` is only needed if you want to enable the seeded GitHub MCP
- Docker Compose mounts `${HOME}/knowledge` into the API container at `/knowledge` for the seeded `knowledge-base` MCP
- LLM provider API keys are managed in `Configure > Backends` and stored in PostgreSQL
- Telegram settings (enabled, bot token, allowlists) are managed in `Configure > Telegram` and stored in PostgreSQL
- `SHARPCLAW_WORKSPACE` is only used by Docker Compose and the service install script to set up the workspace directory; the workspace path exposed to the filesystem MCP server is configured in `Configure > Settings` at runtime

### Configuration Fallbacks

The API resolves configuration in this order where applicable:

- JWT secret: `Auth:JwtSecret`, then `SHARPCLAW_JWT_SECRET`
- DB connection: `ConnectionStrings:DefaultConnection`, then `SHARPCLAW_DB_CONNECTION`
- Workspace path: database setting (persisted in PostgreSQL, configurable via the UI or API at runtime)

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

## Running as Linux Systemd Services

Use this path to install SharpClaw as persistent systemd services that start automatically on boot.  The API runs as a dedicated `sharpclaw` system user; nginx serves the compiled frontend and proxies `/api/` to the backend.

### Prerequisites

The system dependencies must already be installed before running the service install script.  If they are not, run `scripts/install-linux.sh` first (see [Running Locally on Linux](#running-locally-on-linux-without-docker)).

The service install script requires:
- .NET 10 SDK
- Node.js 18+ (22 LTS recommended) and npm
- `npx` available in the runtime toolchain for built-in MCP servers
- Docker installed and usable by the service host for the seeded `duckduckgo` MCP (`docker run -i --rm mcp/duckduckgo`)
- PostgreSQL 16 running with the SharpClaw database already created
- A populated `.env` file at the repo root

### Environment Setup

Copy `.env.example` to `.env` and fill in your values if you have not done so already:

```bash
cp .env.example .env
$EDITOR .env
```

### Running the Service Install Script

```bash
sudo ./scripts/install-service-linux.sh
```

The script:

1. Validates prerequisites (.NET SDK, Node.js, npm, PostgreSQL client)
2. Creates a dedicated `sharpclaw` system user (no login shell)
3. Publishes the .NET API to `/opt/sharpclaw/api`
4. Builds the React frontend (`npm ci && npm run build`) and deploys it to `/var/www/sharpclaw`

> **Note:** The built-in DuckDuckGo MCP uses Docker even for systemd installs. Make sure Docker is installed on the host and that the `sharpclaw` service user can access the Docker socket.
5. Installs nginx if not present
6. Writes an nginx site configuration that:
   - Serves the compiled frontend
   - Proxies `/api/` to the backend with SSE streaming support
7. Writes `/etc/sharpclaw/env` from your `.env` values (owned `root:sharpclaw`, mode `640`)
8. Writes `/etc/systemd/system/sharpclaw-api.service`
9. Optionally writes `/etc/systemd/system/sharpclaw-telegram.service` when Telegram is enabled
10. Enables and starts `sharpclaw-api.service` (and `sharpclaw-telegram.service` when configured) plus `nginx`

> **Note:** Run the script again at any time to redeploy after a code change.  The script stops the running service, republishes, redeploys, and restarts.

### Installed Paths

| Path | Contents |
|---|---|
| `/opt/sharpclaw/api` | Published .NET API binary |
| `/opt/sharpclaw/telegram` | Published Telegram worker binary |
| `/var/www/sharpclaw` | Compiled React frontend |
| `/etc/sharpclaw/env` | Environment file (secrets, auth settings, DB connection) |
| `/etc/systemd/system/sharpclaw-api.service` | API systemd unit |
| `/etc/systemd/system/sharpclaw-telegram.service` | Telegram worker systemd unit (optional) |
| `/etc/nginx/sites-available/sharpclaw` (Debian/Ubuntu) | nginx site config |
| `/etc/nginx/conf.d/sharpclaw.conf` (RHEL/Fedora) | nginx site config |
| `/opt/sharpclaw/workspace` | Default filesystem MCP workspace |

### Accessing the App

Browse to `http://localhost` (port 80, served by nginx).

> **Security note:** The nginx site configuration serves HTTP on port 80.  For deployments accessible beyond a trusted local or LAN environment, configure SSL/TLS (e.g. via Let's Encrypt / Certbot) and restrict inbound access with firewall rules before exposing the host to the internet.

### Managing the Services

```bash
# API service
sudo systemctl status  sharpclaw-api
sudo systemctl restart sharpclaw-api
sudo systemctl stop    sharpclaw-api
sudo journalctl -u     sharpclaw-api -f

# Telegram worker service (if enabled)
sudo systemctl status  sharpclaw-telegram
sudo systemctl restart sharpclaw-telegram
sudo systemctl stop    sharpclaw-telegram
sudo journalctl -u     sharpclaw-telegram -f

# nginx (frontend + proxy)
sudo systemctl status  nginx
sudo systemctl restart nginx
sudo journalctl -u     nginx -f
```

### Editing the Environment File

The environment file at `/etc/sharpclaw/env` holds all secrets and configuration for the running service, including the service `PATH` used by `npx`-based MCP subprocesses.  Edit it directly for runtime changes:

```bash
sudo $EDITOR /etc/sharpclaw/env
sudo systemctl restart sharpclaw-api
sudo systemctl restart sharpclaw-telegram
```

### Telegram Service (Optional)

`scripts/install-service-linux.sh` installs and manages `sharpclaw-telegram` as a systemd service by default. Pass `--no-telegram` to skip it:

```bash
sudo ./scripts/install-service-linux.sh --no-telegram
```

Telegram bot settings (enabled state, bot token, allowlists) are loaded from the SharpClaw API runtime settings endpoint, which reads from PostgreSQL.

Minimal `.env` settings when using the Telegram service:

```dotenv
SHARPCLAW_API_URL=http://127.0.0.1:5000
SHARPCLAW_API_TOKEN=<telegram-worker-bearer-token>
```

---

## Running Locally on Linux (without Docker Compose)

Use this path when you want to run the SharpClaw application processes directly on a Linux machine instead of through Docker Compose — for example during active development or debugging.

SharpClaw itself does not require Docker in this mode. The only Docker dependency is the seeded `duckduckgo` MCP, which launches through `docker run -i --rm mcp/duckduckgo`. If you do not want Docker installed for local runs, disable that MCP.

### Prerequisites

The following tools must be installed:

| Tool | Minimum version | Purpose |
|---|---|---|
| .NET SDK | 10.0 | Build and run the ASP.NET Core API |
| Node.js | 18 LTS (22 recommended) | Build and serve the React frontend |
| npm | bundled with Node.js | Frontend package management |
| Docker | recent CLI/Engine | Optional; only needed if you want to use the seeded `duckduckgo` MCP |
| PostgreSQL | 16 | Persistent storage for agents, sessions, and messages |

If you are starting from a fresh Debian/Ubuntu or RHEL/Fedora/CentOS Stream machine, the install script handles everything:

```bash
sudo ./scripts/install-linux.sh
```

The script:

1. Installs .NET 10 SDK (via the Microsoft install script)
2. Installs Node.js 22 LTS and npm (via the NodeSource package feed)
3. Installs PostgreSQL 16
4. Creates the PostgreSQL role and database using credentials from your `.env` file

> **Note:** `scripts/install-linux.sh` does not install Docker. Install Docker separately only if you want to use the seeded DuckDuckGo MCP in local Linux runs; otherwise you can disable that MCP.

> **Note:** Open a new terminal after running the install script so that updated `PATH` entries take effect.

### Environment Setup

Copy `.env.example` to `.env` and fill in your values:

```bash
cp .env.example .env
$EDITOR .env
```

Key variables for a local run (see [Environment Variables](#environment-variables) for the full list):

| Variable | Notes |
|---|---|
| `POSTGRES_DB` | Database name (e.g. `sharpclaw`) |
| `POSTGRES_USER` | PostgreSQL role name |
| `POSTGRES_PASSWORD` | PostgreSQL role password |
| `SHARPCLAW_DB_CONNECTION` | Full connection string – the backend script constructs this automatically from `POSTGRES_*` if it is not set |
| `GITHUB_PERSONAL_ACCESS_TOKEN` | Needed only if you want to enable the seeded GitHub MCP |
| `SHARPCLAW_WORKSPACE` | Host-side path mounted as the default workspace directory (service install only; workspace is configured at runtime via `Configure > Settings`) |
| `SHARPCLAW_KNOWLEDGE_BASE` | Optional override for the seeded `knowledge-base` MCP path; service installs default this to `/var/lib/sharpclaw/knowledge` |
| `SHARPCLAW_JWT_SECRET` | Required JWT signing secret for API authentication |

After startup, configure backend provider keys in `Configure > Backends`.
The generated service env file only carries runtime and MCP-level environment values; backend provider credentials are no longer written there.

### Starting the Backend

```bash
./scripts/start-backend.sh
```

The script:

- Loads `.env` from the repo root
- Derives `SHARPCLAW_DB_CONNECTION` from `POSTGRES_*` variables if neither `SHARPCLAW_DB_CONNECTION` nor `ConnectionStrings__DefaultConnection` is already set
- Restores NuGet packages
- Runs the API with `dotnet run`

The API listens on `http://localhost:5000` (and `https://localhost:5001`) by default.  Pass extra `dotnet run` flags after the script name if needed:

```bash
./scripts/start-backend.sh --launch-profile Development
```

### Starting the Frontend Dev Server

In a **second terminal**:

```bash
./scripts/start-frontend.sh
```

The script:

- Runs `npm ci` in `SharpClaw.Web/` if `node_modules` is absent
- Starts the Vite dev server on `http://localhost:5173`

The Vite configuration (`SharpClaw.Web/vite.config.ts`) already proxies `/api/` requests to the backend at `http://localhost:5000`, so no additional configuration is needed for a local dev setup.

### Accessing the App

Browse to `http://localhost:5173`.

### Typical Local Development Workflow

```text
Terminal 1:  sudo ./scripts/install-linux.sh   # one time only
             # open a new terminal after install to pick up updated PATH

Terminal 1 (new shell):
             cp .env.example .env && $EDITOR .env
             ./scripts/start-backend.sh

Terminal 2:  ./scripts/start-frontend.sh
```

---

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
	- returns live or cached models for `anthropic`, `openai`, or `copilot`
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
		"id": "ade",
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
	"agentId": "ade"
}
```

Example response:

```json
{
	"sessionId": "abc123def456",
	"persona": "Ade",
	"agentId": "ade"
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

- `/api/auth/setup` is available until the first user is created
- After setup, all API routes except auth status/login/logout require authentication
- Browser clients authenticate with an HttpOnly JWT cookie set by `/api/auth/login`
- Non-browser clients can authenticate with a `Bearer` token obtained from `/api/auth/login`

## Built-In Agents

The database is automatically seeded with built-in agents on startup. Agent definitions are stored in `agents/*.md` and loaded via `SessionStore.cs`. These seeds are inserted safely and do not overwrite user-edited definitions.

### Agent Roster

| Agent | Role | MCP | Backend | Model |
|-------|------|-----|---------|-------|
| **Ade** | Generalist router; routes to specialists when a better fit exists | `duckduckgo` | Anthropic | Claude Haiku 4.5 |
| **Noah** | Knowledge manager; captures and organizes notes, todos, meeting notes, journals | `knowledge-base`, `duckduckgo` | Anthropic | Claude Haiku 4.5 |
| **Cody** | Software architect; C#, TypeScript, Python expert; SOLID principles and design patterns | `filesystem`, `github`, `duckduckgo` | Copilot | Claude Opus 4.6 |
| **Debbie** | Critical thinking partner; challenges ideas, finds gaps, plays devil's advocate | `duckduckgo` | Anthropic | Claude Haiku 4.5 |
| **Remy** | Task manager; captures and organizes todos, reminders, shopping lists | `knowledge-base`, `duckduckgo` | Anthropic | Claude Haiku 4.5 |

### Agent Details

**Ade** (`ade`) — Your generalist assistant. Helps directly or routes to a specialist when appropriate. Uses JSON routing decisions to hand off work.

**Noah** (`noah`) — Knowledge base manager. Captures reflections, meeting notes, daily journals, and work documentation using the `knowledge-base` MCP. Optimized for speed with built-in safety (asks before destructive edits).

**Cody** (`cody`) — Software architect with deep expertise in design patterns, SOLID principles, clean architecture, and testability. Uses the `filesystem` MCP to read and write code. Runs on Copilot backend with Claude Opus 4.6 for more sophisticated architectural reasoning.

**Debbie** (`debbie`) — Rigorous thinking partner. Challenges assumptions, probes for gaps, examines trade-offs, and plays devil's advocate constructively. Helps you think stronger, not just feel better.

**Remy** (`remy`) — Task and reminder manager. Captures todos, reminders, and shopping lists quickly and reliably using the `knowledge-base` MCP. Organizes by category, priority, and due date; suggests weekly reviews.

### Built-In MCPs

- `filesystem` — local workspace file access
- `sqlite` — SQLite inspection and querying
- `github` — GitHub repositories, issues, and pull requests
- `duckduckgo` — lightweight web search and page-content fetching via Docker image `mcp/duckduckgo` (requires Docker on the API host)
- `knowledge-base` — personal knowledge base files mounted from the configured knowledge directory

### Agent Files

Agent definitions are stored in markdown format in the `agents/` folder:
- `agents/ade.md`
- `agents/noah.md`
- `agents/cody.md`
- `agents/debbie.md`
- `agents/remy.md`

Each file contains YAML frontmatter (name, description, backend, model, MCP servers, permission policy) and a system prompt that guides the agent's behavior. You can edit these files to customize agent behavior; changes will be reflected in `SessionStore.cs` and seeded on next API startup.

## Backend Notes

### Anthropic Backend

- Implemented in `SharpClaw.Anthropic/`
- Uses the configured model from the stored agent definition
- Requires an API key stored in `Configure > Backends`

### OpenAI Backend

- Uses the `OpenAI` NuGet package (`OpenAI` 2.x) to call the Chat Completions API
- Requires an API key stored in `Configure > Backends`
- Supports streaming and tool use
- Default model: `gpt-4o-mini`
- Available models are surfaced via `GET /api/backends/openai/models`

### OpenRouter Backend

- Uses the OpenAI-compatible Chat Completions API at `https://openrouter.ai/api/v1`
- Requires an API key stored in `Configure > Backends`
- Supports streaming and tool use
- Default model: `openai/gpt-4o-mini`
- Available models are surfaced via `GET /api/backends/openrouter/models`

### Copilot Backend

- Uses `GitHub.Copilot.SDK`
- Requires a GitHub token stored in `Configure > Backends`
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
- MCP process arguments are stored as JSON arrays and must remain string-only because they are passed directly to stdio transports

## Still To Do (not prioritized)

- [#9 Refactor multi-class source files into one class per file](https://github.com/kjhughes097/sharpclaw/issues/9)
- [#10 Define a file storage hierarchy for generated artifacts](https://github.com/kjhughes097/sharpclaw/issues/10)
- [#12 Add a heartbeat agent or scheduled stuck-session monitor](https://github.com/kjhughes097/sharpclaw/issues/12)
- [#13 Add sample agent definitions and an importable starter set](https://github.com/kjhughes097/sharpclaw/issues/13)
- [#14 Refactor the permission system to reduce complexity](https://github.com/kjhughes097/sharpclaw/issues/14)
- [#17 Create a sandbox API Docker image with additional development tools](https://github.com/kjhughes097/sharpclaw/issues/17)