# SharpClaw V3 — Pre-3.3 Foundations Spec

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** built (tasks T0–T6; T7 acceptance recorded in [verification-log.md](verification-log.md)). Orientation and the decisions taken on open points: [pre-3.3-orientation.md](pre-3.3-orientation.md).
**Relationship to other documents:** Phases 3.1 and 3.2 are built. This spec adds five foundations that the Phase 3.3 web UI depends on. Where it conflicts with the 3.1 or 3.2 specs, **this spec wins** (section 3 lists the supersessions). Everything else in those specs still applies.

## 0. How to use this document

- **Task T0 is mandatory and comes first.** You have access to the repository; this spec does not. Class, file and endpoint names below are illustrative. Read the code, map this spec onto what actually exists, and report conflicts before changing anything.
- Implement in the task order in section 13. Each task is one branch and one pull request, with passing tests, before the next starts.
- "MUST" / "MUST NOT" are hard requirements.
- Build only what is described. Anything not here is out of scope (section 14). If something is ambiguous or contradictory, stop and ask the human.
- The repo's standing rules still apply: never log or emit as a metric label any prompt, reply, tool argument, tool result, soul contents, token, key, password, session value or CSRF token.
- Update docs as part of each task (README sections, `docs/tech-debt.md`, the roadmap), keeping the README concise: do not add dated verification logs or duplicate configuration tables to it.

## 1. Goals

The 3.3 web UI exposes five weaknesses that are cheap to fix now and expensive to fix after the UI exists:

| # | Foundation | Problem today |
|---|---|---|
| 1 | Browser authentication | The client API accepts only a static admin key as a bearer token. A browser cannot use that safely, and `EventSource` cannot set an `Authorization` header. |
| 2 | Tool exchanges in thread history | Only the final reply is stored. The model forgets earlier tool use and the UI cannot show it after a reload. |
| 3 | Context management | The full thread is sent on every call. Long threads, made longer by tool use, will eventually fail at the provider. |
| 4 | SSE replay | The event stream has no replay. A dropped connection (phone sleep, tab reload) loses events. |
| 5 | Approvals with nobody watching | With no SSE subscriber, approvals are denied immediately, so closing the tab means no approvals can ever be granted. |

## 2. Decisions and defaults

These are the defaults this spec is written around. 

| Decision | Default |
|---|---|
| **(D)** Browser auth model | Gateway-native login: username and password, server-side session, `HttpOnly` cookie. The admin-key bearer remains for the CLI and scripts. |
| **(D)** UI origin | The UI is served **same-origin** as the client API (by the gateway or behind the same reverse proxy path). No CORS in this phase. |
| **(D)** TLS | Terminated by a reverse proxy in front of the gateway. The gateway supports forwarded headers; it does not terminate TLS. |
| **(D)** Password policy | At least 12 characters. PBKDF2-HMAC-SHA256, 600,000 iterations or more. |
| **(D)** Tool results in history | Stored truncated. Results flagged untrusted are **not replayed** to the model on later turns (a stub is sent instead). |
| **(D)** Default context window | 32,000 tokens per agent, overridable per agent. |
| **(D)** Context shortening | Drop whole oldest turns. Summarisation is a future extension (interface only). |
| **(D)** Approvals | Pending approvals wait for a human even when nobody is connected, unless the message was posted `unattended`. Timeout stays 300 seconds. |
| **(D)** Event retention | 14 days of durable events. |
| Existing, unchanged | SQLite, EF Core, UTC `DateTime`, `TimeProvider` everywhere, error shape, two listeners separated by local port, agents authenticate with per-agent tokens. |

## 3. Supersessions and migrations overview

| Previous rule | Replaced by |
|---|---|
| 3.2 §7: "Only the final assistant reply is stored in the thread." | Section 5: tool exchanges are stored (as non-default message kinds). |
| 3.2 §8.2: "No SSE subscriber → approval created already denied." | Section 9: pending unless the message is `unattended`. |
| 3.1 §5: `messages` ordered by `created_at, id`; unique reply index on `role = 'assistant'`. | Section 5: ordering by a per-thread `seq`; unique index on final assistant messages only. |
| 3.1 §9: no replay. | Section 8: durable events and `Last-Event-ID` replay. |
| 3.1 §6: client API authenticated by admin key only. | Section 4: admin key or session cookie. |
| Tech-debt #11 (lazy approval expiry) | Fixed by the expiry sweeper (section 9). Remove the register entry in the same PR. |

All schema changes are EF migrations; each task commits its own migration. Existing rows MUST be migrated safely (backfills are specified per section).

## 4. Foundation 1: browser authentication

### 4.1 Model

- **Credentials:** the admin key (bearer) continues to work on every client route exactly as today. A browser instead logs in with a username and password and receives a session cookie.
- **Which routes accept which credential:** all `/api/*` routes accept *either* a valid admin-key bearer *or* a valid session cookie, except user management (`/api/users*`), which accepts the admin-key bearer only. A stolen browser session therefore cannot create users or reset passwords.
- The agent listener is unchanged. Cookies are ignored there.
- Multi-user is **not** a goal. The schema supports several users but nothing in 3.3 requires more than one.

### 4.2 Data model

**`users`**

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| name | text, unique | lowercase, `^[a-z0-9][a-z0-9._-]{1,31}$` |
| password_hash | text | `pbkdf2-sha256$<iterations>$<salt b64>$<hash b64>`, 16-byte salt, 32-byte hash |
| disabled | bool | default false |
| created_at | UTC datetime | |

**`sessions`**

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| user_id | Guid FK | |
| token_hash | text, unique | SHA-256 hex of the random cookie value (32 bytes, base64url) |
| csrf_token | text | random 32 bytes, base64url; returned to the UI by `GET /api/auth/session` |
| created_at | UTC datetime | |
| expires_at | UTC datetime | absolute expiry |
| last_seen_at | UTC datetime | updated at most once per minute |
| revoked_at | UTC datetime, nullable | |

### 4.3 Endpoints (client listener)

| Route | Auth | Behaviour |
|---|---|---|
| `POST /api/auth/login` `{ "username", "password" }` | none (rate limited) | On success: creates a session, sets the cookie, returns `200 { "user": {"id","name"}, "csrfToken": "…", "expiresAt": "…" }`. On any failure (unknown user, wrong password, disabled user): `401 unauthorized` with the **same** message `Invalid username or password.` |
| `POST /api/auth/logout` | session | Revokes the session, clears the cookie, `204`. |
| `GET /api/auth/session` | optional | Always `200`. `{ "authenticated": false }` when no valid credential. Otherwise `{ "authenticated": true, "method": "session" or "admin_key", "user": {…} or null, "csrfToken": "…" or null, "expiresAt": … }`. |
| `POST /api/auth/password` `{ "currentPassword", "newPassword" }` | session | Verifies the current password, applies the policy, updates the hash, revokes **all other** sessions of the user. `204`. |
| `POST /api/users` `{ "name", "password" }` | admin key only | Creates a user. `201`. 409 on duplicate name. |
| `GET /api/users` | admin key only | Names, ids, disabled, createdAt. Never hashes. |
| `PUT /api/users/{name}/password` `{ "password" }` | admin key only | Sets the password and **revokes all sessions** for that user. `204`. |
| `PATCH /api/users/{name}` `{ "disabled" }` | admin key only | Disabling revokes all sessions. |

### 4.4 Password hashing and login hardening

- Hash with `Rfc2898DeriveBytes.Pbkdf2` (HMAC-SHA256), iterations from config (default 600,000, never lower than that). Compare with `CryptographicOperations.FixedTimeEquals`. On successful login, if the stored iteration count is below the configured one, re-hash and store.
- For an unknown username, still perform a hash computation against a dummy hash, so response time does not reveal whether the user exists.
- Password policy: at least 12 characters, at most 256. Violations → 400 `validation_failed` (policy message only, never echo the value).
- **Rate limiting:** in-memory counters, both per username and per remote IP: `Auth:Login:MaxFailures` (default 5) failures within `Auth:Login:WindowMinutes` (default 15) → `429 rate_limited` with `Retry-After`. A success clears the username counter. (Add `rate_limited` to the error table: HTTP 429.) Counters reset on restart; that is acceptable.
- Use the connection's remote address, taking forwarded headers into account only when `Auth:ForwardedHeaders:Enabled` is true.

### 4.5 Cookie, session lifetime, CSRF and origin

- Cookie name `Auth:Session:CookieName` (default `sharpclaw_session`); attributes `HttpOnly`, `SameSite=Strict`, `Path=/`, `Secure` according to `Auth:Session:CookieSecure` (`Auto` = Secure when the request is HTTPS as seen after forwarded-header processing; `Always`; `Never`). Cookie expiry = the session's absolute expiry.
- Sessions slide: idle timeout `Auth:Session:IdleTimeoutHours` (default 168, i.e. 7 days) since `last_seen_at`; absolute cap `Auth:Session:AbsoluteTimeoutDays` (default 30). Expired or revoked → treated as unauthenticated. Prune sessions expired or revoked more than 7 days ago daily.
- **CSRF protection for cookie-authenticated unsafe methods** (`POST`, `PUT`, `PATCH`, `DELETE`) — all three MUST hold:
  1. `SameSite=Strict` cookie (above);
  2. the `Origin` header is present and is the request's own origin or one listed in `Auth:AllowedOrigins`; if `Origin` is absent, fall back to `Referer`; if both are absent, reject;
  3. header `X-CSRF-Token` equals the session's `csrf_token` (compare in constant time).
  Failure → `403 csrf_failed` (add to the error table).
  `POST /api/auth/login` requires only the origin check (there is no session yet).
- Bearer (admin key) requests are exempt from CSRF checks (no ambient credential).
- **SSE** needs no special handling: same-origin `EventSource` sends the cookie. Do not accept credentials in query strings.
- **CORS:** disabled. `Auth:AllowedOrigins` exists for reverse-proxy setups where the browser's origin differs from the gateway's view of it; it is not a CORS allow-list.
- **Forwarded headers:** when `Auth:ForwardedHeaders:Enabled` (default false) is true, apply `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` from `Auth:ForwardedHeaders:KnownProxies` only. Never trust them otherwise.

### 4.6 Logging and metrics

- NEVER log passwords, hashes, cookie values, CSRF tokens, or the **username submitted on a failed login** (people paste passwords into the wrong field). Log successful logins by user name and session id only.
- `sharpclaw.auth.failures` gains a `method` label (`admin_key`, `password`, `session`, `csrf`, `origin`). `sharpclaw.auth.logins` counter, label `result` (`success`, `failure`, `rate_limited`).

### 4.7 CLI

| Command | Behaviour |
|---|---|
| `sharpclaw users create <name>` | Prompts for the password without echo (confirm twice); `--password-stdin` for scripts. Uses the admin key. |
| `sharpclaw users list` | Table. |
| `sharpclaw users set-password <name>` | Same prompting; revokes sessions. |
| `sharpclaw users disable <name>` / `enable <name>` | |

The CLI keeps using the admin key; it does not log in.

## 5. Foundation 2: tool exchanges in thread history

### 5.1 Goal and principles

The thread stores the whole conversation the model took part in, including tool calls and their results, so that (a) the model remembers earlier tool use, and (b) the UI can show it after a reload. Safety constraints:

- Stored tool arguments and results are **truncated**; history must not balloon.
- Results flagged **untrusted** (content that may carry prompt injection, e.g. `workspace_read`) are **never replayed to the model on later turns**; a stub is sent instead. This keeps taint from becoming permanent in a thread and keeps injected content from persisting through history.
- Nothing here changes the rule that tool arguments and results never appear in logs or metric labels. They are stored in the database only.

### 5.2 Data model changes: `messages`

| New column | Type | Notes |
|---|---|---|
| seq | int | Per-thread sequence, assigned at insert inside the transaction as `max(seq)+1`. **All ordering uses `seq`.** Unique index `(thread_id, seq)`. Backfill existing rows by `(created_at, id)`. |
| kind | text | `message` (default; user messages and final assistant replies), `tool_request`, `tool_result` |
| tool_calls | text, nullable | JSON array `[{ "id", "name", "arguments" }]` on `tool_request` rows |
| tool_call_id | text, nullable | on `tool_result` rows |
| tool_ok | bool, nullable | on `tool_result` rows |
| untrusted | bool | default false; on `tool_result` rows |
| truncated | bool | default false; true if any stored content or argument was shortened |
| unattended | bool | default false; on `user` rows (used in section 9) |

Roles are now `user`, `assistant`, `tool`. A `tool_request` row is role `assistant` (its `content` is any text the model produced before calling tools; may be empty). A `tool_result` row is role `tool`. Both have status `done` and carry the same `correlation_id` as the user message that started the turn.

**Index change:** drop the unique `correlation_id` index filtered on `role = 'assistant'`; replace it with a unique index on `correlation_id` filtered to `role = 'assistant' AND kind = 'message'`. (One final reply per user message, still enforced.) Keep `(agent_id, role, status, created_at)` and add `(thread_id, seq)`.

Backfill: all existing rows get `kind = 'message'`.

### 5.3 Writing exchanges

Extend the agent reply endpoint (the only place exchanges are written):

**`POST /agent/threads/{id}/messages`**
```json
{
  "content": "final reply text",
  "correlationId": "…",
  "exchanges": [
    {
      "assistantContent": "text before the calls, or null",
      "toolCalls": [ { "id": "call_1", "name": "workspace_write", "arguments": "{\"path\":\"a.md\",\"content\":\"…\"}" } ],
      "results":   [ { "toolCallId": "call_1", "content": "{\"ok\":true,…}", "ok": true, "untrusted": false } ]
    }
  ]
}
```

- `exchanges` is optional (default empty). Validation: at most `Tools:MaxExchangesPerReply` (default 50); within an exchange every tool call has exactly one result and vice versa; ids unique within the reply. Violations → 400 `validation_failed`.
- **Single transaction:** insert each exchange (a `tool_request` row, then one `tool_result` row per result in call order), then the final `message` row, then mark the user message `done`. Assign `seq` in that order. If anything fails, nothing is stored.
- **Truncation (agent first, gateway enforces):**
  - `arguments`: keep valid JSON. Truncate every JSON string value longer than `Tools:MaxStoredArgumentStringChars` (default 500), appending `…[truncated N chars]`.
  - result `content`: truncate to `Tools:MaxStoredResultChars` (default 4,000), appending `…[truncated N chars]`.
  - The gateway re-applies both limits to whatever it receives and sets `truncated = true` when it shortens anything.
- A turn that ends with `fail` stores **no** exchanges (known gap, section 15). The `tool_calls` audit table (3.2) is unaffected and remains the independent record.
- The audit table `tool_calls` and these message rows are linked by the provider tool-call id and the correlation id; no foreign key is required.

### 5.4 Reading

- **Client:** `GET /api/threads/{id}/messages` returns only `kind = 'message'` rows by default. `?includeTools=true` returns all kinds in `seq` order. The message JSON gains: `kind`, `seq`, `toolCalls` (array or null), `toolCallId`, `toolOk`, `untrusted`, `truncated`. The existing `after=<messageId>` parameter continues to work (by `seq`).
- **Agent:** `GET /agent/threads/{id}/messages` returns all kinds in `seq` order with the same fields. (The agent will normally use the context endpoint in section 6 instead.)
- **Model-facing replay** (done in the context endpoint, section 6): rows map to OpenAI messages — `tool_request` → `assistant` with `content` (or null) and `tool_calls`; `tool_result` → `tool` with `tool_call_id` and `content`. For `tool_result` rows with `untrusted = true` that belong to an **earlier turn** than the current one, replace the content with:
  `{"ok":true,"omitted":"Untrusted content from an earlier turn is not replayed. Call the tool again if you need it."}`
  Trusted results (list, write, delete outcomes) are replayed as stored.

### 5.5 Agent changes

- Collect each exchange during the turn (the loop already has the assistant message, calls and results).
- Pass them in the reply as above, applying the truncation rules before posting.
- Do not change the existing taint logic: a turn still starts untainted, because untrusted content from earlier turns is stubbed out.

## 6. Foundation 3: context management

### 6.1 Goal

Prevent unbounded growth of the prompt. The gateway builds the model-ready history, fitting it into a per-agent window, with a clear error when a single turn cannot fit. Summarisation is deferred, but the seam is built.

### 6.2 Configuration and data

| Key | Default | Notes |
|---|---|---|
| `Context:DefaultWindowTokens` | `32000` | Used when the agent has no override |
| `Context:ReplyReserveTokens` | `2000` | Kept free for the model's reply |
| `Context:CharsPerToken` | `4` | Estimation heuristic |
| `Context:MessageOverheadTokens` | `4` | Per message |

- `agents.context_window_tokens` (int, nullable). Exposed as `contextWindowTokens` on agent create/patch/get; effective window = agent value ?? default. CLI: `agents create … --context-window <n>`, `agents set … --context-window <n>`. Validation: 1,000 to 2,000,000.
- `llm_calls.estimated_prompt_tokens` (int, nullable): the gateway's estimate for every call, stored next to the provider-reported prompt tokens so the heuristic can be tuned later.
- `threads`: `context_dropped_turns` (int, default 0) and `context_estimated_tokens` (int, default 0), updated whenever the context endpoint runs. The thread DTO exposes `context: { "droppedTurns", "estimatedTokens", "windowTokens" }` so the UI can show that older messages are not in the model's context.

### 6.3 Token estimation

`estimate(message) = ceil((chars(content) + chars(tool_calls JSON)) / CharsPerToken) + MessageOverheadTokens`. Request-level tool schemas are added by the caller through `reserveTokens` (below). Character counts, not content, are all that is used and recorded.

### 6.4 Context endpoint (agent listener)

**`GET /agent/threads/{id}/context?reserveTokens=<n>`**

- `reserveTokens` is supplied by the agent: its estimate for the system prompt (soul) plus tool schemas. Default 0; must be `>= 0`.
- Response `200`:
```json
{
  "messages": [ { "role": "user", "content": "…" }, { "role": "assistant", "content": null, "tool_calls": [ … ] }, { "role": "tool", "tool_call_id": "…", "content": "…" } ],
  "truncated": true,
  "droppedTurns": 3,
  "estimatedTokens": 5120,
  "windowTokens": 32000
}
```
(OpenAI wire shape, snake_case for `tool_calls` and `tool_call_id`.)

**Algorithm**
1. Load all rows for the thread by `seq`, applying the untrusted-result stub rule (5.4).
2. Group into **turns**: a turn starts at a `user` `kind = message` row and includes everything up to the next such row. The **current turn** is the one containing the message this agent has claimed (the latest).
3. `budget = windowTokens − reserveTokens − Context:ReplyReserveTokens`.
4. The current turn is always included. If it alone exceeds the budget → `400 context_too_large` (add to the error table).
5. Add earlier turns newest-first, whole turns only, while the running total stays within the budget; stop at the first that does not fit.
6. If any turns were dropped, ask `IContextSummariser` for a summary of them (see 6.5); if it returns nothing, prepend a `system` message: `Earlier conversation (N turns) is not shown.`
7. Update the thread's context columns; record metrics (6.6).

The endpoint is read-only apart from step 7. It returns `404` for threads that do not belong to the agent.

### 6.5 Summariser seam

```csharp
public interface IContextSummariser
{
    Task<string?> SummariseAsync(Guid threadId, IReadOnlyList<ContextMessage> droppedMessages, CancellationToken ct);
}
```
Register a `NoSummariser` that returns `null`. Do not implement summarisation in this phase. A test double MUST be used to prove the seam works (a returned summary replaces the generic note).

### 6.6 Preflight on chat completions

Before calling the provider, estimate the prompt from the request's messages (plus tool schemas in the request, as `ceil(schema chars / CharsPerToken)`). If `estimate > windowTokens − ReplyReserveTokens`: record an `llm_calls` row with status `rejected`, `error_code = context_too_large`, and return `400 context_too_large`. Always store `estimated_prompt_tokens`. Additionally map a recognisable upstream "context length exceeded" provider error to the same code when it occurs.

### 6.7 Agent changes

- Replace "fetch whole history and map it" with a call to the context endpoint, passing `reserveTokens` computed from soul length and tool schema size using the same `CharsPerToken` heuristic (read from a shared constant or the response).
- Messages sent to the model = `[system(soul)] + context.messages`, plus in-turn tool exchanges as the loop proceeds.
- `400 context_too_large` from either the context endpoint or chat completions → `fail(msg.id, "context_too_large")`.

### 6.8 Metrics

`sharpclaw.context.truncations` (counter, label agent), `sharpclaw.context.rejections` (counter, label agent), `sharpclaw.context.estimated_tokens` (histogram, unit `{token}`, label agent; recorded at the context endpoint).

## 7. Shared contracts for sections 5 and 6

Update `SharpClaw.Contracts` for: the extended message DTO; the reply request with `exchanges`; the context response; agent DTO `contextWindowTokens`; thread DTO `context`; error codes `context_too_large`, `csrf_failed`, `rate_limited`. Keep serialisation camelCase except the OpenAI-shaped fields noted above.

## 8. Foundation 4: SSE replay

### 8.1 Goal

A client that disconnects and reconnects (including the browser's automatic `EventSource` reconnect) misses nothing durable and can reconstruct the in-progress reply.

### 8.2 Durable and ephemeral events

| Kind | Events | Has `id:` |
|---|---|---|
| **Durable** (stored, replayable) | `message.created` (new: a user message was posted), `message.completed`, `error`, `tool.requested`, `approval.requested`, `approval.decided` (new) | yes |
| **Ephemeral** | `delta`, `snapshot` (new), `ready` (new), `reset` (new), keep-alive comments | no (except `ready`, see below) |

Event payloads keep the shapes defined in 3.1 and 3.2, plus:
- `message.created`: `{ "messageId", "threadId", "correlationId": "<same as messageId>" }`
- `approval.decided`: `{ "approvalId", "correlationId", "status": "approved|denied|expired", "decidedBy": "user|system" }`
- `snapshot`: `{ "correlationId", "content": "<text streamed so far>" }`
- `reset`: `{ "reason": "history_pruned" }`
- `ready`: `{ "threadId" }`, sent with `id:` set to the current maximum event id for the scope.

### 8.3 Storage

**`thread_events`**

| Column | Type | Notes |
|---|---|---|
| id | bigint PK, autoincrement | the SSE event id; global and monotonic |
| thread_id | Guid | indexed `(thread_id, id)` |
| type | text | |
| data | text | the JSON payload exactly as sent |
| created_at | UTC datetime | |

**`event_log_state`**: single row `pruned_through` (bigint, default 0): the highest event id removed by pruning.

Durable events are **persisted first, then published** to the in-memory bus with their id. Use one `IEventPublisher` for all durable events. Payloads contain content only where the live event already did (`message.completed`); they never contain tool arguments or results. Retention: prune events older than `Events:RetentionDays` (default 14) daily and at startup, updating `pruned_through`.

### 8.4 Endpoints and connection protocol

- `GET /api/threads/{id}/stream` (existing) and **new** `GET /api/events` (durable events for all threads, same protocol). Both accept the last id from the `Last-Event-ID` header or, as a fallback, `?lastEventId=`.
- On connect, send `retry: 3000`, then:
  1. Subscribe to the live bus **first**, buffering events.
  2. If a last id was supplied: if it is **less than** `pruned_through`, send `reset` (the client must reload history) and continue; otherwise send all stored events with `id > lastId` for the scope, in order.
  3. If no last id was supplied: replay nothing.
  4. Send `ready` with `id:` = the current maximum event id for the scope (so a subsequent reconnect has a baseline).
  5. For each in-flight turn in the scope, send a `snapshot` from the active-turn buffer (8.5).
  6. Flush buffered live events, **dropping durable events with id ≤ the highest id already sent**, then stream live.
- Keep the 15-second keep-alive comment.
- `delta` events carry **no** `id:` line, so browsers keep the last durable id as `Last-Event-ID`.
- A gauge `sharpclaw.sse.subscribers` (label `scope`: `thread` or `global`) tracks open streams.

### 8.5 Active-turn buffer

An in-memory `ActiveTurnBuffers` keyed by `(threadId, correlationId)`. The chat completions endpoint appends each streamed content delta (purpose `chat` only) as it publishes the `delta` event; maximum 256 KB per buffer (drop appends beyond the cap and log once). Remove the buffer when the turn ends: `message.completed`, `fail`, claim requeue, or after 15 minutes. It is not persisted; a gateway restart loses in-flight turns anyway (the claim timeout requeues them).

### 8.6 CLI

- `chat` and `ask` reconnect automatically if the stream drops (backoff 1 s doubling to 10 s, giving up after 60 s of failures), passing the last durable event id.
- Track characters already printed per correlation id. On `snapshot`, print only `content[printed..]`. On `reset`, reload the thread messages and continue waiting for the in-flight reply.
- Ignore durable events whose id was already handled.

## 9. Foundation 5: approvals without a watcher

### 9.1 Behaviour change

Pending approvals are **durable queue items**. An approval no longer depends on an SSE subscriber being connected:

- By default, `POST /agent/approvals` creates the approval `pending` with `expires_at = now + Approvals:TimeoutSeconds` (default 300) and publishes `approval.requested` (a durable event).
- **Unattended turns:** the client may post a message with `"unattended": true` (`POST /api/threads/{id}/messages { "content", "unattended" }`, default false; stored on the user message). For an unattended message, an approval is created already `denied` with `decided_by = system` and the agent reports `no_approver`, exactly as the old no-subscriber behaviour. This is how scripts, tests and (later) scheduled agents declare that nobody is there to answer.
- Agent-side behaviour is unchanged: it posts the approval, then long-polls it; outcomes still map to `denied_by_user`, `approval_expired`, `no_approver`.

### 9.2 Queue API (client listener)

| Route | Behaviour |
|---|---|
| `GET /api/approvals?status=pending&agent=<name>&limit=50` | `{ "approvals": [ { id, agent, threadId, correlationId, toolName, summary, risk, reason, status, createdAt, expiresAt, decidedBy, decidedAt } ] }`, newest first. `status` optional; `limit` max 200. |
| `GET /api/approvals/{id}` | One approval, same shape. 404 if unknown. |
| `POST /api/approvals/{id}` `{ "decision" }` | Unchanged from 3.2 (409 if not pending). Now also authenticated by session cookie with CSRF (section 4). Publishes `approval.decided`. |

### 9.3 Expiry sweeper

A hosted `ApprovalExpirySweeper` runs every 10 seconds (injectable timer/clock): every `pending` approval with `expires_at <= now` becomes `expired` with `decided_by = system`, wakes the agent's long poll, publishes a durable `approval.decided`, and records the metric. Keep the lazy expiry on read as a backup. This resolves tech-debt #11; remove that register entry in the same PR.

### 9.4 Orphaned approvals

When a user message leaves `claimed` for any reason other than completing normally (the `fail` endpoint, claim-timeout requeue), every `pending` approval for that correlation id becomes `expired` (`decided_by = system`) with the same events and metrics. When the reply is posted, any still-pending approval for that correlation is likewise expired.

### 9.5 Notification seam

```csharp
public interface IApprovalNotifier
{
    Task NotifyRequestedAsync(ApprovalNotification approval, CancellationToken ct);
}
```
Called after a `pending` approval is created. `ApprovalNotification` carries ids, agent name, tool name, risk, the approval summary (which already contains no content) and expiry only; never arguments or results. Register a no-op implementation. Exceptions are caught and logged without content. Do not build any real notifier in this phase.

### 9.6 CLI

- `sharpclaw approvals list [--status pending] [--agent <name>]`
- `sharpclaw approvals approve <id>` / `approvals deny <id>`
- `sharpclaw ask <agent> "<text>" --unattended` (posts with `unattended: true`; any approval is denied immediately).
- The existing interactive prompt in `chat`/`ask` is unchanged.

### 9.7 Metrics

`sharpclaw.approvals.pending` (observable gauge, label agent), `sharpclaw.approval.decision_time` (histogram, unit `s`, labels agent and status; from creation to decision or expiry). The existing `sharpclaw.approvals` counter keeps its labels.

## 10. Configuration summary (all new keys)

| Key | Default | Section |
|---|---|---|
| `Auth:Session:CookieName` | `sharpclaw_session` | 4 |
| `Auth:Session:CookieSecure` | `Auto` (`Auto`/`Always`/`Never`) | 4 |
| `Auth:Session:IdleTimeoutHours` | `168` | 4 |
| `Auth:Session:AbsoluteTimeoutDays` | `30` | 4 |
| `Auth:Password:Pbkdf2Iterations` | `600000` (minimum 600000) | 4 |
| `Auth:Login:MaxFailures` | `5` | 4 |
| `Auth:Login:WindowMinutes` | `15` | 4 |
| `Auth:AllowedOrigins` | empty (same-origin only) | 4 |
| `Auth:ForwardedHeaders:Enabled` | `false` | 4 |
| `Auth:ForwardedHeaders:KnownProxies` | empty | 4 |
| `Tools:MaxStoredResultChars` | `4000` | 5 |
| `Tools:MaxStoredArgumentStringChars` | `500` | 5 |
| `Tools:MaxExchangesPerReply` | `50` | 5 |
| `Context:DefaultWindowTokens` | `32000` | 6 |
| `Context:ReplyReserveTokens` | `2000` | 6 |
| `Context:CharsPerToken` | `4` | 6 |
| `Context:MessageOverheadTokens` | `4` | 6 |
| `Events:RetentionDays` | `14` | 8 |
| `Approvals:TimeoutSeconds` | `300` (existing) | 9 |
| `Approvals:SweepIntervalSeconds` | `10` | 9 |

New error codes (add to the shared error table and contracts): `csrf_failed` (403), `rate_limited` (429, with `Retry-After`), `context_too_large` (400).

## 11. Telemetry summary

All telemetry rules from 3.1 and 3.2 still apply (no content, ids as log attributes not resource attributes, exporter failures never break requests).

| Metric | Type | Labels | Section |
|---|---|---|---|
| `sharpclaw.auth.failures` (extended) | counter | listener, method | 4 |
| `sharpclaw.auth.logins` | counter | result | 4 |
| `sharpclaw.context.truncations` | counter | agent | 6 |
| `sharpclaw.context.rejections` | counter | agent | 6 |
| `sharpclaw.context.estimated_tokens` | histogram | agent | 6 |
| `sharpclaw.sse.subscribers` | observable gauge | scope | 8 |
| `sharpclaw.approvals.pending` | observable gauge | agent | 9 |
| `sharpclaw.approval.decision_time` | histogram (s) | agent, status | 9 |

Also add (small, from the earlier code review): `sharpclaw.tool.unreported` (observable gauge, label agent) counting `tool_calls` rows older than 5 minutes with `reported_at` null. This is the integrity signal the 3.2 design relies on.

Update `docker/grafana/sharpclaw-dashboard.json` with panels for: pending approvals and decision time, context truncations and rejections, login results and auth failures by method, unreported tool calls. Use the metric names as they actually land in VictoriaMetrics (see `docs/telemetry-notes.md`).

## 12. Tests

Use the existing test setup (real Kestrel on ephemeral ports, temporary SQLite, `FakeProvider`, `FakeTimeProvider`).

**Foundation 1: auth**
- Login success sets an `HttpOnly`, `SameSite=Strict` cookie and returns a CSRF token; the `Secure` flag follows the setting and forwarded-header handling.
- Wrong password, unknown user and disabled user return the identical response and do similar work (the dummy-hash path is exercised).
- Rate limiting by username and by IP, `Retry-After` present, success clears the counter; counters respect the fake clock.
- Cookie-authenticated `POST` without `Origin`/`Referer`, with a foreign `Origin`, or without a valid `X-CSRF-Token` → 403; bearer requests are exempt; `GET` needs none.
- Session idle expiry (sliding) and absolute expiry using the fake clock; logout and password change revoke correctly (password change keeps the current session, revokes others).
- `/api/users*` rejects session cookies and accepts the admin key.
- A cookie on the agent listener is ignored; agent routes still need agent tokens.
- Password policy; hash format; re-hash when iterations increase; hashes never returned by any endpoint.
- Logs captured during a failed login contain neither the submitted username nor the password.

**Foundation 2: tool exchanges**
- Reply with exchanges stores `tool_request`, `tool_result` and the final message in the correct `seq` order, in one transaction; a failure part-way stores nothing.
- Validation: mismatched call/result ids, too many exchanges, duplicate ids.
- Truncation of long argument strings (still valid JSON) and long results; `truncated` flag; the gateway enforces limits even if the agent did not.
- The unique-reply index still rejects a second final reply (409) but allows many `tool_request` rows.
- Default client listing excludes tool kinds; `includeTools=true` includes them; the `after` parameter works with `seq`.
- Migration of existing rows (`kind`, `seq` backfill) keeps order.
- Untrusted results from earlier turns are stubbed in model-facing replay; trusted ones are replayed; the stub is not applied to the current turn.
- Agent: exchanges collected and posted; a multi-turn conversation where turn 2 sees turn 1's trusted tool results.

**Foundation 3: context**
- Estimation arithmetic; turn grouping; newest-first selection of whole turns only; the current turn always included; `context_too_large` when it alone does not fit.
- The dropped-turns note; the summariser seam replaces it when a test double returns text; `NoSummariser` default.
- `reserveTokens` shifts the budget; the agent window override and default.
- Preflight on chat completions: over-window request → 400 and a `rejected` `llm_calls` row with `estimated_prompt_tokens`; normal calls store the estimate.
- Thread `context` fields update; the agent maps `context_too_large` to `fail`.
- Untrusted stubs are applied before estimation.

**Foundation 4: replay**
- Durable events are stored with increasing ids; deltas are not stored and have no `id:`.
- Reconnect with `Last-Event-ID` (header and query) replays exactly the missed durable events, in order, with no duplicates and no gap even when events arrive during the replay (bus subscribed first, dedupe by id).
- Fresh connect replays nothing and sends `ready` with the baseline id.
- In-flight turn: a reconnect mid-stream receives a `snapshot` equal to the text streamed so far, then continues with deltas.
- A last id below `pruned_through` yields `reset`; pruning respects retention using the fake clock; the global `GET /api/events` stream works the same way and spans threads.
- The CLI resumes after a dropped stream and prints each character exactly once (snapshot trimming).

**Foundation 5: approvals**
- With no subscriber connected, an approval is `pending`, not denied; it can be listed, approved and denied through the API; the agent's long poll wakes.
- An `unattended` message produces an immediately denied approval with `decided_by = system`.
- The sweeper expires approvals on schedule (fake clock), emits `approval.decided`, wakes the agent, and updates metrics.
- Orphan handling: `fail`, claim-timeout requeue and reply posting expire pending approvals for that correlation.
- `IApprovalNotifier` is invoked for pending approvals only, and an exception from it does not affect the request.
- Concurrency: two simultaneous decisions on one approval → exactly one succeeds, one 409.

**Cross-cutting**
- Existing 3.1 and 3.2 tests still pass, adjusted only where a supersession in section 3 requires it (document each adjustment in the PR).
- A test (or log-capture assertion) that none of the new code paths log message content, tool arguments or results, passwords, cookies or CSRF tokens.
- `scripts/verify-isolation.sh` still passes.

## 13. Task order

| # | Task | Done when |
|---|---|---|
| T0 | **Orient (no code changes).** Read the repo, both earlier specs and `docs/tech-debt.md`. Write `docs/pre-3.3-orientation.md`: the real class/endpoint names this spec maps to, every place the repo already deviates from the 3.1/3.2 specs, and any conflict with this spec. If there is a conflict, stop and ask the human. | The orientation note exists and has been reviewed; open questions are resolved |
| T1 | Foundation 4: durable events, `Last-Event-ID` replay, snapshot, `ready`/`reset`, global stream, retention, CLI reconnect | Replay tests pass; manual: kill and restore a CLI stream mid-reply and see no loss |
| T2 | Foundation 5: approvals queue, `unattended`, sweeper, orphan handling, notifier seam, list/get API, CLI | Approval tests pass; tech-debt #11 removed |
| T3 | Foundation 2: message columns and `seq` migration, reply `exchanges`, truncation, untrusted stubbing, client `includeTools`, agent changes | Exchange tests pass; migration verified on a copy of a real database |
| T4 | Foundation 3: context endpoint, window config, preflight, summariser seam, agent switch to the context endpoint, thread context fields, CLI flags | Context tests pass |
| T5 | Foundation 1: users, sessions, login/logout/session/password endpoints, CSRF and origin checks, rate limiting, forwarded headers, CLI `users` commands | Auth tests pass |
| T6 | Telemetry and dashboard additions (section 11); docs: README (concise), tech-debt register (add new items, remove paid ones), roadmap and plan updated with these decisions, supersession notes added to the 3.1 and 3.2 specs | Dashboard JSON imports; docs reviewed |
| T7 | Acceptance walk-through (section 16) with `FakeProvider`, then one real OpenRouter run covering tool history, approval queue and reconnect | Checklist complete; results recorded in `docs/verification-log.md` |

## 14. Out of scope

The web UI itself; passkeys, OIDC or two-factor authentication; multi-user permissions or per-user data separation; password reset by email; real notification channels (ntfy, Telegram, email); summarisation; storing exchanges for turns that fail; a stop/cancel-turn feature; per-agent approval timeouts; WebSockets; CORS; TLS termination in the gateway; agent-to-agent messaging; network tools.

## 15. Known gaps accepted in this phase

- Summarisation is not built; long threads lose their oldest turns (the thread shows how many).
- A failed turn stores no tool exchanges, so after a failure the model does not know what tools it already ran in that turn. The `tool_calls` audit table still records them.
- Token estimates use a characters-per-token heuristic; `estimated_prompt_tokens` is stored beside the provider's real count so it can be tuned.
- Login rate-limit counters are in memory and reset on restart.
- Durable events duplicate reply text already stored in messages (bounded by retention).
- A blocked agent: while an approval is pending, the agent processes nothing else (one message at a time), for up to the approval timeout.
- Sessions can only be revoked by logout, password change or an admin password reset; there is no session list UI yet.

## 16. Acceptance checklist

- [ ] A browser-style client (curl with a cookie jar) can log in, call `/api/*` with the CSRF header and a same-origin `Origin`, stream SSE with the cookie, and log out; the same calls without the CSRF token or with a foreign `Origin` return 403.
- [ ] The admin-key bearer still works for the CLI; user management rejects cookies.
- [ ] Wrong-password logins are rate limited, and no log line contains a submitted username or password.
- [ ] After a multi-step tool turn, a second turn in the same thread shows the model remembering the earlier tool use (trusted results replayed).
- [ ] An earlier `workspace_read` result is not replayed on a later turn (stub sent), and a later write in that turn is not tainted by it.
- [ ] `GET …/messages?includeTools=true` shows the full exchange, truncated as configured; the default listing shows only user and final messages.
- [ ] A thread pushed beyond its context window keeps working; the oldest turns are dropped; the thread reports `droppedTurns`; a single oversize turn fails with `context_too_large`.
- [ ] Killing and reconnecting the CLI stream mid-reply loses no text and prints nothing twice; a stale last id yields `reset`.
- [ ] Closing the CLI while an approval is pending leaves it `pending`; `sharpclaw approvals list` shows it; approving it from another terminal lets the agent continue; an unanswered one expires on time and the model is told.
- [ ] An `--unattended` message has its approvals denied immediately.
- [ ] No prompt, reply, tool argument or result, password, cookie, CSRF token or key appears in any log or metric (verified by tests).
- [ ] All automated tests pass (existing and new), the isolation script passes, the dashboard imports, and the docs are updated.
- [ ] No out-of-scope features were added.
