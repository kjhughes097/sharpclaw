# JIRA Integration Skill

You have access to a JIRA integration skill that can create and fetch tickets via the JIRA REST API.

## Required Environment Variables

The following must be set at runtime:

- `JIRA_USER_EMAIL` — The user's JIRA email address (used for Basic Auth)
- `JIRA_API_KEY` — The JIRA API token (generate at https://id.atlassian.net/manage-profile/security/api-tokens)
- `JIRA_BASE_URL` — The JIRA instance URL (e.g., `https://mycompany.atlassian.net`)

## Available Commands

### `create` — Create a JIRA ticket

Creates a ticket in a specified project, optionally using a project-specific template for default field values.

**Usage:**
```bash
./run.sh create --project <PROJECT_KEY> --summary "<summary>" [options]
```

**Arguments:**
| Flag | Required | Description |
|------|----------|-------------|
| `--project` | Yes | JIRA project key (e.g., `ENG`, `PLATFORM`) |
| `--summary` | Yes | Ticket summary/title |
| `--description` | No | Ticket description (plain text or ADF JSON) |
| `--type` | No | Issue type override (e.g., `Bug`, `Story`, `Task`) |
| `--priority` | No | Priority override (e.g., `High`, `Medium`, `Low`) |
| `--labels` | No | Comma-separated labels (e.g., `backend,urgent`) |
| `--components` | No | Comma-separated component names |
| `--assignee` | No | Assignee account ID or email |
| `--sprint` | No | Sprint ID to add the ticket to |

**Template behaviour:**
- If a file `templates/<PROJECT_KEY>.json` exists, its fields are used as defaults.
- Any flags provided on the command line override template values.
- If no template exists, you must provide at least `--type` (defaults to `Task` otherwise).

**Examples:**
```bash
# Create a task using template defaults
./run.sh create --project ENG --summary "Add rate limiting to API gateway"

# Create a bug with overrides
./run.sh create --project ENG --summary "Login fails on Safari" --type Bug --priority High --labels "browser,auth"

# Create with full description
./run.sh create --project PLATFORM --summary "Migrate to PostgreSQL 17" --description "Upgrade from PG 15 to PG 17 for improved JSON performance."
```

---

### `fetch` — Fetch JIRA tickets

Retrieves tickets from a project/sprint with optional filters.

**Usage:**
```bash
./run.sh fetch --project <PROJECT_KEY> [options]
```

**Arguments:**
| Flag | Required | Description |
|------|----------|-------------|
| `--project` | Yes | JIRA project key |
| `--sprint` | No | Sprint name or ID (uses active sprint if omitted) |
| `--assignee` | No | Filter by assignee (account ID or email) |
| `--label` | No | Filter by label |
| `--status` | No | Filter by status (e.g., `"In Progress"`, `Done`) |
| `--max` | No | Maximum results to return (default: 50) |

**Examples:**
```bash
# All tickets in the active sprint
./run.sh fetch --project ENG

# Tickets assigned to a specific user
./run.sh fetch --project ENG --assignee "john@company.com"

# Tickets with a specific label in a named sprint
./run.sh fetch --project ENG --sprint "Sprint 42" --label backend

# Filter by status
./run.sh fetch --project ENG --status "In Progress"
```

**Output format:**
Returns a markdown-formatted table with columns: Key, Summary, Status, Assignee, Priority, Labels.

---

## Template Format

Templates live in `skills/jira-skill/templates/<PROJECT_KEY>.json`. Each template defines default field values for ticket creation in that project.

**Structure:**
```json
{
  "project_key": "EXAMPLE",
  "issue_type": "Story",
  "priority": "Medium",
  "labels": ["team-alpha"],
  "components": ["backend"],
  "custom_fields": {
    "customfield_10001": "value"
  }
}
```

**To add a new project template:**
1. Create a JSON file named `<PROJECT_KEY>.json` in the `templates/` directory.
2. Define any default fields. Only include fields you want as defaults.
3. All fields can be overridden at creation time via command-line flags.

## Invocation

The skill script is located at `skills/jira-skill/run.sh`. Invoke it from the SharpClaw root:

```bash
bash skills/jira-skill/run.sh <command> [flags...]
```

Or if calling from the skill directory:

```bash
./run.sh <command> [flags...]
```
