# SharpClaw V3 — Phase 3.1 Implementation Spec

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** ready to implement. Everything below is decided unless listed under "Inputs needed from the human" (section 19).

> **Superseded in part** by the [pre-3.3 foundations spec](sharpclaw-pre-3.3-foundations-spec.md)
> (built): message ordering is by a per-thread `seq` and the unique reply index covers final replies
> only (§5 there); SSE has durable events and `Last-Event-ID` replay (§8); the client API also
> accepts a browser session cookie (§4); the full thread is no longer sent on every call (context
> window, §6). New error codes are listed in the error table below.

## 0. How to use this document

- Implement in the task order given in section 17. Complete and test each task before starting the next. Commit after each task.
- Build **only** what this spec describes. If something is not here, it is out of scope; do not add it. If something is ambiguous or contradictory, stop and ask the human rather than guessing.
- Where this spec says "MUST" or "MUST NOT", treat it as a hard requirement.
- Package versions, internal class names and file-level organisation are yours to choose unless stated.
- Never commit secrets. Never log prompt or reply content, tokens or keys (section 14).
- Before declaring the work done, run the full test suite and walk through the acceptance checklist in section 18.

## 1. Context and scope

SharpClaw V3 is a personal multi-agent system with four parts: a **gateway** (policy, LLM calls, tool calls, audit), **agents** (one Docker container each), a client-facing **API** (served by the gateway process) and a **UI** (not in this phase).

Core principle: the gateway is the single trust boundary. Agents are untrusted workloads. They hold no provider keys, have no direct internet access, and reach the world only through the gateway.

### Phase 3.1 goal

Prove the architecture end to end with the smallest possible system:

> CLI → gateway → agent → gateway → OpenRouter → back to the CLI, streamed, with the agent network-isolated, every LLM call audited, a daily spend cap enforced, and telemetry flowing to Grafana.

### In scope

- Gateway (ASP.NET) with two listeners, SQLite persistence, per-agent token auth, admin key auth
- OpenAI-compatible streaming chat completions endpoint backed by OpenRouter, with a fake provider for testing
- Per-agent daily USD budget, enforced
- Audit log of every LLM call
- Conversation threads and messages stored in the gateway DB; agent inbox via long poll; live streaming to the client via SSE
- Agent: a C# console app running a simple loop in Docker
- CLI: admin and chat client
- OpenTelemetry logs and metrics to the existing Grafana stack (VictoriaMetrics and Loki)
- Docker Compose with a network-isolated agent, plus a script that verifies the isolation
- Automated tests and a starter Grafana dashboard

### Out of scope (do NOT build)

Tools and MCP, web UI, agent-to-agent messaging, context truncation or summarisation, provider retries, scheduled or "wake" agents, human approval flows, multi-user support, the Docker socket or any container management from the gateway, Telegram or Slack, tracing, non-streaming completions, Postgres.

## 2. Technology and conventions

- **.NET:** the current LTS release (.NET 10 at the time of writing). C#, `Nullable` enabled, `ImplicitUsings` enabled, warnings as errors in Release.
- **Gateway:** ASP.NET Core minimal APIs, Entity Framework Core with SQLite, EF migrations applied on startup.
- **Telemetry:** OpenTelemetry .NET SDK with the OTLP exporter.
- **Tests:** xUnit, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Extensions.TimeProvider.Testing`.
- **JSON:** camelCase, enums as strings, no nulls omitted unless noted.
- **IDs:** `Guid.CreateVersion7()`, stored as TEXT.
- **Timestamps:** UTC `DateTime` (Kind = Utc) with a value converter that forces `Kind=Utc` on read. **Do not use `DateTimeOffset`** with the SQLite provider: it cannot order or compare it server-side.
- **Money:** USD as `double`, stored as REAL. Sub-cent tolerance is acceptable; budgets are guard rails, not accounting.
- **Time:** all code that needs "now" takes a `TimeProvider`. Never call `DateTime.UtcNow` directly.
- **SQLite:** WAL mode and `busy_timeout=5000`, set when the connection opens. Single gateway process.
- **Secrets:** environment variables only (`.env` file, gitignored, with a committed `.env.example`).
- **Errors:** one JSON shape everywhere (section 7).

## 3. Repository layout

```
SharpClaw/
  SharpClaw.sln
  README.md
  .gitignore
  .env.example
  src/
    SharpClaw.Contracts/      DTOs, error shapes, constants shared by all projects
    SharpClaw.Gateway/        ASP.NET gateway (both listeners), EF Core, providers, telemetry
    SharpClaw.Agent/          console app: the agent loop
    SharpClaw.Cli/            admin and chat client (assembly name: sharpclaw)
  tests/
    SharpClaw.Gateway.Tests/
    SharpClaw.Agent.Tests/
  docker/
    compose.yml
    Dockerfile.gateway
    Dockerfile.agent
    agents/
      assistant/
        config/soul.md        mounted read-only into the agent
        workspace/            mounted read-write into the agent (git-ignored contents)
    fake-provider/
      script.example.json
    grafana/
      sharpclaw-dashboard.json
  scripts/
    verify-isolation.sh
    telemetry-smoke-test/     throwaway console project (task T1)
```

## 4. Configuration

Gateway configuration comes from `appsettings.json` with environment variable overrides (standard ASP.NET `__` syntax). Defaults shown.

| Key | Default | Notes |
|---|---|---|
| `Listeners:ClientPort` | `8080` | Client API |
| `Listeners:AgentPort` | `8081` | Agent API; never published to the host |
| `Database:Path` | `/data/sharpclaw.db` | SQLite file |
| `Auth:AdminKey` | (required) | Env `Auth__AdminKey`; gateway refuses to start if empty |
| `Provider:Type` | `OpenRouter` | `OpenRouter` or `Fake` |
| `OpenRouter:BaseUrl` | `https://openrouter.ai/api/v1` | |
| `OpenRouter:ApiKey` | (required when Provider is OpenRouter) | Env `OpenRouter__ApiKey` |
| `Provider:ConnectTimeoutSeconds` | `10` | |
| `Provider:IdleTimeoutSeconds` | `60` | Max gap between streamed chunks |
| `Fake:ScriptPath` | (none) | Optional JSON script (section 10) |
| `Fake:CostPerTokenUsd` | `0.000001` | Used when a script rule gives no cost |
| `Inbox:MaxWaitSeconds` | `60` | Upper clamp for long-poll `wait` |
| `Inbox:ClaimTimeoutSeconds` | `300` | Claimed-but-unfinished messages return to `pending` after this |
| `Messages:MaxContentChars` | `32000` | |

Telemetry configuration uses the standard `OTEL_*` environment variables (section 14).

Agent configuration (environment variables):

| Variable | Default | Notes |
|---|---|---|
| `GATEWAY_URL` | (required) | e.g. `http://gateway:8081` |
| `AGENT_TOKEN` | (required) | Issued by the gateway |
| `SOUL_PATH` | `/config/soul.md` | |
| `WORKSPACE_PATH` | `/workspace` | Mounted but unused in 3.1 |
| `LONG_POLL_SECONDS` | `30` | |
| `LOG_LEVEL` | `Information` | |

CLI configuration: `SHARPCLAW_URL` (default `http://localhost:8080`) and `SHARPCLAW_ADMIN_KEY` (required).

## 5. Data model (SQLite via EF Core)

All tables use the conventions in section 2. Use EF migrations; the initial migration is committed.

### `agents`

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| name | text, unique | lowercase, `^[a-z0-9][a-z0-9-]{1,31}$` |
| model | text | OpenRouter model id; the gateway always uses this, ignoring any model in a request |
| daily_budget_usd | double | `>= 0`; `0` means the agent cannot spend |
| enabled | bool | default true |
| created_at | UTC datetime | |

Agents are never deleted in 3.1; disable them instead.

### `agent_tokens`

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| agent_id | Guid FK → agents | |
| token_hash | text, unique | SHA-256 of the full token, lowercase hex |
| prefix | text | first 8 characters of the full token, for identification only |
| created_at | UTC datetime | |
| expires_at | UTC datetime, nullable | |
| revoked_at | UTC datetime, nullable | |
| last_used_at | UTC datetime, nullable | updated at most once per minute per token |

### `threads`

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| agent_id | Guid FK → agents | |
| title | text, nullable | |
| created_at | UTC datetime | |

### `messages`

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| thread_id | Guid FK → threads | |
| agent_id | Guid FK → agents | |
| role | text | `user` or `assistant` |
| content | text | |
| status | text | `pending`, `claimed`, `done`, `failed` |
| correlation_id | Guid, nullable | for an `assistant` message: the id of the `user` message it answers |
| created_at | UTC datetime | |
| claimed_at | UTC datetime, nullable | |
| completed_at | UTC datetime, nullable | |
| fail_reason | text, nullable | short code, never content |

Indexes: `(agent_id, role, status, created_at)` for the inbox query; `(thread_id, created_at)`; **unique** on `correlation_id` filtered to `role = 'assistant'` (enforces one reply per user message).

Status rules: `user` messages move `pending → claimed → done | failed` (and `claimed → pending` on claim timeout). `assistant` messages are created with status `done`.

The inbox is simply `user` messages with status `pending` for an agent. Keep this table shape: Phase 3.5 will extend it for agent-to-agent messaging.

### `llm_calls` (append-only)

| Column | Type | Notes |
|---|---|---|
| id | Guid PK | |
| agent_id | Guid FK → agents | |
| thread_id | Guid | |
| correlation_id | Guid | the user message id being answered |
| model | text | |
| status | text | `ok`, `error`, `cancelled`, `rejected` |
| prompt_tokens | int | 0 when unknown |
| completion_tokens | int | 0 when unknown |
| cost_usd | double | 0 when unknown or rejected |
| usage_known | bool | false when the provider reported no usage |
| latency_ms | int | request start to stream end |
| ttft_ms | int, nullable | time to first content delta |
| error_code | text, nullable | e.g. `budget_exceeded`, `provider_error`, `provider_timeout` |
| error_detail | text, nullable | short, never contains prompt or reply content |
| created_at | UTC datetime | when the call started |

Indexes: `(agent_id, created_at)` (daily spend query), `(correlation_id)`.

`llm_calls` rows are never updated after being finalised and never deleted.

## 6. Authentication

### Admin key (client listener)

- Sent as `Authorization: Bearer <key>`.
- Compare using `CryptographicOperations.FixedTimeEquals` over the SHA-256 hashes of both values (so lengths match).
- Missing or wrong → 401 `unauthorized`.

### Agent tokens (agent listener)

- Format: `sc_` + base64url (no padding) of 32 bytes from `RandomNumberGenerator`. The full value is shown **once**, in the response that creates it. Only the hash and prefix are stored.
- Verification: compute SHA-256 hex of the presented token, look up `token_hash`. Valid only if the token is not revoked, not expired, and its agent is `enabled`.
- Unknown, revoked or expired token → 401 `unauthorized`. Valid token but disabled agent → 403 `agent_disabled`.
- On success, set the authenticated agent id on the request context. Agents MUST NOT be able to read or write another agent's threads or messages (404 `not_found`, not 403, for foreign ids).
- Increment the `sharpclaw.auth.failures` metric on every failure, labelled by listener.

## 7. Listeners, routing and errors

### Two listeners, one process

Configure Kestrel with two endpoints: client (port 8080) and agent (port 8081). Route groups are separated by the **local port the connection arrived on** (`HttpContext.Connection.LocalPort`), not by the `Host` header (clients control that header). A request to a route group on the wrong port returns 404 `not_found`.

- Client routes: everything under `/api/*`, plus `GET /healthz`.
- Agent routes: everything under `/agent/*` and `/v1/*`, plus `GET /healthz`.
- `GET /healthz` is unauthenticated and returns `200 {"status":"ok"}` once migrations have been applied.

### Error shape

All errors, including those from `/v1/chat/completions`, use:

```json
{ "error": { "code": "budget_exceeded", "message": "Daily budget exhausted. Resets at 2026-10-26T00:00:00Z." } }
```

| HTTP | code | When |
|---|---|---|
| 400 | `validation_failed` | bad input; `message` says which field |
| 400 | `unsupported` | a feature outside 3.1 (e.g. `stream: false`, `tools`) |
| 400 | `context_too_large` | (pre-3.3) the turn or request does not fit the agent's context window |
| 401 | `unauthorized` | missing, invalid, revoked or expired credentials |
| 403 | `agent_disabled` | agent token valid but agent disabled |
| 403 | `csrf_failed` | (pre-3.3) cookie-authenticated unsafe request without a valid origin or CSRF token |
| 404 | `not_found` | unknown id, or an id owned by another agent, or wrong listener |
| 409 | `conflict` | e.g. duplicate agent name, reply already exists |
| 429 | `budget_exceeded` | daily budget used up; includes a `Retry-After` header (seconds until next UTC midnight) |
| 429 | `loop_limit_exceeded` | (3.2) the per-message LLM call backstop was reached |
| 429 | `rate_limited` | (pre-3.3) too many failed logins; includes `Retry-After` |
| 502 | `provider_error` | upstream failure before streaming started |
| 504 | `provider_timeout` | upstream connect or idle timeout before streaming started |
| 500 | `internal_error` | anything else; never include stack traces or content in `message` |

Errors that occur **after** a streamed response has started cannot change the status code; see section 11.

## 8. API: client listener (admin key)

All routes require the admin key except `/healthz`. Request and response bodies are JSON.

### Agents

**`POST /api/agents`**
Request: `{ "name": "assistant", "model": "<openrouter-model-id>", "dailyBudgetUsd": 1.0 }`
- Validate name format and uniqueness (409 `conflict` if taken), non-empty model, budget `>= 0`.
- Creates the agent **and its first token** in one transaction.
- Response `201`:
```json
{
  "agent": { "id": "…", "name": "assistant", "model": "…", "dailyBudgetUsd": 1.0, "enabled": true, "createdAt": "…" },
  "token": { "id": "…", "value": "sc_…", "prefix": "sc_AbCd1", "expiresAt": null }
}
```
`token.value` appears in this response only.

**`GET /api/agents`** → `{ "agents": [ <agent with todaySpendUsd and remainingUsd> ] }`
**`GET /api/agents/{idOrName}`** → one agent, same shape. 404 if unknown.
**`PATCH /api/agents/{idOrName}`** — any of `model`, `dailyBudgetUsd`, `enabled`. Returns the updated agent.

`todaySpendUsd` is the sum of `llm_calls.cost_usd` for the agent since the start of the current UTC day; `remainingUsd = max(0, dailyBudgetUsd − todaySpendUsd)`.

### Tokens

**`POST /api/agents/{idOrName}/tokens`** — optional body `{ "expiresAt": "…" }`. Returns `201` with the same `token` object as above (value shown once).
**`GET /api/agents/{idOrName}/tokens`** → token metadata only (id, prefix, createdAt, expiresAt, revokedAt, lastUsedAt). Never the hash or value.
**`DELETE /api/agents/{idOrName}/tokens/{tokenId}`** → `204`; sets `revoked_at`. Idempotent.

### Threads and messages

**`POST /api/threads`** — `{ "agentId": "…" }` (id or name accepted) and optional `"title"`. Returns `201 { "thread": { "id", "agentId", "title", "createdAt" } }`.
**`GET /api/threads?agentId=…`** → recent threads, newest first, max 50.
**`GET /api/threads/{id}/messages?after=<messageId>`** → `{ "messages": [ { id, role, content, status, correlationId, createdAt } ] }` ordered by `created_at, id`. `after` returns only messages created after that message.

**`POST /api/threads/{id}/messages`** — `{ "content": "…" }`
- Content non-empty and at most `Messages:MaxContentChars`.
- Creates a `user` message with status `pending`, wakes any long-polling agent for that thread's agent (section 8, inbox).
- Returns `202 { "message": { "id", "status": "pending", "createdAt" } }`.
- The agent being disabled does not block queuing.

**`GET /api/threads/{id}/stream`** — Server-Sent Events, described in section 9.

### Audit

**`GET /api/audit/llm-calls?agent=<name>&threadId=…&since=<utc>&limit=100`** → `{ "calls": [ <llm_calls row as camelCase JSON> ] }`, newest first, `limit` max 500.

## 8b. API: agent listener (agent token)

### Inbox (long poll)

**`GET /agent/inbox?wait=30`**
- `wait` is clamped to `Inbox:MaxWaitSeconds`; default 30.
- First, requeue this agent's `claimed` messages whose `claimed_at` is older than `Inbox:ClaimTimeoutSeconds` back to `pending` (crash recovery).
- Then atomically claim the oldest `pending` `user` message for the authenticated agent. The claim MUST be a single atomic statement so two concurrent pollers can never receive the same message, for example:
```sql
UPDATE messages SET status = 'claimed', claimed_at = @now
WHERE id = (SELECT id FROM messages
            WHERE agent_id = @agent AND role = 'user' AND status = 'pending'
            ORDER BY created_at, id LIMIT 1)
  AND status = 'pending'
RETURNING id, thread_id, content, created_at;
```
- Found → `200 { "message": { "id", "threadId", "content", "createdAt" } }`.
- Nothing found: wait for a wake-up signal for this agent (an in-process per-agent signal raised when a message is posted) or for 2 seconds, whichever is first, then retry, until `wait` expires → `204 No Content`.
- Honour request cancellation promptly.

### Thread history

**`GET /agent/threads/{id}/messages`** → same shape as the client route. 404 if the thread does not belong to this agent. Returns the whole thread in order.

### Chat completions

**`POST /v1/chat/completions`** — OpenAI-compatible, streaming only. Behaviour in section 11.

### Reply and failure

**`POST /agent/threads/{id}/messages`** — `{ "content": "…", "correlationId": "<user message id>" }`
- The correlation message must exist, belong to this agent and this thread, and have status `claimed`. Otherwise 404 `not_found` (or 409 `conflict` if it is already `done`).
- In one transaction: insert the `assistant` message (status `done`, `correlation_id` set, `completed_at` now) and mark the user message `done`.
- A second reply for the same `correlationId` → 409 `conflict` (the unique index guards this).
- Publishes a `message.completed` event (section 9).
- Returns `201 { "message": { "id", "createdAt" } }`.

**`POST /agent/messages/{id}/fail`** — `{ "reason": "<short code>" }`
- The message must be this agent's `claimed` user message; marks it `failed` with `fail_reason`.
- Publishes an `error` event (section 9). Returns `204`.
- `reason` is a short code such as `budget_exceeded`, `provider_error`, `agent_error`. Reject content longer than 100 characters (400 `validation_failed`).

## 9. Server-Sent Events (client stream)

`GET /api/threads/{id}/stream` returns `text/event-stream` with `Cache-Control: no-cache`. Events (each `data` is a single JSON line):

```
event: delta
data: {"correlationId":"…","content":"Hel"}

event: message.completed
data: {"correlationId":"…","messageId":"…","content":"Hello there"}

event: error
data: {"correlationId":"…","code":"budget_exceeded","message":"…"}
```

Rules:
- Send a comment line (`: keepalive`) every 15 seconds.
- Events are published to subscribers of that thread only. Implement with an in-memory per-thread pub/sub (`IThreadEventBus`); each subscriber has its own `Channel<T>` and is removed when the client disconnects.
- There is **no replay**. A client that connects late misses earlier events and should read history via `GET …/messages`. The CLI therefore opens the stream **before** posting a message.
- `delta` events are published by the chat completions endpoint (section 11) as content streams through. `message.completed` is published by the reply endpoint. `error` is published only by the `fail` endpoint (so each failure produces exactly one `error` event).

## 10. Providers

```csharp
public interface IChatProvider
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ProviderChatRequest request, CancellationToken ct);
}
```

`ChatStreamEvent` is one of: `Delta(string text)`, `Usage(int promptTokens, int completionTokens, double? costUsd)`, `Done(string finishReason)`. Failures are thrown as `ProviderException(kind, httpStatus?, shortMessage)` where kind is `Upstream`, `Timeout` or `Cancelled`. `ProviderChatRequest` carries: model, messages (role + content), and the optional `temperature`, `maxTokens`, `topP`, `stop`.

Select the implementation with `Provider:Type`.

### OpenRouterProvider

- `POST {BaseUrl}/chat/completions` with `Authorization: Bearer <key>`, a `X-Title: SharpClaw` header, `stream: true`.
- Request usage reporting by sending **both** `"stream_options": {"include_usage": true}` and `"usage": {"include": true}`. Parse `usage.prompt_tokens`, `usage.completion_tokens` and `usage.cost` (USD) from the final chunk when present. **Verify these field names against the current OpenRouter documentation during implementation**, and tolerate their absence (then `usage_known = false`).
- Parse the upstream SSE stream: ignore comment lines (e.g. `: OPENROUTER PROCESSING`), handle `data: [DONE]`, read `choices[0].delta.content` for deltas and `choices[0].finish_reason`.
- A chunk containing an `error` object mid-stream → throw `ProviderException(Upstream, …)`.
- Timeouts: `ConnectTimeoutSeconds` to receive response headers; `IdleTimeoutSeconds` between chunks. Both → `ProviderException(Timeout)`.
- Non-2xx before streaming → `ProviderException(Upstream, status)`. Never copy upstream bodies into logs or client errors if they could contain prompt content; log only status and an upstream error code if present.
- Use a typed `HttpClient` via `IHttpClientFactory`, with `HttpCompletionOption.ResponseHeadersRead`.

### FakeProvider

Purpose: test everything (budgets, streaming, failures, telemetry) without spending money or reaching the network.

- Default behaviour: reply `Fake reply to: <last user message>`, streamed in chunks of 8 characters with no delay. Usage: `promptTokens = ceil(total prompt chars / 4)`, `completionTokens = ceil(reply chars / 4)`, `costUsd = (prompt + completion tokens) × Fake:CostPerTokenUsd`.
- Optional script file (`Fake:ScriptPath`), loaded at startup:

```json
{
  "rules": [
    { "match": "regex tested against the last user message", "response": "text to stream",
      "chunkSize": 4, "delayMs": 20,
      "usage": { "promptTokens": 100, "completionTokens": 50, "costUsd": 0.5 } },
    { "match": "^fail-http$", "error": { "type": "http", "status": 500 } },
    { "match": "^fail-timeout$", "error": { "type": "timeout" } },
    { "match": "^fail-midstream$", "response": "partial", "error": { "type": "midstream", "afterChars": 4 } }
  ]
}
```
- The first matching rule wins; no match falls back to the default behaviour. A rule without `usage` uses the defaults above. A rule may set `"usage": null` to simulate a provider that reports no usage.
- Errors throw `ProviderException` exactly as the real provider would, so the same gateway paths are exercised. `midstream` yields some deltas then throws.

## 11. Chat completions behaviour

`POST /v1/chat/completions` on the agent listener.

**Required headers (in addition to the bearer token):** `X-Correlation-Id` (the claimed user message id) and `X-Thread-Id`. Missing or invalid → 400 `validation_failed`. Both must refer to a claimed message and a thread that belong to the authenticated agent, else 404 `not_found`.

**Request body (OpenAI shape):** `messages` (required, non-empty, roles `system|user|assistant`, string content), `stream` (must be `true`, else 400 `unsupported`), optional `temperature`, `max_tokens`, `top_p`, `stop`. `model` is accepted and **ignored**; the agent's configured model is always used. `tools`, `tool_choice` and any non-string content → 400 `unsupported`. Other unknown fields are ignored.

**Order of operations**
1. Authenticate; check headers and ownership.
2. **Budget check:** `spent = SUM(cost_usd)` for this agent since the start of the current UTC day. If `spent >= daily_budget_usd`: insert an `llm_calls` row with status `rejected`, `error_code = budget_exceeded`, increment `sharpclaw.budget.exceeded`, and return 429 `budget_exceeded` with `Retry-After`. Calls already in flight are not counted; small overshoot is accepted.
3. Create the provider request (agent's model, forwarded sampling params) and start streaming. If the provider throws **before** the first event, return 502 `provider_error` or 504 `provider_timeout` and record the call.
4. Respond `200` with `Content-Type: text/event-stream`, `Cache-Control: no-cache`. Flush after every chunk. Emit OpenAI-style chunks that the gateway builds itself from `ChatStreamEvent`s (do not pass upstream bytes through):
   - first chunk: `{"id":"chatcmpl-<guid>","object":"chat.completion.chunk","model":"<model>","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}`
   - per delta: same envelope with `"delta":{"content":"…"}`
   - on `Done`: a chunk with `"delta":{}` and the finish reason
   - then, if usage is known, a chunk with `"choices":[]` and `"usage":{"prompt_tokens":…,"completion_tokens":…,"total_tokens":…}`
   - finally `data: [DONE]`
   Each line is `data: <json>` followed by a blank line.
5. For every delta, also publish a `delta` event to the thread's SSE subscribers (section 9), with `correlationId` from the header.
6. When the stream ends, finalise the `llm_calls` row: status `ok`, tokens, cost, `usage_known`, `latency_ms`, `ttft_ms`. If no usage was reported, `usage_known = false`, tokens and cost 0, and log a warning.
7. **Mid-stream failure** (provider throws after step 4): write one final chunk `data: {"error":{"code":"provider_error","message":"…"}}`, then `data: [DONE]`, and record the call with status `error`. The agent treats an `error` chunk as a failed call.
8. **Client or agent disconnect** (`RequestAborted`): cancel the upstream request and record the call with status `cancelled`, `usage_known = false`. (Cancelled calls may still cost money upstream; this is a known limitation.)

Telemetry for each call is recorded as described in section 14, regardless of outcome.

## 12. Agent application (`SharpClaw.Agent`)

A .NET console app. It references `SharpClaw.Contracts` and talks only to the gateway's agent listener over HTTP.

### Loop

```
startup:
  read env config; log "agent started" (no secrets)
loop until shutdown:
  msg = GET /agent/inbox?wait=LONG_POLL_SECONDS
    204        -> continue
    401        -> log error, exit with code 1 (token invalid or revoked)
    403        -> log, sleep 30s, continue (agent disabled)
    other/net  -> exponential backoff 1s..30s with jitter, continue
  history = GET /agent/threads/{msg.threadId}/messages
  system  = contents of SOUL_PATH (re-read every turn; default "You are a helpful assistant." with a warning if missing)
  POST /v1/chat/completions  (stream: true)
       headers: X-Correlation-Id = msg.id, X-Thread-Id = msg.threadId
       body.messages = [system] + history mapped to {role, content}
  accumulate delta content from the stream
    429 response            -> fail(msg.id, "budget_exceeded")
    502/504 or error chunk  -> fail(msg.id, "provider_error")
    other exception         -> fail(msg.id, "agent_error")
  on success: POST /agent/threads/{threadId}/messages { content, correlationId = msg.id }
```

Rules:
- Process one message at a time.
- The model is chosen by the gateway; the agent sends no `model` (or any placeholder).
- Do not retry a failed LLM call in 3.1.
- Handle SIGTERM/`CancellationToken`: stop polling and exit. A message left `claimed` is requeued by the gateway after the claim timeout.
- Logging: structured JSON to stdout (`AddJsonConsole`). Fields include `message_id`, `thread_id`, durations and outcome codes. **Never log message content, history, the soul file contents or the token.**
- The `/workspace` mount is available but unused in 3.1. Do not write to it.
- Put the HTTP calls and SSE parsing behind small interfaces so the loop can be unit-tested against a fake gateway.

## 13. CLI (`SharpClaw.Cli`, assembly `sharpclaw`)

Environment: `SHARPCLAW_URL`, `SHARPCLAW_ADMIN_KEY`. Use any lightweight argument parser (`System.CommandLine` is fine). Exit non-zero on errors and print the gateway's error `message`.

| Command | Behaviour |
|---|---|
| `sharpclaw agents create <name> --model <id> --daily-budget <usd>` | Creates agent and first token; prints the agent and the token value with a clear "shown once" warning |
| `sharpclaw agents list` / `agents show <name>` | Table or detail including today's spend and remaining budget |
| `sharpclaw agents set <name> [--model …] [--daily-budget …] [--enabled true\|false]` | PATCH |
| `sharpclaw tokens issue <agent> [--expires <utc>]` | Prints the new token once |
| `sharpclaw tokens list <agent>` / `tokens revoke <agent> <tokenId>` | Metadata only |
| `sharpclaw chat <agent> [--thread <id>]` | Interactive: creates a thread (or reuses one), reads a line, streams the reply to stdout; `/exit` quits |
| `sharpclaw ask <agent> "<text>"` | One-shot chat, prints the reply |
| `sharpclaw audit [--agent <name>] [--limit N]` | Table of recent LLM calls |

Chat flow (important ordering, because streams have no replay):
1. Open `GET /api/threads/{id}/stream` first and wait until the response headers arrive.
2. `POST` the message.
3. Print `delta` contents as they arrive. Finish on the `message.completed` or `error` event whose `correlationId` equals the posted message id; print the error message for `error`.
4. Time out with a clear message if nothing arrives within 120 seconds of the post.

## 14. Telemetry (OpenTelemetry → Grafana)

The gateway is the authoritative source. Agents cannot reach the telemetry backends (no egress), so they only write JSON logs to stdout.

### Export

- OpenTelemetry SDK in the gateway, logs via `ILogger` (with `IncludeScopes = true` and `IncludeFormattedMessage = true`) and metrics.
- OTLP over HTTP/protobuf. Each signal has its own endpoint because the backends differ:
  - Metrics → VictoriaMetrics: `http://<host>:8428/opentelemetry/v1/metrics`
  - Logs → Loki (3.0+): `http://<host>:3100/otlp/v1/logs`
- Standard variables (signal-specific endpoint variables are used as the full URL):
```
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=…
OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=…
OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=cumulative
OTEL_SERVICE_NAME=sharpclaw-gateway
OTEL_RESOURCE_ATTRIBUTES=deployment.environment=home
```
- VictoriaMetrics supports **cumulative temporality only** and protobuf only (not JSON).
- Register an exporter for a signal only when its endpoint variable is set; with none set the gateway runs with console logs only. Export failures MUST NOT affect request handling.
- Console logging (JSON, stdout) is always on, whatever the exporters do.
- Add a `--healthcheck` command-line mode to the gateway binary (section 15); it MUST NOT start telemetry.

### Log attributes

Use `ILogger.BeginScope` so every log line inside a request carries these attributes: `agent` (name), `agent_id`, `thread_id`, `correlation_id`. They MUST be **log attributes, not resource attributes**: Loki stores resource attributes as index labels only for a small selected set, and stores the rest (including log attributes) as structured metadata. This keeps ids searchable (`| correlation_id="…"`) without creating high-cardinality labels. Only stable values (service name, environment, version) are resource attributes.

**Never log or emit as a metric label:** message or reply content, prompts, soul contents, tokens, keys, upstream response bodies.

### Metrics

Meter name `SharpClaw.Gateway`. Names below are the OTel names; how they appear in VictoriaMetrics depends on its naming flags (see the smoke test).

| Name | Instrument | Unit | Labels |
|---|---|---|---|
| `sharpclaw.llm.calls` | Counter | `{call}` | agent, model, status |
| `sharpclaw.llm.tokens` | Counter | `{token}` | agent, model, type (`prompt`/`completion`) |
| `sharpclaw.llm.cost_usd` | Counter (double) | `USD` | agent, model |
| `sharpclaw.llm.duration` | Histogram | `s` | agent, model |
| `sharpclaw.llm.time_to_first_token` | Histogram | `s` | agent, model |
| `sharpclaw.budget.remaining_usd` | Observable gauge | `USD` | agent |
| `sharpclaw.budget.exceeded` | Counter | `{rejection}` | agent |
| `sharpclaw.inbox.depth` | Observable gauge | `{message}` | agent |
| `sharpclaw.message.duration` | Histogram | `s` | agent |
| `sharpclaw.agent.last_seen` | Observable gauge | `s` (Unix time) | agent |
| `sharpclaw.auth.failures` | Counter | `{failure}` | listener (`client`/`agent`) |

Notes:
- Labels are limited to agent name, model, status, type and listener. **Never** label by thread, message, correlation id or token.
- `message.duration` is recorded when a reply is posted: reply time minus the user message's `created_at`.
- Observable gauges query the database in their callbacks (cheap queries; no more than once per collection interval) or read a small in-memory dictionary (`last_seen`, updated on each authenticated agent request).

### Smoke test (task T1, before any feature)

`scripts/telemetry-smoke-test/` is a throwaway console project that, using the same env configuration, emits one counter (`sharpclaw.smoke.counter`) and one log line carrying `agent`, `agent_id`, `thread_id` and `correlation_id` attributes. It proves:
1. the metric arrives in VictoriaMetrics, and the **exact name it lands under** is recorded in `docs/telemetry-notes.md` (dots or underscores, any `_total`/unit suffixes);
2. the log arrives in Loki and `correlation_id` is queryable as structured metadata (not rejected, not turned into a label);
3. both are visible in Grafana Explore.

If Loki rejects the logs, the usual cause is structured metadata not being enabled (needs a recent schema, v13 with TSDB); report that to the human rather than working around it. Record findings in `docs/telemetry-notes.md`.

### Starter dashboard

After the smoke test, create `docker/grafana/sharpclaw-dashboard.json` (importable Grafana dashboard) using the **actual landed metric names**. Panels: LLM calls by status; cost today per agent against its daily budget; call duration and time-to-first-token (p50/p95); tokens by type; inbox depth; agent last-seen age; budget rejections; auth failures; plus a Loki logs panel filtered by `service_name` with a `correlation_id` variable.

## 15. Docker and Compose

### Images

- `Dockerfile.gateway` and `Dockerfile.agent`: multi-stage builds (`sdk` → `aspnet`/`runtime` images), run as a **non-root** user, no secrets baked in.
- The runtime images have no `curl`. The gateway binary supports `--healthcheck`: it performs `GET http://localhost:8080/healthz`, exits 0 on 200 and 1 otherwise. Compose uses that for the gateway healthcheck.
- Migrations run on gateway startup. The SQLite file lives on a named volume at `/data`.

### `docker/compose.yml`

```yaml
name: sharpclaw

networks:
  internal:
    internal: true        # no route to the outside world
  external: {}

volumes:
  gateway-data: {}

services:
  gateway:
    build: { context: .., dockerfile: docker/Dockerfile.gateway }
    environment:
      Auth__AdminKey: ${ADMIN_KEY}
      OpenRouter__ApiKey: ${OPENROUTER_API_KEY}
      Provider__Type: ${PROVIDER_TYPE:-OpenRouter}
      OTEL_EXPORTER_OTLP_PROTOCOL: http/protobuf
      OTEL_EXPORTER_OTLP_METRICS_ENDPOINT: ${OTEL_METRICS_ENDPOINT:-}
      OTEL_EXPORTER_OTLP_LOGS_ENDPOINT: ${OTEL_LOGS_ENDPOINT:-}
      OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE: cumulative
      OTEL_SERVICE_NAME: sharpclaw-gateway
    ports:
      - "127.0.0.1:8080:8080"     # client API only; 8081 is never published
    networks: [internal, external]
    volumes:
      - gateway-data:/data
    healthcheck:
      test: ["CMD", "dotnet", "SharpClaw.Gateway.dll", "--healthcheck"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 15s

  agent-assistant:
    build: { context: .., dockerfile: docker/Dockerfile.agent }
    depends_on:
      gateway: { condition: service_healthy }
    environment:
      GATEWAY_URL: http://gateway:8081
      AGENT_TOKEN: ${AGENT_ASSISTANT_TOKEN}
    networks: [internal]            # internal only
    volumes:
      - ./agents/assistant/config:/config:ro
      - ./agents/assistant/workspace:/workspace
    read_only: true
    tmpfs: [/tmp]
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    restart: unless-stopped
```

Adjust paths and the healthcheck command to whatever works with the final image layout. The `.env.example` lists: `ADMIN_KEY`, `OPENROUTER_API_KEY`, `PROVIDER_TYPE`, `OTEL_METRICS_ENDPOINT`, `OTEL_LOGS_ENDPOINT`, `AGENT_ASSISTANT_TOKEN` (with empty values and comments). The gateway's telemetry endpoints must be reachable from the `external` network.

### Bootstrap procedure (document in the README)

1. Copy `.env.example` to `.env`; set `ADMIN_KEY`, `OPENROUTER_API_KEY` (or `PROVIDER_TYPE=Fake`) and the OTLP endpoints.
2. `docker compose up -d gateway`
3. `sharpclaw agents create assistant --model <id> --daily-budget 1`; copy the token into `.env` as `AGENT_ASSISTANT_TOKEN`.
4. `docker compose up -d agent-assistant`
5. `sharpclaw chat assistant`

Containers are started and stopped manually. The gateway never touches the Docker socket.

### `scripts/verify-isolation.sh`

Verifies the isolation guarantees against a running stack and exits non-zero on failure:
1. From a throwaway container on the **internal** network (e.g. `docker run --rm --network sharpclaw_internal curlimages/curl`): a request to a public site (e.g. `https://example.com`) MUST fail; `http://gateway:8081/healthz` MUST succeed.
2. From the host: `http://127.0.0.1:8081` MUST NOT be reachable (port not published).
3. `GET /api/agents` on port 8081 returns 404 and `GET /agent/inbox` on port 8080 returns 404 (run from the appropriate network).
4. The agent container's root filesystem is read-only and `/config` is not writable.

## 16. Tests

Use a `WebApplicationFactory` with a temporary SQLite file, `FakeProvider`, and a fake `TimeProvider`.

**Automated (must pass in CI-style `dotnet test`)**
- **Auth:** admin key valid/invalid; agent token valid, unknown, revoked, expired; disabled agent returns 403; foreign thread ids return 404; token value is never returned after creation; hashes are SHA-256 hex.
- **Listener separation:** client routes 404 on the agent port and vice versa (use the real Kestrel endpoints or a test server configured with both local ports).
- **Agents API:** create (including duplicate name 409, bad name 400), patch, token issue/revoke/list.
- **Budget:** call allowed under cap; 429 with `Retry-After` once `spent >= cap`; rejected call recorded with status `rejected`; spend comes from `llm_calls`; after advancing the fake clock past UTC midnight the agent can call again; budget `0` always rejects.
- **Inbox:** a message is claimed exactly once with many concurrent pollers; long poll returns promptly when a message arrives; 204 on timeout; stale claims requeue after the claim timeout; reply endpoint rejects a second reply (409) and a wrong agent (404); `fail` marks the message and emits an `error` event.
- **Chat completions:** output is valid OpenAI-style SSE ending in `[DONE]`; usage chunk present when known; `stream:false` and `tools` rejected; the agent's configured model is used regardless of the request model; headers required; deltas also reach SSE subscribers; usage recorded (`usage_known` true/false); provider failure before streaming → 502/504 and a recorded `error` row; failure mid-stream → error chunk then `[DONE]`; client abort → `cancelled` row and upstream cancellation observed.
- **FakeProvider:** scripting, regex matching, simulated errors, default echo.
- **Telemetry:** with an in-memory exporter, a scripted call emits the expected metrics with the right labels and values; log output (captured) contains none of the message content, the token or the admin key; an unreachable OTLP endpoint does not break requests.
- **Agent loop (`SharpClaw.Agent.Tests`):** against a fake gateway: happy path, 204 loop, 429 → `fail(budget_exceeded)`, error chunk → `fail(provider_error)`, 401 → exits, backoff on network errors, soul file re-read each turn, no content in logs.
- **CLI:** at least argument parsing and the chat ordering (stream opened before post) against a fake server.

**Manual (document the results in the README)**
- `scripts/verify-isolation.sh` passes.
- One end-to-end chat with the real OpenRouter provider and a cheap model.

## 17. Task order

Each task ends with passing tests (where applicable) and a commit.

| # | Task | Done when |
|---|---|---|
| T0 | Scaffold: solution, projects, `.gitignore`, `.env.example`, empty README, test projects | `dotnet build` and `dotnet test` succeed |
| T1 | **Telemetry smoke test** (section 14) and `docs/telemetry-notes.md` | Metric and log visible in Grafana; landed metric name and Loki behaviour recorded. If it cannot be made to work, stop and report |
| T2 | Contracts, EF model, initial migration, SQLite setup (WAL, busy timeout), `TimeProvider` plumbing | Migration applies; schema matches section 5 |
| T3 | Two listeners and port-based route separation; `/healthz`; `--healthcheck`; error shape; config | Listener-separation tests pass |
| T4 | Auth (admin key, agent tokens) and the agents/tokens client endpoints | Auth and agents-API tests pass |
| T5 | `FakeProvider` and `IChatProvider`; then `OpenRouterProvider` | Fake tests pass; OpenRouter provider covered by tests against a stubbed HTTP handler |
| T6 | `/v1/chat/completions`: budget check, streaming, `llm_calls`, SSE tee, telemetry | Chat completions, budget and telemetry tests pass |
| T7 | Threads, messages, inbox long poll, reply/fail endpoints, SSE stream endpoint | Inbox and SSE tests pass |
| T8 | Agent app | Agent loop tests pass |
| T9 | CLI | CLI tests pass; manual `ask` works against a locally running gateway on `Provider:Type=Fake` |
| T10 | Dockerfiles, Compose, `verify-isolation.sh`, README bootstrap section | Whole stack runs from `docker compose`; isolation script passes |
| T11 | Grafana dashboard JSON (using real metric names) | Dashboard imports and shows data from a fake-provider run |
| T12 | Acceptance walk-through (section 18) with the fake provider, then one real OpenRouter chat | Checklist complete; results noted in the README |

## 18. Definition of done / acceptance checklist

- [ ] `sharpclaw chat assistant` streams a reply token by token (Fake provider, then real OpenRouter).
- [ ] The agent container can reach nothing except the gateway's agent listener (`verify-isolation.sh` passes).
- [ ] Client routes are 404 on port 8081 and agent routes are 404 on port 8080; 8081 is not published to the host.
- [ ] Exceeding the daily budget stops the agent with a clear error (429, `fail(budget_exceeded)`, error shown in the CLI), and it works again after the UTC day rolls over (proved by a test with a fake clock).
- [ ] Every LLM call, including rejected ones, appears in `sharpclaw audit` with model, tokens, cost, latency and status.
- [ ] A message is never delivered to two pollers, and never receives two replies.
- [ ] The whole stack runs on `Provider:Type=Fake` with no access to OpenRouter.
- [ ] Gateway logs and metrics appear in Grafana; the starter dashboard shows calls, cost against daily budget, latency and inbox depth; a single message can be followed through the logs by `correlation_id`.
- [ ] No prompt/reply content, tokens or keys appear in any log or metric (verified by a test).
- [ ] An unreachable telemetry backend does not affect the gateway.
- [ ] All automated tests pass; the README documents setup, bootstrap, configuration and the manual verification results.
- [ ] No out-of-scope features were added (section 1).

## 19. Inputs needed from the human

The implementer should ask for these when first needed, and must not invent them:

- The OTLP endpoints for VictoriaMetrics and Loki (hosts, ports, any auth headers or tenant id), and confirmation that the Docker host can reach them. Loki version and its structured metadata setting are discovered by the smoke test.
- The VictoriaMetrics naming flags in use (discovered by the smoke test).
- An OpenRouter API key, and the model id for the first agent (pick something cheap for testing).
- The admin key value (any strong random string).
- The contents of `docker/agents/assistant/config/soul.md` (a placeholder persona is fine until then).

## 20. Known gaps (accepted for 3.1)

- Full thread history is sent on every LLM call; no truncation or summarisation, so very long threads will eventually fail with a provider error.
- No provider retries; a transient provider failure fails the message.
- A small budget overshoot is possible with concurrent or in-flight calls; cancelled calls may incur upstream cost that is not recorded.
- No SSE replay; clients must read history for missed events.
- A single gateway process and a single SQLite file; no high availability.
- Streaming only; no tools; no traces.
