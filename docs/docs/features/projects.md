---
sidebar_position: 15
---

# Projects & Ticket Tracking

SharpClaw includes a lightweight, file-based project and ticket tracking system. Projects and tickets are stored as markdown files with YAML frontmatter, making them easy to create, edit, and version-control.

## Directory Structure

Projects live in the configured `ProjectsDirectory` (default: `projects/` relative to the app root):

```
projects/
├── my-project/
│   ├── project.md          # Project definition
│   └── tickets/
│       ├── 001.md          # First ticket
│       ├── 002.md          # Second ticket
│       └── 003.md          # Third ticket
└── another-project/
    ├── project.md
    └── tickets/
        └── 001.md
```

## File Formats

### Project File (`project.md`)

```markdown
---
title: My Project
created_at: 2026-05-23T13:45:00Z
---

Optional description of the project in markdown.
```

| Field        | Type               | Required | Description                         |
| ------------ | ------------------ | -------- | ----------------------------------- |
| `title`      | string             | Yes      | Human-readable project name         |
| `created_at` | ISO 8601 timestamp | Yes      | When the project was created        |
| Body         | markdown           | No       | Project description                 |

The directory name becomes the **project ID** (slug). For example, `projects/my-project/` has ID `my-project`.

### Ticket File (`{number}.md`)

```markdown
---
title: Implement user authentication
status: in_progress
created_at: 2026-05-23T13:46:00Z
updated_at: 2026-05-23T14:30:00Z
---

Detailed description of the ticket in markdown.

## Acceptance Criteria

- [ ] Users can log in
- [ ] Sessions persist across restarts
```

| Field        | Type               | Required | Description                              |
| ------------ | ------------------ | -------- | ---------------------------------------- |
| `title`      | string             | Yes      | Ticket title                             |
| `status`     | string             | Yes      | One of the valid statuses (see below)    |
| `created_at` | ISO 8601 timestamp | Yes      | When the ticket was created              |
| `updated_at` | ISO 8601 timestamp | Yes      | When the ticket was last modified        |
| Body         | markdown           | No       | Full ticket description                  |

The filename (without `.md`) is the **ticket ID** — a zero-padded number like `001`, `002`, etc. New tickets auto-increment from the highest existing number.

### Ticket Statuses

| Status        | Frontmatter value | Description                        |
| ------------- | ----------------- | ---------------------------------- |
| Idea          | `idea`            | Captured but not yet planned       |
| Planning      | `planning`        | Being scoped and specified         |
| Todo          | `todo`            | Planned and ready to be picked up  |
| In Progress   | `in_progress`     | Actively being worked on           |
| Blocked       | `blocked`         | Cannot progress (external blocker) |
| For Review    | `for_review`      | Complete, awaiting review          |
| Done          | `done`            | Finished                           |

## Chat Commands

Two dot-commands provide quick access to project and ticket information:

### `.projects`

Lists all projects with their ticket counts.

```
> .projects
Projects:
• **My Project** (`my-project`) — 5 tickets
• **Another Project** (`another-project`) — 2 tickets
```

### `.tickets`

Lists tickets, optionally filtered by project and status.

| Syntax                           | Description                                |
| -------------------------------- | ------------------------------------------ |
| `.tickets`                       | Lists all tickets across all projects      |
| `.tickets my-project`            | Lists all tickets in `my-project`          |
| `.tickets my-project in_progress`| Lists only in-progress tickets in a project|

```
> .tickets my-project
Tickets in **My Project**:
• `001` [planning] Fix SDK header error
• `002` [in_progress] Add authentication
• `003` [done] Write documentation
```

## Agent Tools

Two tools are available to agents for programmatic project and ticket management.

### `project` Tool

Manage projects from within agent conversations.

| Parameter     | Type   | Required | Description                                     |
| ------------- | ------ | -------- | ----------------------------------------------- |
| `action`      | string | Yes      | `list_projects`, `create_project`, `get_project` |
| `project_id`  | string | No       | Project ID slug (required for `get_project`)    |
| `title`       | string | No       | Project title (required for `create_project`)   |
| `description` | string | No       | Project description (optional for `create_project`) |

**Actions:**

- **`list_projects`** — Returns all projects with ticket status breakdowns.
- **`create_project`** — Creates a new project directory with `project.md`. The ID is auto-generated from the title (slugified).
- **`get_project`** — Returns full project details including all tickets.

### `ticket` Tool

Manage tickets from within agent conversations.

| Parameter           | Type   | Required | Description                                           |
| ------------------- | ------ | -------- | ----------------------------------------------------- |
| `action`            | string | Yes      | `list_tickets`, `create_ticket`, `update_ticket`, `get_ticket`, `move_ticket`, `delete_ticket` |
| `project_id`        | string | Yes      | Project ID slug                                       |
| `ticket_id`         | string | No       | Ticket ID, e.g. `001` (required for `get_ticket`, `update_ticket`, `move_ticket`, `delete_ticket`) |
| `title`             | string | No       | Ticket title (required for `create_ticket`)           |
| `description`       | string | No       | Ticket description in markdown                        |
| `status`            | string | No       | New status (for `update_ticket`)                      |
| `target_project_id` | string | No       | Target project ID (required for `move_ticket`)        |

**Actions:**

- **`list_tickets`** — Lists tickets in a project, optionally filtered by status.
- **`create_ticket`** — Creates a new ticket with auto-incremented ID. Default status is `planning`.
- **`update_ticket`** — Updates a ticket's title, description, and/or status.
- **`get_ticket`** — Returns full ticket details including description.
- **`move_ticket`** — Moves a ticket from one project to another. The ticket retains its ID and all metadata.
- **`delete_ticket`** — Soft-deletes a ticket by moving it to a `.deleted/` folder within the project's tickets directory. The ticket is removed from all lists but can be recovered from disk.

### Example: Agent Workflow

An agent can manage tickets as part of its task execution:

```
Agent: I'll create a ticket for the new feature.

[tool call: ticket]
  action: create_ticket
  project_id: my-project
  title: Add rate limiting to API
  description: Implement rate limiting middleware...

→ Created ticket `004` in project 'my-project': Add rate limiting to API

[tool call: ticket]
  action: update_ticket
  project_id: my-project
  ticket_id: 004
  status: in_progress

→ Updated ticket `004`: [in_progress] Add rate limiting to API
```

## REST API

All endpoints are under `/api/projects`. No authentication is required.

### List Projects

```http
GET /api/projects
```

**Response** `200 OK`:
```json
[
  {
    "id": "my-project",
    "title": "My Project",
    "description": "Project description",
    "createdAt": "2026-05-23T13:45:00+00:00",
    "ticketCount": 5
  }
]
```

### Get Project

```http
GET /api/projects/{projectId}
```

**Response** `200 OK`:
```json
{
  "id": "my-project",
  "title": "My Project",
  "description": "Project description",
  "createdAt": "2026-05-23T13:45:00+00:00",
  "tickets": [
    {
      "id": "001",
      "title": "First ticket",
      "status": "planning",
      "createdAt": "2026-05-23T13:46:00+00:00",
      "updatedAt": "2026-05-23T13:46:00+00:00"
    }
  ]
}
```

**Response** `404 Not Found` if project doesn't exist.

### Create Project

```http
POST /api/projects
Content-Type: application/json

{
  "title": "My New Project",
  "description": "Optional description"
}
```

**Response** `201 Created`:
```json
{
  "id": "my-new-project",
  "title": "My New Project",
  "description": "Optional description",
  "createdAt": "2026-05-23T14:00:00+00:00"
}
```

**Errors:**
- `400 Bad Request` — Title is missing
- `409 Conflict` — Project with that slug already exists

### List Tickets

```http
GET /api/projects/{projectId}/tickets
GET /api/projects/{projectId}/tickets?status=in_progress
```

**Response** `200 OK`:
```json
[
  {
    "id": "001",
    "projectId": "my-project",
    "title": "First ticket",
    "description": "Full markdown description",
    "status": "planning",
    "createdAt": "2026-05-23T13:46:00+00:00",
    "updatedAt": "2026-05-23T13:46:00+00:00"
  }
]
```

### Get Ticket

```http
GET /api/projects/{projectId}/tickets/{ticketId}
```

**Response** `200 OK` — Same shape as a single item in the list response.

**Response** `404 Not Found` if ticket doesn't exist.

### Create Ticket

```http
POST /api/projects/{projectId}/tickets
Content-Type: application/json

{
  "title": "New ticket title",
  "description": "Optional markdown description"
}
```

**Response** `201 Created`:
```json
{
  "id": "004",
  "projectId": "my-project",
  "title": "New ticket title",
  "description": "Optional markdown description",
  "status": "planning",
  "createdAt": "2026-05-23T14:00:00+00:00",
  "updatedAt": "2026-05-23T14:00:00+00:00"
}
```

**Errors:**
- `400 Bad Request` — Title is missing or project not found

### Update Ticket

```http
PATCH /api/projects/{projectId}/tickets/{ticketId}
Content-Type: application/json

{
  "title": "Updated title",
  "description": "Updated description",
  "status": "in_progress"
}
```

All fields are optional — include only what you want to change.

**Response** `200 OK` — Returns the updated ticket.

**Response** `404 Not Found` if ticket doesn't exist.

### Move Ticket

```http
POST /api/projects/{projectId}/tickets/{ticketId}/move
Content-Type: application/json

{
  "targetProjectId": "another-project"
}
```

Moves a ticket from one project to another, preserving the ticket ID and all metadata.

**Response** `200 OK` — Returns the ticket with updated `projectId`.

**Errors:**
- `400 Bad Request` — Target project ID missing, same as current, or target project not found
- `404 Not Found` — Ticket doesn't exist

### Delete Ticket

```http
DELETE /api/projects/{projectId}/tickets/{ticketId}
```

Soft-deletes a ticket by moving it to a `.deleted/` subdirectory within the project's tickets folder. The file is preserved on disk for recovery but removed from all API responses and UI views.

**Response** `204 No Content` — Ticket was deleted.

**Response** `404 Not Found` — Ticket doesn't exist.

### Improve Ticket (AI-powered)

```http
POST /api/projects/{projectId}/tickets/{ticketId}/improve
```

Uses an LLM to rewrite and improve the ticket description, making it clearer and more actionable. Returns the improved description without saving it (the client can preview and save separately).

**Response** `200 OK`:
```json
{
  "description": "Improved markdown description..."
}
```

**Errors:**
- `404 Not Found` — Ticket doesn't exist
- `502 Bad Gateway` — LLM call failed

## Web UI — Kanban Board

The **Projects** page in the web UI (`/projects`) displays a Kanban board with drag-and-drop ticket management:

- **Columns** represent statuses: Idea → Planning → Todo → In Progress → For Review → Done
- **Cards** show the ticket number, title, and a colour-coded project chip
- **Drag and drop** tickets between columns to change status (desktop)
- **Click** a card to open an editor dialog with the full markdown description
- **Change status** via the Status dropdown in the edit dialog (works on touch devices where native drag-and-drop is unavailable)
- **Move tickets** between projects using the Project dropdown in the edit dialog
- **Delete tickets** via the Delete button in the edit dialog (with confirmation prompt)
- **Filter** by project using the chips at the top
- **Improve** button uses AI to enhance the ticket description

### Mobile

The board is responsive and works on phones and tablets:

- Columns scroll horizontally with snap-to-column behaviour on small screens
- Ticket dialogs open full-screen on mobile for easier editing
- Touch users move tickets between columns by tapping a card and changing the Status dropdown (HTML5 drag-and-drop is disabled on mobile because it is not reliably supported)

## Configuration

The projects directory is configured in `appsettings.json`:

```json
{
  "SharpClaw": {
    "ProjectsDirectory": "projects"
  }
}
```

Relative paths are resolved from the application's content root.

## Getting Started

### 1. Create a Project

Using the chat command or API:

```
> Ask the agent: "Create a project called My App"
```

Or via the REST API:

```bash
curl -X POST http://localhost:5100/api/projects \
  -H "Content-Type: application/json" \
  -d '{"title": "My App", "description": "A new application"}'
```

Or create the directory structure manually:

```bash
mkdir -p projects/my-app/tickets
cat > projects/my-app/project.md << 'EOF'
---
title: My App
created_at: 2026-05-23T00:00:00Z
---

Description of the project.
EOF
```

### 2. Create Tickets

```bash
curl -X POST http://localhost:5100/api/projects/my-app/tickets \
  -H "Content-Type: application/json" \
  -d '{"title": "Set up CI pipeline", "description": "Configure GitHub Actions for build and test"}'
```

### 3. Track Progress

Use the Kanban board in the web UI, chat commands, or agent tools to move tickets through statuses as work progresses.

### 4. Integration with Agents

Agents with access to the `project` and `ticket` tools can autonomously create, update, and query tickets as part of their workflows. For example, a planning agent could break down a feature request into tickets, or a coding agent could update ticket status as it completes work.

## Automatic Ticket Assignment

A background worker (`TicketAssignmentWorker`) periodically scans every project for tickets in `todo` status whose `assignee` matches the name of a registered agent. When it finds one, it:

1. Moves the ticket to `in_progress`.
2. Invokes the assigned agent with a directive describing the ticket and asking it to either complete the work (transition to `for_review`) or block it with a reason (transition to `blocked`).

Each agent processes at most one ticket per tick. If multiple tickets are assigned to the same agent, the rest are picked up on subsequent ticks. While an agent is processing a ticket it is locked out of further work, so a long-running task does not get re-dispatched.

Safety net:

- If the agent's invocation fails (LLM error, exception, timeout) the worker moves the ticket to `blocked` and appends the reason to the description.
- If the agent finishes its turn without transitioning the ticket out of `in_progress`, the worker moves it to `blocked` with a note explaining that human review is required.

For the worker to act on a ticket, the assigned agent must have the `ticket` tool available (either explicitly in its frontmatter `tools` list, or by omitting `tools` so it inherits all registered tools).

### Configuration

```json
"TicketWorker": {
  "Enabled": true,
  "PollingIntervalSeconds": 60
}
```

| Setting                  | Default | Description                                                                 |
| ------------------------ | ------- | --------------------------------------------------------------------------- |
| `Enabled`                | `true`  | Disable the worker entirely without removing it from the host.              |
| `PollingIntervalSeconds` | `60`    | How often to scan for newly-assigned `todo` tickets. Minimum effective: 5s. |

## Ticket Comments

Each ticket supports a thread of comments — useful for clarifying questions, recording blockers, agent observations, review feedback, or links to PRs.

Comments are stored as JSON files (one per ticket) under `{workspace}/ticket-comments/{ticketId}.json`. Because ticket IDs are globally unique, the comments survive moving a ticket between projects. They are deleted automatically when the parent ticket is deleted.

### Fields

| Field      | Description                                              |
|------------|----------------------------------------------------------|
| `id`       | 12-char generated comment ID                             |
| `ticketId` | Parent ticket ID                                         |
| `author`   | Name of the user or agent that wrote the comment         |
| `content`  | Comment text                                             |
| `created`  | Creation timestamp (UTC)                                 |
| `updated`  | Last-edit timestamp (UTC), `null` if never edited        |

### API Endpoints

| Method | Path                                                                  | Description                          |
|--------|-----------------------------------------------------------------------|--------------------------------------|
| GET    | `/api/projects/{projectId}/tickets/{ticketId}/comments`               | List comments (oldest first)         |
| POST   | `/api/projects/{projectId}/tickets/{ticketId}/comments`               | Add a comment (`{author, content}`)  |
| PUT    | `/api/projects/{projectId}/tickets/{ticketId}/comments/{commentId}`   | Edit a comment (author must match)   |
| DELETE | `/api/projects/{projectId}/tickets/{ticketId}/comments/{commentId}?author=…` | Delete a comment (author must match) |

Edits and deletes require the request to supply the same `author` as the original comment — this is lightweight ownership without coupling to an auth system.

### UI

Comments appear in a panel beneath the description editor inside the ticket edit dialog. The panel:

- Lists comments oldest-first with author chip and timestamp
- Visually distinguishes agent comments (filled primary chip) from human comments
- Shows an `(edited)` marker with tooltip when a comment has been updated
- Provides inline edit/delete controls (only enabled for the current author)
- Persists the author name in `localStorage` so it carries between sessions
- Confirms deletion via a dialog
