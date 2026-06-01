---
sidebar_position: 8
---

# Scheduling

The scheduling system allows agents to create one-off or recurring tasks that execute automatically at specified times. Tasks can either invoke an agent with a prompt, or run a shell command directly. Results are delivered back to the originating channel (Telegram or web).

## How It Works

1. A user asks an agent something like _"Find me the top 5 news stories every Wednesday at 8am"_
2. The agent calls the `schedule_task` tool with a cron expression and prompt (or command)
3. SharpClaw persists the task as a `.task.md` file in the workspace
4. The `SchedulerWorker` background service checks for due tasks every 30 seconds
5. When a task is due:
   - **Agent tasks**: The agent runs the prompt and delivers the result
   - **Command tasks**: The shell command executes and success/failure is delivered
6. Results are delivered to the original channel (Telegram or web)

## Task Types

### Agent Tasks (default)

The traditional behaviour — runs a prompt through an agent and delivers the response.

### Command Tasks

Runs a shell command directly via `/bin/bash` without invoking an agent. Ideal for:

- Data collection (API calls, downloads)
- File maintenance (cleanup, rotation)
- Pre-fetching data that agents will summarise later

Command tasks report ✅ success or ❌ failure (with output/error) back to the channel. They have a 5-minute timeout.

## Tools

### `schedule_task`

Creates a new scheduled task.

| Parameter         | Type    | Required | Description                                                                   |
| ----------------- | ------- | -------- | ----------------------------------------------------------------------------- |
| `prompt`          | string  | Yes      | The prompt to send to the agent when the task fires                           |
| `cron_expression` | string  | Yes      | Standard 5-field cron expression (minute hour day-of-month month day-of-week) |
| `one_off`         | boolean | No       | If true, runs once then auto-deletes. Defaults to false (recurring)           |
| `description`     | string  | No       | Human-readable summary of the scheduled task                                  |
| `command`         | string  | No       | Shell command to execute. When provided, creates a command task instead of an agent task |

**Cron examples:**

| Expression    | Meaning                       |
| ------------- | ----------------------------- |
| `0 8 * * 3`   | Every Wednesday at 8:00 AM    |
| `0 7 * * 1-5` | Weekdays at 7:00 AM           |
| `30 9 1 * *`  | 1st of every month at 9:30 AM |
| `0 */4 * * *` | Every 4 hours                 |

**Command task example:**

```
schedule_task(
  prompt: "Fetch daily Anthropic usage data",
  cron_expression: "0 6 * * *",
  command: "curl -sH 'Authorization: Bearer $ANTHROPIC_ADMIN_KEY' https://api.anthropic.com/v1/... | jq -r ... >> /data/usage.csv",
  description: "Daily Anthropic API usage fetch"
)
```

### `cancel_task`

Cancels a previously scheduled task.

| Parameter | Type   | Required | Description                  |
| --------- | ------ | -------- | ---------------------------- |
| `task_id` | string | Yes      | The ID of the task to cancel |

## User Commands

| Command        | Description                     |
| -------------- | ------------------------------- |
| `.schedules`   | List all active scheduled tasks |
| `.cancel <id>` | Cancel a scheduled task by ID   |

## Persistence

Tasks are stored as individual `.task.md` files in `{workspace}/schedules/`. Each file uses YAML frontmatter:

```markdown
---
id: a1b2c3d4
agent: myles
type: agent
cron: "0 8 * * 3"
one_off: false
channel_key: "123456789"
channel_type: Telegram
created: 2026-05-06T10:00:00+00:00
next_run: 2026-05-07T08:00:00+00:00
enabled: true
description: "Top 5 news stories weekly"
---

Find me the top 5 news stories this week and summarise them.
```

A command task looks like:

```markdown
---
id: b2c3d4e5
agent: cody
type: command
cron: "0 6 * * *"
one_off: false
channel_key: "123456789"
channel_type: Telegram
created: 2026-06-01T10:00:00+00:00
next_run: 2026-06-02T06:00:00+00:00
enabled: true
description: "Daily Anthropic usage fetch"
command: "curl -s ... >> /data/usage.csv"
---

Fetch daily Anthropic usage data
```

Tasks survive service restarts — the `SchedulerWorker` reloads them from disk on startup.

## Architecture

```
┌─────────────┐     ┌──────────────────┐     ┌───────────────┐
│ Agent calls  │────▶│ ScheduleTaskTool │────▶│ ScheduleStore │
│ schedule_task│     └──────────────────┘     │ (.task.md)    │
└─────────────┘                               └───────┬───────┘
                                                      │
                                              ┌───────▼───────┐
                                              │SchedulerWorker│ (30s tick)
                                              └───────┬───────┘
                                                      │
                                        ┌─────────────┼─────────────┐
                                        │ type=agent  │ type=command │
                                        ▼             │             ▼
                                ┌──────────────┐     │     ┌──────────────┐
                                │ AgentRunner  │     │     │  /bin/bash   │
                                │ (one-shot)   │     │     │  (process)   │
                                └──────┬───────┘     │     └──────┬───────┘
                                       │             │            │
                                       └─────────────┼────────────┘
                                                     ▼
                                              ┌───────────────┐
                                              │ Delivery       │
                                              │ (Telegram/Web) │
                                              └───────────────┘
```

## Configuration

No additional configuration is required. The scheduler uses the existing `WorkspacePath` setting to locate the `schedules/` directory.

All times are in **UTC**. The cron expressions are evaluated against UTC time.

## Agent Setup

Add `schedule_task` and `cancel_task` to an agent's `tools` list in its frontmatter:

```yaml
tools:
  - schedule_task
  - cancel_task
```
