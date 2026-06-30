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
| `update_ticket` | `project_id`, `ticket_id`, `title`/`description`/`status` (optional) | Update a ticket. **Do not pass `description` when only changing status — use `add_comment` instead.** |
| `get_ticket` | `project_id`, `ticket_id` | Get full ticket details including comments |
| `list_comments` | `project_id`, `ticket_id` | List the comment thread for a ticket |
| `add_comment` | `project_id`, `ticket_id`, `comment`, `author` (optional) | Add a comment to the ticket thread |

### Ticket Statuses

| Status | Meaning |
|--------|---------|
| `idea` | Captured but not yet refined |
| `planning` | Being scoped or designed |
| `todo` | Ready to be picked up. If `assignee` matches a registered agent, the auto-dispatch worker will move it to `in_progress` and invoke that agent. |
| `in_progress` | Actively being worked on (set by the auto-dispatch worker, or by a human) |
| `blocked` | Cannot progress without external input; the blocking reason lives in a comment |
| `for_review` | Work is complete and awaiting human review; a comment carries the summary and PR link |
| `done` | Reviewed and accepted (humans only) |

### The Description Is Immutable

The ticket **description** is the original requirement. **Never modify it**
through `update_ticket` once a ticket exists. All status-related context —
blocking reasons, completion summaries, PR links, agent observations — belongs
in **comments**, not the description. This protects the original requirement
from being overwritten and gives every ticket a clean audit trail.

The only legitimate reasons to ever pass `description` to `update_ticket` are:
- A human explicitly asks you to refine the requirement itself.
- You created the ticket moments ago and need to correct your own draft.

If in doubt, add a comment instead.

### Comments as the Audit Trail

Before doing any substantive work on a ticket, **read the existing comments**
(`list_comments` or via `get_ticket`). They contain:

- Prior blocking reasons (when the ticket was last `blocked`)
- Any unblocking details a human has added
- Earlier agent observations or context handoffs

If a ticket was previously `blocked` and is now back in `todo`/`in_progress`,
assume the most recent comments explain what changed.

### Two Modes of Working on Tickets

You will encounter tickets in two distinct ways. The rules differ.

#### Mode A — Auto-dispatched (you were invoked by the worker)

When the `TicketAssignmentWorker` picks up a `todo` ticket assigned to you, it moves
it to `in_progress` and invokes you with a directive containing the ticket details
and existing comments. **Treat the conversation prompt as the directive — do the
work now.** You only get one turn, so you must reach a terminal state before
stopping:

- **Complete the work** → add a comment with a summary (and PR link, if applicable),
  then move it to `for_review`:

  ```
  ticket(action="add_comment", project_id="...", ticket_id="...",
         author="<your-agent-name>",
         comment="**Ready for review.** <summary>. PR: <url>")
  ticket(action="update_ticket", project_id="...", ticket_id="...", status="for_review")
  ```

- **Cannot proceed** (missing info, ambiguity, external dependency, error you
  cannot resolve) → add a comment explaining the blocker, then move to `blocked`:

  ```
  ticket(action="add_comment", project_id="...", ticket_id="...",
         author="<your-agent-name>",
         comment="**Blocked:** <clear explanation>")
  ticket(action="update_ticket", project_id="...", ticket_id="...", status="blocked")
  ```

**Never leave an auto-dispatched ticket in `in_progress`.** Finish or block.
**Never modify the ticket description** as part of a status transition.

#### Mode B — Interactive (a human is talking to you about a ticket)

The human drives status transitions. You may complete work and move tickets to
`for_review` if the human explicitly asks, but do not pick tickets up of your own
accord and do not move them to `in_progress` yourself — the worker (or a human)
does that.

### Status Transitions You Are Allowed

| From | To | When |
|------|----|------|
| `in_progress` | `for_review` | You completed the work (either mode); add summary comment first |
| `in_progress` | `blocked` | Auto-dispatch mode: you cannot proceed; add blocker comment first |
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
