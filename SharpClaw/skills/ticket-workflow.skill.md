---
description: Rules for how agents interact with the project and ticket tracking system
---

## Project & Ticket System

You have two tools for managing work: `project` and `ticket`.

### The `project` tool

| Action | Parameters | Purpose |
|--------|-----------|---------|
| `list_projects` | — | List all projects with ticket summaries |
| `create_project` | `title`, `description` (optional) | Create a new project |
| `get_project` | `project_id` | Get project details and its tickets |

### The `ticket` tool

| Action | Parameters | Purpose |
|--------|-----------|---------|
| `list_tickets` | `project_id` | List all tickets in a project |
| `create_ticket` | `project_id`, `title`, `description` (optional), `status` (optional) | Create a ticket |
| `update_ticket` | `project_id`, `ticket_id`, `title`/`description`/`status` (optional) | Update a ticket |
| `get_ticket` | `project_id`, `ticket_id` | Get full ticket details |

### Ticket Workflow Rules

- **Only work on tickets with status `in_progress`**. A human moves tickets to this status when they are ready for you to pick up.
- Do not start work on tickets in `idea`, `planning`, or `for_review` status.

### Completing Work

- When you have completed the work described in a ticket, **move it to `for_review`** status.
- Do not move tickets to `done` — a human will do that after reviewing your work.

### Creating Tickets

- You may **create new tickets** with status `idea` if asked to by the user.
- Do not create tickets in any other status.

### Status Transitions You Are Allowed

| From | To | When |
|------|----|------|
| `in_progress` | `for_review` | You completed the work |
| *(new ticket)* | `idea` | User asked you to create a ticket |

### Status Transitions You Must NOT Make

- Do not move tickets to `in_progress`, `planning`, or `done`.
- Do not change the status of tickets that are not `in_progress` (except creating new ones as `idea`).
