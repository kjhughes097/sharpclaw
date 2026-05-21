---
sidebar_position: 14
---

# Web UI

SharpClaw includes a browser-based dashboard for managing agents, MCP servers, skills, and configuration without editing files directly.

## Technology

| Layer    | Stack                                    |
| -------- | ---------------------------------------- |
| Frontend | Vite 9, React 19, TypeScript, MUI v9    |
| Editor   | Monaco Editor (`@monaco-editor/react`)   |
| Build    | Production bundle served from `wwwroot/` |
| API      | .NET Minimal API endpoints under `/api/` |

## Pages

| Route              | Purpose                                         |
| ------------------ | ----------------------------------------------- |
| `/`                | Home — agent cards with LLM/model badges        |
| `/agents`          | Agent list with create/edit/delete               |
| `/agents/:name`    | Monaco editor for `.agent.md` files             |
| `/mcps`            | MCP server list with create/edit/delete          |
| `/mcps/:name`      | Monaco editor for MCP JSON definitions          |
| `/tools`           | Read-only tool list with parameter schemas       |
| `/tools/:name`     | Tool detail view                                |
| `/skills`          | Skill list with create/edit/delete               |
| `/skills/:name`    | Monaco editor for `.skill.md` files             |
| `/config`          | LLM keys, bot tokens, workspace settings         |
| `/examples`        | MUI component showcase (charts, grids, etc.)    |

## API Endpoints

All endpoints live under `/api/`:

```
GET    /api/agents          List all agents (name, description, llm, model)
GET    /api/agents/{name}   Get full .agent.md content
PUT    /api/agents/{name}   Update agent file content
POST   /api/agents          Create new agent
DELETE /api/agents/{name}   Delete agent

GET    /api/mcps            List all MCP definitions
GET    /api/mcps/{name}     Get MCP JSON content
PUT    /api/mcps/{name}     Update MCP definition
POST   /api/mcps            Create new MCP definition
DELETE /api/mcps/{name}     Delete MCP definition

GET    /api/tools           List all tools with metadata
GET    /api/tools/{name}    Get tool detail (parameters)

GET    /api/skills          List all skills
GET    /api/skills/{name}   Get skill markdown content
PUT    /api/skills/{name}   Update skill content
POST   /api/skills          Create new skill
DELETE /api/skills/{name}   Delete skill

GET    /api/config          Get configuration (secrets masked)
PUT    /api/config          Update configuration values
```

## Development

Run the frontend dev server with hot reload (proxies `/api` to the .NET backend on port 5100):

```bash
./sharpclaw.sh web
# or directly:
cd src/SharpClaw.Web && npm run dev
```

The dev server runs on `http://localhost:5173`.

## Production Build

Build the frontend bundle into `SharpClaw/wwwroot/` so the .NET app serves it as static files:

```bash
./sharpclaw.sh web-build
# or directly:
cd src/SharpClaw.Web && npm run build
```

The .NET app serves the SPA via `UseStaticFiles()` and `MapFallbackToFile("index.html")`.

## Project Structure

```
src/SharpClaw.Web/
├── index.html
├── vite.config.ts          # Proxy + build output config
├── package.json
├── tsconfig.json
└── src/
    ├── main.tsx            # React entry point
    ├── App.tsx             # Router with all routes
    ├── api/
    │   └── client.ts       # Base fetch wrapper
    ├── components/
    │   ├── SideMenu.tsx    # Permanent 240px drawer
    │   ├── MenuContent.tsx # Navigation items
    │   └── AppNavbar.tsx   # Mobile top bar
    ├── layouts/
    │   └── DashboardLayout.tsx
    ├── pages/
    │   ├── HomePage.tsx
    │   ├── AgentListPage.tsx
    │   ├── AgentEditorPage.tsx
    │   ├── McpListPage.tsx
    │   ├── McpEditorPage.tsx
    │   ├── ToolListPage.tsx
    │   ├── ToolDetailPage.tsx
    │   ├── SkillListPage.tsx
    │   ├── SkillEditorPage.tsx
    │   ├── ConfigPage.tsx
    │   └── ExamplesPage.tsx
    └── theme/
        └── index.ts        # Dark theme customisation
```
