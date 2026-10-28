# SharpClaw V3 — Phase 3.2 Implementation Spec

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** ready to implement. This document is the source of truth for Phase 3.2. Anything not
described here is out of scope (section 14). The Phase 3.1 spec still applies wherever this
document does not change it.

> **Superseded in part** by the [pre-3.3 foundations spec](sharpclaw-pre-3.3-foundations-spec.md)
> (built): tool exchanges are now stored in thread history as `tool_request` / `tool_result` rows
> and replayed to the model, except untrusted results from earlier turns (§5 there, replacing §7
> "only the final reply is stored"); approvals stay `pending` when nobody is watching unless the
> message was posted `unattended`, a sweeper expires them, and a client queue API lists them
> (§9, replacing §8.2's "no subscriber → denied").

## 0. How to use this document

- Implement in the task order given in section 15. Each task is one branch and one pull request,
  with passing tests, before the next task starts.
- Where this spec says "MUST" or "MUST NOT", treat it as a hard requirement.
- If something is ambiguous or contradictory, stop and ask the human rather than guessing.
- Never log or emit as a metric label any prompt, reply, tool argument, tool result, soul contents,
  token or key.

## 1. Goal and approach

**Goal:** an agent can use tools safely. The agent decides whether each tool call is allowed, based
on the call's arguments and an assessed risk. When the risk is above the agent's allowed level, the
agent asks the human for permission. Every tool call is audited.

**Approach** (modelled on Hermes Agent and GitHub Copilot, both of which keep the decision in the
agent's own process and isolate side effects in a sandbox):

| Responsibility | Where | Enforced by |
|---|---|---|
| Tool loop, tool registry, risk scoring, approval decision, tool execution | **Agent** | The agent's single dispatch path (section 7) |
| Containing the effects of tool execution | **Agent container** | Docker isolation (internal network, read-only rootfs, read-only `/config`, only its own `/workspace`) |
| LLM access, budgets, approval brokering, independent audit of tool calls, loop backstop | **Gateway** | The gateway (agents cannot bypass it) |

The gateway does **not** decide whether a tool call is allowed. It sees every tool call the model
requests (they flow through `/v1/chat/completions`) and records it independently, so an agent that
fails to report a call is visible.

### Threat model

- **Hijacked model** (prompt injection from content the agent reads, or a malicious user message):
  the realistic threat. The model can only *request* tool calls; the agent's dispatcher decides. The
  dispatcher is the single execution path, so a hijacked model cannot bypass the policy.
- **Compromised agent code** (an attacker running code in the container): contained by Docker
  isolation. To keep this threat unlikely, the agent MUST NOT have any tool that executes code or
  shell commands (section 6).

## 2. Exit criteria

- An agent completes a multi-step task using tools (e.g. write a file, read it back, summarise).
- A tool call whose risk is above the agent's allowed level prompts the user in the CLI, and runs
  only if the user approves.
- A denied, expired or blocked call is not executed, is returned to the model as an error, and is
  recorded in the tool-call audit.
- With nobody watching the thread, an approval is denied immediately.
- The agent loop is bounded (agent iteration cap and gateway backstop).

## 3. Risk model

Four ordered levels: `low < medium < high < critical`.

Each tool scores its own calls from their arguments and returns one of:
- `Risk(level, reason)` — a confident score.
- `Uncertain(reason)` — the rules cannot score it confidently.
- `Blocked(reason)` — a hardline rule; never executed, never offered for approval.

**Smart mode** (Hermes-style), configured per agent:
- On: an `Uncertain` call is classified by an auxiliary LLM call through the gateway (section 8.4).
- Off, or the classifier's answer cannot be parsed: an `Uncertain` call is treated as `high`.

**Taint:** a tool declares whether its result is *untrusted content* (content that may contain
prompt injection). Once any untrusted result has entered the current turn, every later tool call in
that turn is scored **one level higher**, capped at `critical`. A turn is the processing of one user
message.

**Decision:** with the final risk `r` and the agent's `allowedRiskLevel` `a`:
- `Blocked` → decision `blocked`.
- `r <= a` → decision `allowed`.
- `r > a` → request approval → decision `approved`, `denied` or `expired`.

## 4. Agent configuration

Read once at startup from `AGENT_CONFIG_PATH` (default `/config/agent.json`, mounted read-only):

```json
{
  "allowedRiskLevel": "low",
  "smartMode": true,
  "maxToolIterations": 10,
  "tools": ["workspace_list", "workspace_read", "workspace_write", "workspace_delete"]
}
```

| Key | Default | Notes |
|---|---|---|
| `allowedRiskLevel` | `low` | Calls above this need approval |
| `smartMode` | `false` | Auxiliary LLM for `Uncertain` calls |
| `maxToolIterations` | `10` | Max LLM rounds that return tool calls, per user message |
| `tools` | `[]` | Tools offered to the model. Unknown names fail startup |

- Missing file → the agent runs with **no tools** (chat only) and logs a warning.
- Invalid JSON, an unknown tool name or an unknown risk level → the agent logs an error and exits
  with code 1. Configuration errors MUST NOT silently grant tools.
- The configuration is never read from the workspace and never changes at runtime.

## 5. Contracts (OpenAI-compatible tool calling)

`/v1/chat/completions` keeps the OpenAI wire shape, which uses snake_case for these fields.

**Request additions**
- `tools`: optional array of `{ "type": "function", "function": { "name", "description", "parameters" } }`.
  `name` must match `^[a-zA-Z0-9_-]{1,64}$`. `parameters` is a JSON Schema object, passed through.
- `tool_choice`: optional; only `"auto"` or `"none"` (anything else → 400 `unsupported`).
- Message roles: `system`, `user`, `assistant`, `tool`.
  - `assistant` messages may have `content: null` with `tool_calls: [{ "id", "type": "function", "function": { "name", "arguments" } }]`
    (`arguments` is a JSON string).
  - `tool` messages require `tool_call_id` and string `content`.
- The 3.1 rule "`tools` → 400 `unsupported`" is removed.

**Response additions** (gateway-built SSE chunks)
- Providers may stream tool-call fragments. The gateway accumulates them and emits **each complete
  tool call once** as a chunk:
  `{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_…","type":"function","function":{"name":"…","arguments":"{…}"}}]},"finish_reason":null}]}`
- The finish chunk uses `finish_reason: "tool_calls"` when tool calls were returned.

**New gateway request header:** `X-SharpClaw-Purpose: chat | risk_assessment` (default `chat`).
For `risk_assessment` the gateway MUST NOT publish deltas to the thread's SSE stream.

**New SSE events on `GET /api/threads/{id}/stream`**

```
event: tool.requested
data: {"correlationId":"…","toolCallId":"call_…","toolName":"workspace_write"}

event: approval.requested
data: {"approvalId":"…","correlationId":"…","toolName":"workspace_delete","summary":"delete notes/old.md","risk":"high","reason":"deletes a file","expiresAt":"…"}
```

`tool.requested` MUST NOT include arguments. `approval.requested.summary` is a short human-readable
description built by the tool (section 6), at most 200 characters.

## 6. Tools in 3.2 (workspace only)

All tools operate inside the agent's workspace root (`WORKSPACE_PATH`, default `/workspace`). Paths
are relative, `/`-separated.

**Hardline rules** (→ `Blocked`) shared by all workspace tools:
- empty path, absolute path, or a path that resolves outside the root (`..` traversal);
- any existing path component that is a symbolic link;
- exceeding the size or count caps below.

| Tool | Arguments | Result | Risk | Untrusted result |
|---|---|---|---|---|
| `workspace_list` | `path` (optional, default root) | entries (name, type, size), max 200 | `low` | no |
| `workspace_read` | `path` | text, max 64 KB (larger files are truncated with a marker) | `low` | **yes** |
| `workspace_write` | `path`, `content` (max 256 KB) | `{ path, bytes, created }` | new file `low`; overwrite `medium`; *sensitive* path `Uncertain` | no |
| `workspace_delete` | `path` (files only) | `{ path, deleted }` | `high` | no |

- *Sensitive* paths for `workspace_write`: dotfiles (any segment starting with `.`) and extensions
  `.sh .bash .ps1 .bat .cmd .exe .dll .so .py .js` (executable or script content).
- Writes create parent directories, write to a temporary file in the same directory, then rename.
- Each tool provides a `summary` for approvals, e.g. `delete notes/old.md` or
  `write notes/a.md (1.2 KB, overwrite)`. Summaries never include file content.
- **Rule:** no tool may execute code or shell commands. This is recorded in the repo instructions.

### Deferred: network tools (designed now, built later)

Recorded so 3.2 does not paint us into a corner. Not implemented in 3.2.
- Tools: `download_to_workspace(url, path)`, `http_fetch(url)`.
- The agent decides: hardline rules → risk from URL and path → approval if needed.
- The gateway fetches (`POST /agent/fetch`) under fixed rules the agent cannot override: host
  allow-list, no private or loopback addresses, redirects re-checked, size cap, timeout. It audits
  host, status, bytes and type, never content.
- The agent writes the result into its workspace through the same confined write path; the model
  receives a short summary, not the content. Fetched content is untrusted (taint).
- Possible later addition: the gateway requires an approved approval id before fetching from a host
  that is not allow-listed.

## 7. Agent: tool dispatch

All tool execution goes through **one** class, `ToolDispatcher`:

```
dispatch(toolCall, turn):
  tool = registry[toolCall.name]           # unknown or not configured → error result, status "unknown_tool"
  args = parse(toolCall.arguments)         # invalid JSON → error result, status "invalid_arguments"
  assessment = tool.Assess(args)
  if Blocked → record, return error result
  risk = assessment is Uncertain ? (smartMode ? classify() : high) : assessment.level
  if turn.tainted → risk = min(risk + 1, critical)
  if risk > allowedRiskLevel:
      approval = gateway.requestApproval(...); wait for decision (long poll)
      if not approved → record, return error result
  result = tool.ExecuteUncheckedAsync(args)  # the ONLY call site
  if tool.ReturnsUntrustedContent → turn.tainted = true
  report outcome to gateway; return result
```

- An architecture test MUST fail if `ExecuteUncheckedAsync(` is called anywhere except
  `ToolDispatcher`.
- Results returned to the model are JSON strings: `{"ok":true,…}` or
  `{"ok":false,"error":"<code>","message":"<short text>"}`. Denials return
  `{"ok":false,"error":"denied_by_user"}` (or `blocked`, `approval_expired`, `no_approver`) so the
  model can adapt.
- Failure to report to the gateway is logged and does not fail the turn.

### Agent loop

```
messages = [system(soul)] + thread history
for i in 1..maxToolIterations+1:
    result = chat(messages, tools)          # 429/502/504/error chunk → fail message as in 3.1
    if result has no tool calls → post reply(result.content); done
    if i > maxToolIterations → fail message "tool_loop_limit"
    messages += assistant(content, tool_calls)
    for each call: messages += tool(call.id, dispatch(call, turn))
```

- Only the **final** assistant reply is stored in the thread. Tool exchanges are not stored as
  messages; they are visible through the tool-call audit. (Clients see intermediate assistant text
  streamed live as deltas.)
- History sent to the model therefore contains only user and final assistant messages.

## 8. Gateway changes

### 8.1 Chat completions

- Accept and forward `tools`, `tool_choice`, assistant `tool_calls` and `tool` messages (section 5).
- `ChatStreamEvent` gains `ToolCall(id, name, argumentsJson)`, emitted complete by providers.
- For each tool call, publish `tool.requested` to the thread (unless purpose is `risk_assessment`).
- When the call finishes with status `ok`, insert one `tool_calls` row per tool call (section 9).
- **Loop backstop:** before calling the provider, if the number of `llm_calls` rows for this
  correlation id is `>= Chat:MaxLlmCallsPerMessage` (default 20), record a `rejected` row with
  `error_code = loop_limit_exceeded` and return **429** `loop_limit_exceeded` (no `Retry-After`).
- **Claim heartbeat:** every chat call, approval request and approval poll sets the correlation
  message's `claimed_at` to now, so long tool turns and approval waits are not requeued by the claim
  timeout.

### 8.2 Approvals (agent listener)

**`POST /agent/approvals`** — `{ correlationId, threadId, toolCallId, toolName, summary, risk, reason }`
- The correlation message must be this agent's `claimed` message in that thread, else 404.
- If the thread has **no SSE subscriber**, the approval is created already `denied` with
  `decided_by = system` (nobody can answer; Hermes' unattended → deny). The agent reports this to
  the model as `no_approver`.
- Otherwise it is created `pending` with `expires_at = now + Approvals:TimeoutSeconds` (default
  300), and `approval.requested` is published.
- Returns `201 { "approval": { "id", "status", "decidedBy", "expiresAt" } }`.

**`GET /agent/approvals/{id}?wait=30`** — long poll (same clamp as the inbox) until the status is
not `pending` or `wait` elapses. A `pending` approval past `expires_at` becomes `expired` when read.
Returns `200 { "approval": { "id", "status", "decidedBy", "expiresAt" } }`. Foreign ids → 404.

### 8.3 Approvals (client listener)

**`POST /api/approvals/{id}`** — `{ "decision": "approved" | "denied" }`
- 404 if unknown; 409 `conflict` if not `pending` (including expired).
- Sets `status`, `decided_at`, `decided_by = user` and wakes the agent's long poll.
- Returns `200 { "approval": { "id", "status", "decidedBy", "expiresAt" } }`.

### 8.4 Smart-mode classification

The agent calls `/v1/chat/completions` with `X-SharpClaw-Purpose: risk_assessment`, no tools, a
fixed system prompt asking for exactly one word (`low`, `medium`, `high` or `critical`), and a user
message containing only the tool name and the tool's approval summary (never file content). The
first matching word in the reply is the risk; anything else → `high`. These calls count against the
budget and the loop backstop, and are recorded with `purpose = risk_assessment`.

### 8.5 Tool-call outcome reporting

**`POST /agent/tool-calls`** — `{ correlationId, toolCallId, decision, risk, tainted, approvalId, status, errorCode, durationMs }`
- The row must exist (created by the gateway from the LLM stream) and belong to this agent, else
  404. This means an agent cannot report a call the model never requested.
- Updates the row once (409 if already reported). Returns 204.

### 8.6 Audit (client listener)

**`GET /api/audit/tool-calls?agent=&threadId=&limit=100`** → `{ "calls": [ … ] }`, newest first,
`limit` max 500. Arguments are returned truncated as stored.

## 9. Data model

### `tool_calls`

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| agent_id | Guid FK | |
| thread_id | Guid | |
| correlation_id | Guid | |
| llm_call_id | Guid | the `llm_calls` row that returned this call |
| tool_call_id | text | provider id; unique with `correlation_id` |
| tool_name | text | |
| arguments | text | truncated to 1,000 characters |
| status | text | `requested` → `succeeded`, `failed`, `denied`, `blocked`, `expired`, `unknown_tool`, `invalid_arguments` |
| decision | text, nullable | `allowed`, `approved`, `denied`, `expired`, `blocked` |
| risk | text, nullable | `low`…`critical` |
| tainted | bool | |
| approval_id | Guid, nullable | |
| error_code | text, nullable | |
| duration_ms | int, nullable | |
| created_at | UTC datetime | |
| reported_at | UTC datetime, nullable | null = the agent never reported |

Indexes: unique `(correlation_id, tool_call_id)`; `(agent_id, created_at)`.

### `approvals`

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| agent_id, thread_id, correlation_id | Guid | |
| tool_call_id, tool_name | text | |
| summary | text | at most 200 characters, no content |
| risk, reason | text | |
| status | text | `pending`, `approved`, `denied`, `expired` |
| decided_by | text, nullable | `user` or `system` |
| created_at, expires_at | UTC datetime | |
| decided_at | UTC datetime, nullable | |

### `llm_calls`

Add `purpose` (text, default `chat`). The table stays append-only.

## 10. CLI

- During `chat` / `ask`: on `approval.requested` for the posted message, print the tool, risk,
  reason and summary, prompt `Allow? [y/N]`, and post the decision. Anything other than `y`/`yes`
  is a denial. A 409 (expired) prints "approval expired".
- `tool.requested` prints a short marker line, e.g. `[tool] workspace_write`.
- New: `sharpclaw audit tools [--agent <name>] [--limit N]` — table of recent tool calls.

## 11. Configuration (gateway additions)

| Key | Default | Notes |
|---|---|---|
| `Approvals:TimeoutSeconds` | `300` | Pending approvals expire after this |
| `Chat:MaxLlmCallsPerMessage` | `20` | Loop backstop per correlation id |

Agent: `AGENT_CONFIG_PATH` (default `/config/agent.json`), see section 4.

## 12. Telemetry

| Name | Instrument | Labels |
|---|---|---|
| `sharpclaw.tool.calls` | Counter | agent, tool, status |
| `sharpclaw.approvals` | Counter | agent, status |

Recorded when the agent reports an outcome and when an approval is decided or expires. Tool names
come from a fixed set, so they are acceptable labels. Arguments and results never appear in logs or
labels. Add dashboard panels for tool calls by status and approvals by status.

## 13. Tests

- **Tool calling (gateway):** tools forwarded; tool calls streamed as complete chunks with
  `finish_reason: tool_calls`; `tool_calls` rows recorded; `tool.requested` published without
  arguments; `risk_assessment` purpose suppresses SSE deltas; `tool_choice` validation; invalid tool
  names rejected; FakeProvider tool-call scripting; OpenRouter fragment accumulation.
- **Loop backstop:** the 21st call for a correlation id gets 429 `loop_limit_exceeded`.
- **Approvals:** no subscriber → denied immediately; approve and deny paths; expiry after the
  timeout (fake clock); 409 on a decided approval; foreign ids 404; long poll wakes on decision;
  heartbeat keeps the message claimed.
- **Outcome reporting:** unknown call 404; second report 409; audit endpoint lists rows.
- **Agent:** dispatcher decisions for every branch (allowed, approval approved/denied/expired,
  blocked, unknown tool, invalid arguments, uncertain with and without smart mode, taint bump);
  architecture test for the single call site; workspace tools (traversal, absolute paths, symlinks,
  caps, overwrite detection, sensitive paths, atomic write); config loading (missing, invalid,
  unknown tool); tool loop (multi-step, iteration cap, reply stored once); no content in logs.
- **CLI:** approval prompt posts the decision; `audit tools` parsing.
- **Acceptance:** multi-step task on FakeProvider and real OpenRouter; approval granted and denied;
  unattended deny; isolation script still passes.

## 14. Out of scope for 3.2

Network tools (designed in section 6, built later), code or shell execution, gateway-side policy
decisions, per-tool approval memory ("always allow"), policy hot reload, MCP, web UI, agent-to-agent
messaging, storing tool exchanges in thread history.

## 15. Task order

| # | Task |
|---|---|
| 1 | This spec; roadmap update; no-code-execution rule in the repo instructions |
| 2 | Tech debt #1: refactor `ChatEndpoints` |
| 3 | Tech debt #2: `OpenRouterProvider` tests |
| 4 | Tool calling through `/v1/chat/completions` (contracts, providers, streaming) |
| 5 | Tool-call audit (`tool_calls`, reporting, audit endpoint, metrics) |
| 6 | Approvals in the gateway and the claim heartbeat |
| 7 | Agent tool framework (config, registry, dispatcher, risk, taint, architecture test) |
| 8 | Workspace tools |
| 9 | Agent tool loop, approval client, smart mode, reporting |
| 10 | Gateway loop backstop |
| 11 | CLI approvals and `audit tools` |
| 12 | Docker, dashboard and docs |
| 13 | Acceptance walk-through |
