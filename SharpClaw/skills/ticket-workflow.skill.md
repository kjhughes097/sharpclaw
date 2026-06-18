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

### Ticket Statuses

| Status | Meaning |
|--------|---------|
| `idea` | Captured but not yet refined |
| `planning` | Being scoped or designed |
| `todo` | Ready to be picked up. If `assignee` matches a registered agent, the auto-dispatch worker will move it to `in_progress` and invoke that agent. |
| `in_progress` | Actively being worked on (set by the auto-dispatch worker, or by a human) |
| `blocked` | Cannot progress without external input; the blocking reason is appended to the description |
| `for_review` | Work is complete and awaiting human review |
| `done` | Reviewed and accepted (humans only) |

### Two Modes of Working on Tickets

You will encounter tickets in two distinct ways. The rules differ.

#### Mode A — Auto-dispatched (you were invoked by the worker)

When the `TicketAssignmentWorker` picks up a `todo` ticket assigned to you, it moves
it to `in_progress` and invokes you with a directive containing the ticket details.
**Treat the conversation prompt as the directive — do the work now.** You only get
one turn, so you must reach a terminal state before stopping:

- **Complete the work** → append a summary (and PR link, if applicable) to the
  ticket description, then move it to `for_review`:
  `ticket(action="update_ticket", project_id="...", ticket_id="...", status="for_review")`
- **Cannot proceed** (missing info, ambiguity, external dependency, error you
  cannot resolve) → append a clear explanation of the blocker to the ticket
  description and move it to `blocked`:
  `ticket(action="update_ticket", project_id="...", ticket_id="...", status="blocked")`

**Never leave an auto-dispatched ticket in `in_progress`.** Finish or block.

#### Mode B — Interactive (a human is talking to you about a ticket)

The human drives status transitions. You may complete work and move tickets to
`for_review` if the human explicitly asks, but do not pick tickets up of your own
accord and do not move them to `in_progress` yourself — the worker (or a human)
does that.

### Status Transitions You Are Allowed

| From | To | When |
|------|----|------|
| `in_progress` | `for_review` | You completed the work (either mode) |
| `in_progress` | `blocked` | Auto-dispatch mode: you cannot proceed; reason appended to description |
| *(new ticket)* | `idea` | User asked you to create a ticket |

### Status Transitions You Must NOT Make

- Do **not** move tickets to `in_progress` yourself — only the worker (on a `todo`
  with a matching assignee) or a human may do this.
- Do **not** move tickets to `planning` or `done` — those are human-only.
- Do **not** change the status of unrelated tickets (anything other than the one
  you are currently working on or being asked about).

### Creating Tickets

- You may **create new tickets** with status `idea` if asked to by the user.
- Do not create tickets in any other status.
