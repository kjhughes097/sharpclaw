# SharpClaw V3 — Phase 3.6 Implementation Spec (approvals that fit the threat model)

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** **implemented** (tasks 1–5 in section 10).
**Scope:** cut the number of approval prompts without moving the trust boundary. Earlier specs
still apply wherever this one doesn't change them, in particular the risk model and single dispatch
path ([3.2](sharpclaw-3.2-implementation-spec.md) §3 and §7), approvals
([pre-3.3 foundations](sharpclaw-pre-3.3-foundations-spec.md) §9) and the tool broker
([external tools spec](sharpclaw-external-tools-spec.md) §4).

## 0. How to use this document

- Implement in the task order in section 10, each with passing tests.
- "MUST" / "MUST NOT" are hard requirements. If something is ambiguous, stop and ask.
- Never log, put in an event, or emit as a metric label any tool argument or result, message
  content, token or key.

### 0.1 Decisions

| # | Question | Decision |
|---|---|---|
| Q1 | Who makes automatic decisions? | **The gateway.** The agent's `ToolDispatcher` is unchanged: above its `allowedRiskLevel` (or for an always-approve tool) it still raises an approval. `POST /agent/approvals` then decides it at once when a grant or the agent's autonomy setting covers it. The row, the args-hash binding, the broker's one-time `ConsumeApproval` and the audit trail all work as before. An agent can never grant itself anything. |
| Q2 | Should reading the agent's own workspace taint the turn? | **No, by default.** `workspace_read` and `workspace_search` no longer taint unless `agent.json` sets `taintWorkspaceReads: true`. Results from the web, GitHub, `ask_agent` and `sandbox_run` still taint. |
| Q3 | Default policy | The template agent gets `allowedRiskLevel: medium`. `sandbox_run` leaves `ToolBroker:AlwaysApprove`'s default (now `github_write`, `github_create_pr`) and is scored **medium** instead of high. |
| Q4 | What does a grant match? | **Agent + tool name**, at any risk. Hardline (`Blocked`) rules still apply. |
| Q5 | Grant scopes | **Thread** (calls in one thread) or **agent** (all threads), each optionally **time-boxed** (1, 8 or 24 hours). Grants last until they expire or are revoked. |
| Q6 | Grants for GitHub writes | **Not allowed.** `github_write` and `github_create_pr` act as the user; only autonomy mode auto-approves them. |
| Q7 | Autonomy mode | A per-agent flag (`agents.autonomous`), off by default, set through `PATCH /api/agents/{id}` (so the web UI and admin key). While it's on, the gateway approves **every** approval the agent raises, Critical included. Blocked calls still never run. |
| Q8 | Unattended runs | Grants and autonomy **also apply** to unattended messages (scheduled runs and unattended ask chains). An approval nothing covers is still denied at once. |
| Q9 | Batch approval | `POST /api/approvals/batch` decides every pending approval in a list of ids or a thread in one call. |
| Q10 | How are auto-decisions shown? | `decidedBy` gains `grant` and `autonomy`. The approval carries `grantId` when a grant decided it. Only a durable `approval.decided` event is published (no `approval.requested`), so no card flashes up in the UI. |

## 1. Goal

The operator runs agents whose tools are sandboxed, whose every outbound request passes through the
gateway, whose model spend is capped per agent per day, and which hold none of the operator's
secrets. In that setting, asking a human before every edit of a file the agent wrote itself, or
before every test run in its own sandbox, costs attention and buys little. After this phase most
routine turns need no prompt, and the operator can say "yes, and stop asking" at three widths:
this thread, this agent, or this agent for a while.

## 2. Hard rules

- Every tool call still goes through `ToolDispatcher` (hardline → risk → taint → approval →
  execute → report). Nothing in this phase lets an agent skip raising an approval it needs.
- The gateway alone stores and evaluates grants and autonomy. Agents MUST NOT be able to read,
  create or change them (no `/agent/*` endpoint touches them).
- Gateway hardline refusals in `ApprovalDetails.PrepareAsync` (e.g. a PR that touches a blocked
  path) are checked **before** any grant or autonomy, so they are never auto-approved.
- Grants for tools in `BrokerTools.NonGrantable` MUST be refused with `400 validation_failed`.

## 3. Taint (task 1)

- `IAgentTool` gains `bool ReadsOwnWorkspace => false`. `workspace_read` and `workspace_search` return true.
- `AgentToolConfig` gains `TaintWorkspaceReads` (`taintWorkspaceReads` in `agent.json`, boolean,
  default `false`).
- The dispatcher taints the turn after a successful call when
  `tool.ReturnsUntrustedContent && (!tool.ReadsOwnWorkspace || config.TaintWorkspaceReads)`.
- Accepted risk (recorded in tech debt): content that arrived in the workspace through a download,
  a clone or the sandbox, and is read in a **later** call, no longer raises risk.

## 4. Defaults (task 1)

- `docker/template/agents/assistant/config/agent.json`: `allowedRiskLevel` → `medium`.
- `BrokerTools.DefaultAlwaysApprove` → `github_write`, `github_create_pr`. An operator who wants
  the old behaviour sets `ToolBroker__AlwaysApprove__2=sandbox_run` (or lists all three).
- `sandbox_run` is scored `medium` ("runs code in the sandbox"). With the default level it runs
  without a prompt. After untrusted content in the same turn it's raised to high and asks, unless
  a grant or autonomy covers it.

## 5. Data (task 2)

New table `approval_grants`:

| Column | Type | Notes |
|---|---|---|
| `id` | guid | v7 |
| `agent_id` | guid | FK → agents |
| `tool_name` | text | |
| `thread_id` | guid? | set for thread scope, null for agent scope |
| `expires_at` | datetime? | UTC; null = until revoked |
| `created_by_user` | text? | user name, or `admin_key` |
| `created_at` | datetime | UTC |
| `revoked_at` | datetime? | UTC |

Index on `(agent_id, tool_name)`. New columns: `approvals.grant_id` (guid?) and
`agents.autonomous` (bool, default false). One EF migration: `ApprovalGrantsAndAutonomy`.

A grant is **active** at time `t` when `revoked_at is null && (expires_at is null || expires_at > t)`.

## 6. Gateway decisions (task 2)

`POST /agent/approvals`, after validation and `ApprovalDetails.PrepareAsync`:

1. If the agent is `autonomous`: status `approved`, `decided_by = autonomy`.
2. Else if an active grant exists for (agent, tool) with `thread_id` null or equal to the
   approval's thread, and the tool isn't in `NonGrantable`: status `approved`, `decided_by = grant`,
   `grant_id` set. When several match, the earliest-created grant wins.
3. Else if the message is unattended: denied by `system` (unchanged).
4. Else pending (unchanged).

Auto-decided rows set `decided_at = created_at`. The write publishes `approval.decided` (with
`decidedBy`) and no `approval.requested`. The notifier isn't called. Metrics: `approvals` and
`approval.decision_time` gain a `type` label holding `decided_by` (`user`, `system`, `grant`,
`autonomy`). The log line `approval requested` gains `decided_by`.

## 7. Client API (task 3)

- `POST /api/approvals/{id}` body: `{ "decision": "approved" | "denied", "remember"?: { "scope": "thread" | "agent", "ttlHours"?: 1 | 8 | 24 } }`.
  `remember` is only valid with `approved`. It is refused with `400 validation_failed` for a non-grantable tool, or when
  `ttlHours` isn't one of the allowed values. The grant is created only when the decision wins (not on `409`). The
  response is unchanged.
- `GET /api/agents/{idOrName}/grants` → `{ grants: ApprovalGrantDto[] }`: the active grants, newest first.
  `ApprovalGrantDto(id, toolName, scope, threadId?, expiresAt?, createdByUser?, createdAt)`.
- `DELETE /api/agents/{idOrName}/grants/{grantId}` → `204`. Sets `revoked_at`. Unknown, foreign or
  already-revoked ids → `404`.
- `PATCH /api/agents/{idOrName}` accepts `autonomous` (bool). `AgentDto` gains `autonomous`. The
  change is logged with the actor name.
- `POST /api/approvals/batch` body: `{ "decision", "ids"?: guid[], "threadId"?: guid }`, with exactly one of
  `ids` (1–100) or `threadId`. It decides each pending approval it finds (expiring overdue ones first, as
  single decide does) and returns `{ decided: ApprovalDto[], skipped: int }`. `skipped` counts ids
  that were unknown or no longer pending.
- `ApprovalDto` and `ApprovalDetailDto` gain `grantId`.

All of these are client-listener endpoints (admin key or session + CSRF).

## 8. Web UI and CLI (tasks 4–5)

- **Approval card:** "Approve" plus a menu: *for this thread*, *for this agent*, *for this agent, 1 h / 8 h / 24 h*.
  The menu is hidden for non-grantable tools. "Deny" is unchanged.
- **Approvals page:** "Approve all" for the pending list shown (uses `batch` with ids).
- **Agent page:** an "Autonomous" switch with a warning ("approves every tool call this agent
  makes, including GitHub writes"), and a "Standing approvals" list with revoke.
- **History:** `grant` and `autonomy` decisions show as "auto (grant)" / "auto (autonomy)".
- **CLI:** `sharpclaw approvals approve <id> --remember thread|agent [--ttl 1|8|24]`.

## 9. Out of scope

Argument- or path-matching grants. Grants created from the agent side. Per-tool autonomy.
Changing `allowedRiskLevel` at runtime. Notifications about auto-decisions.

## 10. Tasks

1. Taint and defaults (sections 3, 4): agent and contracts changes, template, tests.
2. Schema and gateway decisions (sections 5, 6): entities, migration, `Create`, metrics, tests.
3. Client API (section 7): remember, grants list/revoke, autonomy patch, batch, OpenAPI, tests.
4. Web UI (section 8): schema regen, card, approvals page, agent page, tests.
5. CLI and docs: `--remember`, README, telemetry notes, tech debt.
