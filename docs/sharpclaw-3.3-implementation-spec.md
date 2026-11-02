# SharpClaw V3 — Phase 3.3 Implementation Spec: Client API and Web UI

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** ready to implement once the defaults in section 2 are confirmed or overridden.
**Prerequisite:** the Pre-3.3 Foundations spec (`sharpclaw-3.3-pre-foundations-spec.md`) is implemented and merged: session-cookie auth with CSRF, tool exchanges in thread history (`includeTools`), context info on threads, durable SSE events with replay and the global `/api/events` stream, and the approvals queue. Task T0 verifies this; if any of it is missing, stop and tell the human.

## 0. How to use this document

- **T0 is mandatory and first.** You have the repository; this spec does not. Endpoint, event and class names are illustrative where they refer to existing code. Read the code and the earlier specs, map this spec onto what exists, and report conflicts before changing anything.
- Implement in the task order in section 13. One branch and pull request per task, with passing tests, before the next starts.
- "MUST" / "MUST NOT" are hard requirements.
- Build only what is described. Anything not here is out of scope (section 14). If something is ambiguous or contradictory, stop and ask the human.
- Standing rules still apply: never log or emit as a metric label any prompt, reply, tool argument or result, soul contents, token, key, password, cookie or CSRF token. The UI MUST NOT send any such content anywhere except to the gateway.
- Keep the README concise. Put UI developer notes in `docs/ui.md`. Do not add dated verification logs or duplicate configuration tables to the README.

## 1. Goal and scope

Replace the CLI as the day-to-day interface with a web UI, served by the gateway, reachable from the home LAN, with feature parity for everything the CLI does interactively, plus the visibility the CLI cannot give: tool activity, approvals, context state and a per-thread trace.

> Log in from a browser (desktop or phone) → pick an agent → chat with streamed replies → see tool use inline → approve or deny risky actions → inspect cost, latency and tool decisions for any turn → manage agents and tokens → survive reconnects, reloads and session expiry gracefully.

### In scope

- Client API additions the UI needs (section 5), an OpenAPI document and a generated typed client
- Static hosting of the UI by the gateway on the client listener, with security headers
- The UI (React + TypeScript + Vite): login, chat, tool and approval handling, agents and tokens admin, approvals inbox, audit and trace views, account page
- Responsive layout that works on a phone
- Tests: gateway tests for the API additions, UI unit/integration tests, and a Playwright end-to-end suite against the Compose stack with the fake provider
- Docker build integration and developer workflow

### Out of scope (do NOT build)

See section 14. Notably: stop/cancel turn, message editing or regeneration, attachments, thread deletion, search, multi-user features, push notifications and PWA, TLS, editing `soul.md` or tool policy from the UI, frontend telemetry.

## 2. Decisions and defaults

| Decision | Value |
|---|---|
| UI stack | React + TypeScript (Vite). Decided by the human. |
| Access | **Home LAN only**, plain HTTP. Decided by the human. Consequences in section 3. |
| Hosting | The gateway serves the built UI from its client listener (same origin). One extra container is not needed. |
| Styling | Tailwind CSS with Radix UI primitives for accessible headless components (dialogs, menus, tabs, toasts). No other component library. |
| Server state | TanStack Query. Client routing: React Router. Minimal additional client state (React state/context; no Redux). |
| API client | `openapi-typescript` (types) + `openapi-fetch` (thin client) generated from a committed OpenAPI snapshot. SSE is hand-typed. |
| Markdown | `react-markdown` + `remark-gfm`. Raw HTML disabled. Remote images never loaded (section 9.6). |
| Tests | Vitest + React Testing Library + MSW for UI tests; Playwright for end-to-end. |
| Package manager | npm, lockfile committed, Node LTS pinned in `.nvmrc` and the Docker build. |
| Browsers | Current and previous major versions of Chrome, Safari (including iOS) and Firefox. |
| Theme | Follows the system light/dark setting. No manual toggle in 3.3. |
| One SSE connection per tab | Use the global `/api/events` stream only (reason in section 3). |
| Existing, unchanged | SQLite, EF Core, UTC `DateTime`, `TimeProvider`, the error shape, port-based listener separation, per-agent tokens, the foundations' auth and event protocol. |

## 3. Consequences of "home LAN only, plain HTTP"

These shape the design and MUST be handled as stated.

1. **No secure context.** Over `http://<lan-host>` the browser does not treat the page as a secure context, so `navigator.clipboard`, service workers, Web Push, `crypto.subtle` and `crypto.randomUUID()` are unavailable (except on `localhost`). The UI MUST NOT depend on any of them. Copy buttons use a fallback (select the text and `document.execCommand('copy')`, and always show the value selectable). Client-side ids use a small non-crypto generator; the server is the source of truth for ids. PWA install, notifications and offline support are out of scope.
2. **HTTP/1.1 only.** Kestrel serves HTTP/2 only over TLS, so browsers allow just 6 concurrent connections per host. A long-lived `EventSource` consumes one. The UI therefore opens **exactly one** SSE connection per tab (the global stream), never one per thread, and the API calls stay short. Document that roughly four tabs is the practical limit.
3. **Credentials cross the LAN in clear text.** The password and session cookie can be sniffed by anyone on the network. This is an accepted risk for a home network. The spec MUST NOT weaken the other protections (cookie flags, CSRF, rate limiting). Record the risk and the future fix (a local TLS reverse proxy, which would also unlock HTTP/2 and secure-context features) in `docs/tech-debt.md`.
4. **Binding.** The client port must be reachable from other LAN devices. Change the Compose publication to `${CLIENT_BIND:-127.0.0.1}:8080:8080` and document setting `CLIENT_BIND` to the host's LAN address (or `0.0.0.0`). Port 8081 stays unpublished. Never port-forward 8080 from the router; say so in the README.
5. **Cookies.** `Auth:Session:CookieSecure=Auto` will not set `Secure` on plain HTTP; that is expected. `SameSite=Strict`, `HttpOnly`, the origin check and CSRF token all still apply.
6. **No third-party network requests.** The UI MUST NOT load fonts, scripts, styles, images or analytics from any other origin. Use the system font stack, bundle all assets, and enforce with CSP (section 6).

## 4. Repository layout additions

```
SharpClaw/
  docs/
    openapi/sharpclaw-client.v1.json     committed OpenAPI snapshot (source for the generated client)
    ui.md                                UI developer guide (short)
    3.3-orientation.md                   output of T0
  ui/
    package.json  package-lock.json  .nvmrc
    vite.config.ts  tsconfig.json  eslint config  prettier config
    index.html
    src/
      main.tsx  App.tsx  routes/
      api/            generated types, client wrapper, error mapping, SSE stream manager, event types
      features/       chat/ agents/ approvals/ audit/ account/
      components/     shared UI components
      lib/            time, ids, clipboard fallback, markdown renderer
      styles/
    tests/            unit and integration tests
    e2e/              Playwright tests and fixtures
  docker/
    Dockerfile.gateway                   gains a Node build stage that produces wwwroot
```

The gateway project gains a `wwwroot` folder at publish time only (built output is not committed).

## 5. Client API additions (gateway)

All routes are on the client listener and accept the admin key or a session cookie (CSRF rules from the foundations apply to cookie-authenticated unsafe methods). JSON is camelCase. Errors use the shared error shape. Use the real names of existing tables, columns and DTOs found in T0; the shapes below are the target.

### 5.1 Threads

**Schema (`threads`):** add `last_message_at` (UTC datetime), `last_preview` (text, at most 120 characters, whitespace collapsed), `archived` (bool, default false). Index `(archived, last_message_at)`. Backfill from existing `kind = 'message'` rows (use `created_at` of the thread when there are none). Update `last_message_at` and `last_preview` whenever a `kind = 'message'` row is inserted (the user post and the final reply). **Auto-title:** when the first user message is posted and `title` is null, set the title to its first 60 characters (whitespace collapsed).

**`GET /api/threads?agentId=<id|name>&archived=false&limit=50&cursor=<opaque>`**
```json
{
  "threads": [
    { "id": "…", "agentId": "…", "agentName": "assistant", "title": "…", "archived": false,
      "createdAt": "…", "lastMessageAt": "…", "preview": "…",
      "pendingApprovals": 0, "inFlight": false, "queued": 0 }
  ],
  "nextCursor": "…" 
}
```
Newest `lastMessageAt` first; `limit` max 100; `cursor` is opaque (encode `lastMessageAt` and id). `pendingApprovals`, `inFlight` (a claimed user message exists) and `queued` (pending user messages) MUST be computed for the whole page with grouped queries, not per thread.

**`GET /api/threads/{id}`**
```json
{
  "thread": { "id", "agentId", "title", "archived", "createdAt", "lastMessageAt",
              "context": { "droppedTurns": 0, "estimatedTokens": 0, "windowTokens": 32000 } },
  "agent": { "id", "name", "model", "enabled", "dailyBudgetUsd", "todaySpendUsd", "remainingUsd" },
  "inFlight": { "correlationId": "…", "claimedAt": "…" },
  "queued": 0,
  "pendingApprovals": [ { "id": "…", "toolName": "…", "risk": "…", "summary": "…", "createdAt": "…", "expiresAt": "…" } ]
}
```
`inFlight` is null when idle.

**`PATCH /api/threads/{id}`** — `{ "title"?: string, "archived"?: bool }`. Title: trimmed, 1 to 120 characters. Returns the thread. Publishes `thread.updated`.

**`POST /api/threads`** (existing) now also publishes `thread.created`.

### 5.2 Message pagination

**`GET /api/threads/{id}/messages?includeTools=true&limit=100&beforeSeq=<n>&afterSeq=<n>`**
- Without a cursor: the **latest** `limit` rows, ascending by `seq`. `beforeSeq`: the `limit` rows before that seq (for "load earlier"). `afterSeq`: rows after that seq. `limit` max 500; counts all returned kinds.
- Response adds `"hasMoreBefore": true|false`. The existing `after=<messageId>` parameter keeps working.
- The UI always requests `includeTools=true` and groups rows itself.

### 5.3 Thread trace

**`GET /api/threads/{id}/trace`** returns one entry per user turn, oldest first, joining records by the correlation id:
```json
{
  "turns": [
    {
      "correlationId": "…", "userMessageId": "…", "status": "done",
      "failReason": null, "claimedAt": "…", "completedAt": "…",
      "llmCalls": [ { "id", "model", "status", "promptTokens", "completionTokens", "estimatedPromptTokens",
                      "costUsd", "usageKnown", "latencyMs", "ttftMs", "errorCode", "createdAt" } ],
      "toolCalls": [ { "id", "toolName", "risk", "decision", "outcome", "approvalId", "durationMs",
                       "createdAt", "reportedAt" } ],
      "approvals": [ { "id", "toolName", "risk", "status", "createdAt", "decidedAt", "decidedBy" } ],
      "totals": { "costUsd": 0.0, "promptTokens": 0, "completionTokens": 0, "llmCalls": 1, "toolCalls": 2 }
    }
  ]
}
```
- Metadata only. The trace MUST NOT include prompts, replies, tool arguments or results (those are available through the messages endpoint). Use the actual column names from the 3.2 `tool_calls` and `approvals` tables; omit fields that do not exist rather than adding schema solely for this endpoint.
- Limit to the most recent 100 turns.

### 5.4 Tool-call audit

**`GET /api/audit/tool-calls?agent=<name>&threadId=…&status=…&since=<utc>&limit=100&before=<cursor>`** → `{ "calls": [ … ], "nextCursor": … }`, newest first, `limit` max 500. If a tool-call audit endpoint already exists from 3.2, reuse and extend it (same filters, cursor paging). Add `before=<cursor>` cursor paging to `GET /api/audit/llm-calls` as well, so both audit tables page the same way.

### 5.5 System info

**`GET /api/system`** → `{ "version": "…", "provider": "OpenRouter"|"Fake", "utcNow": "…", "budgetResetAtUtc": "…" }`. `version` comes from the assembly informational version (set from the git short sha at build time with a Docker build arg; `dev` locally). `budgetResetAtUtc` is the next UTC midnight. Used by the UI footer, so a Fake-provider stack is never mistaken for a real one.

### 5.6 Agent last seen

Add `agents.last_seen_at` (nullable UTC datetime), updated at most once a minute on any authenticated agent-listener request. Expose `lastSeenAt` on the agent DTOs.

### 5.7 Global event stream for the UI

The foundations' global stream `GET /api/events` is extended:
- It carries **all** events, durable and ephemeral (`delta`, `snapshot`), for **all** threads. Volume is small (a personal system).
- **Every event payload MUST include `threadId`** (additive change; per-thread streams get it too).
- New durable events: `thread.created` `{ "threadId", "agentId" }` and `thread.updated` `{ "threadId", "title", "archived" }`.
- On connect it sends `ready`, then a `snapshot` for every in-flight turn across threads.
- Compression middleware MUST NOT touch `text/event-stream` (it breaks streaming).

### 5.8 Usage (optional; task T9)

**`GET /api/usage/daily?days=14&agent=<name>`** → `{ "days": [ { "date": "YYYY-MM-DD", "agent": "…", "calls": 0, "promptTokens": 0, "completionTokens": 0, "costUsd": 0.0 } ] }`, UTC days, aggregated from `llm_calls`; `days` max 90.

### 5.9 Not used by the UI

The UI always posts messages with `unattended = false`. The admin-only user-management endpoints are not used by the UI (user creation stays a CLI action).

## 6. Static hosting and security headers

- The client listener serves the built UI from `wwwroot`. The agent listener MUST NOT serve it (404).
- **SPA fallback:** a `GET` or `HEAD` request whose path does not begin with `/api`, `/openapi` or `/healthz`, and that has no file extension, returns `index.html`. A missing path **with** an extension (a missing asset) returns 404, not `index.html`.
- **Caching:** `index.html` → `Cache-Control: no-cache`. Files under `assets/` (hashed by Vite) → `public, max-age=31536000, immutable`. All `/api/*` and `/openapi/*` responses → `Cache-Control: no-store`.
- **Compression:** gzip/brotli for text assets and JSON; never for `text/event-stream`.
- **Headers on every client-listener response:**

| Header | Value |
|---|---|
| `Content-Security-Policy` (HTML responses) | `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self'; manifest-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Referrer-Policy` | `no-referrer` |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=(), payment=()` |
| `Cross-Origin-Opener-Policy` | `same-origin` |
| `Cross-Origin-Resource-Policy` | `same-origin` |

- The CSP starts strict (no inline scripts or styles). If the Playwright suite shows violations caused by a component library setting inline style **attributes**, add only `style-src-attr 'unsafe-inline'` and document why in `docs/ui.md`. Never add `unsafe-inline` for scripts or `<style>` elements.
- The gateway MUST NOT send `Strict-Transport-Security` or `upgrade-insecure-requests`: the site is plain HTTP on the LAN and both would break it.
- The Vite build MUST NOT emit inline scripts (check `index.html` in a test).

## 7. OpenAPI and generated client

- Use the built-in ASP.NET OpenAPI support. Document name `v1`, served at `GET /openapi/v1.json` on the client listener, authenticated like other `/api/*` routes. Include only client routes (never agent routes).
- Stable `operationId`s (set explicit endpoint names), tags per area (auth, agents, tokens, threads, messages, approvals, audit, system), request and response schemas from `SharpClaw.Contracts`, the shared error object on 4xx/5xx responses, and security schemes for the session cookie, the bearer admin key and the `X-CSRF-Token` header.
- SSE endpoints are documented as `text/event-stream` responses with a description pointing to the event table in `docs/ui.md`.
- **Snapshot:** the committed file `contracts/openapi/sharpclaw-client.v1.json` is the contract. A gateway test generates the document, normalises it (stable key order), and compares it with the snapshot; on mismatch it fails with instructions. Setting `UPDATE_OPENAPI=1` rewrites the snapshot.
- **UI:** `npm run gen:api` runs `openapi-typescript` against the snapshot and writes `ui/src/api/schema.d.ts`, which is committed. `npm run check:api` regenerates and fails on any diff; CI runs it.
- **Event fixtures:** a gateway test writes one sample payload per event type to `contracts/events/<event-type>.json` and compares against the committed files (same `UPDATE_` switch). UI tests parse these fixtures to keep the hand-written event types honest.

## 8. Live updates in the UI

### 8.1 One stream per tab

The UI opens one `EventSource('/api/events')` after authentication. All live behaviour (streamed text, tool activity, approvals, other threads' updates) comes from it. Do not open per-thread streams.

### 8.2 `EventStreamManager`

A single module that owns the connection and exposes: typed event subscription, connection state (`connecting`, `open`, `reconnecting`, `signed_out`), and `lastEventId`.

- Record `lastEventId` from every durable event (a non-empty `MessageEvent.lastEventId`).
- The browser's built-in automatic reconnect handles transient drops and re-sends `Last-Event-ID`.
- If the `EventSource` ends up `CLOSED` (it received a non-200 response): call `GET /api/auth/session`. If unauthenticated, clear all cached data and go to the login page. Otherwise reconnect with a **new** `EventSource` at `/api/events?lastEventId=<last>` (a new instance does not send the header), with backoff 1 s, 2 s, 5 s, then 10 s.
- **Visibility:** when the tab becomes visible after being hidden for more than 10 seconds, close and reopen the stream with `lastEventId` (this also cures half-open connections on phones; replay plus idempotent handlers make it safe). Also reconnect on the browser `online` event.
- On `reset`: invalidate every query, forget `lastEventId`. On `ready` after a reconnect: invalidate the thread list, the open thread and the approvals queries as a safety net.
- All handlers MUST be idempotent (dedupe by message id, approval id and event id).
- Show connection state in the shell (section 9.1).

### 8.3 Event to UI state

| Event | Effect |
|---|---|
| `thread.created` | invalidate thread list |
| `thread.updated` | patch the thread in list and detail caches |
| `message.created` | invalidate the thread's messages, list and detail |
| `delta` | append to the per-turn streaming buffer keyed by `correlationId` (not in the query cache) |
| `snapshot` | replace that buffer's content |
| `tool.requested` | add a live tool-activity row for the turn (using the payload fields defined in 3.2) |
| `message.completed` | clear the buffer and live tool rows for the turn; invalidate messages (with tools), list, detail and trace |
| `error` | clear the buffer; invalidate messages and detail; show a notice if the user is viewing the thread |
| `approval.requested`, `approval.decided` | invalidate approvals queries and the thread detail |

## 9. UI specification

### 9.1 Routes, shell and authentication

| Route | Page |
|---|---|
| `/login` | Sign in |
| `/` | redirects to `/chats` |
| `/chats`, `/chats/:threadId` | Thread list and thread view (`?panel=trace` opens the trace panel) |
| `/approvals` | Approvals inbox |
| `/agents`, `/agents/:name` | Agent list and detail |
| `/audit` | Audit tables (`?tab=llm` or `?tab=tools`, plus filter params) |
| `/account` | Account and system info |
| `/usage` | Optional (T11) |
| `*` | Not-found page |

Every route is deep-linkable and survives reload.

**Authentication gate**
- On load call `GET /api/auth/session`. Unauthenticated → `/login?next=<path>`. After login go to `next` only if it is a same-site relative path (starts with a single `/`, not `//`, no scheme); otherwise `/chats`.
- Keep the CSRF token **in memory only** and send it as `X-CSRF-Token` on every non-GET request. Never put the CSRF token, tokens or any credential in `localStorage`, `sessionStorage`, URLs or logs.
- A `401` from any call → clear all cached data, close the event stream, go to the login page (debounce so several failing calls cause one redirect). A `403 csrf_failed` → re-fetch the session; if still authenticated retry the request once, otherwise treat it as signed out.
- Login form: username, password, clear error text (the server message is deliberately generic), `429 rate_limited` shows the wait time from `Retry-After`. Autofill-friendly attributes (`autocomplete="username"` and `current-password`).

**Shell**
- Desktop: a left sidebar with navigation; on `/chats` a thread-list column and the thread view beside it; an optional right panel (trace).
- Mobile: a top bar with a navigation drawer; the thread list and thread view are separate screens with a back button.
- Nav items: Chats, Approvals (with a pending-count badge), Agents, Audit, Account.
- A connection indicator (from the stream manager): `Live`, `Reconnecting…`, `Offline`. A small footer with the gateway version and provider from `GET /api/system`; when the provider is `Fake`, show a prominent "Fake provider" badge in the header.
- The browser tab title is `SharpClaw`, prefixed with `(N)` when N approvals are pending.
- Compute a server clock offset from `utcNow` in `GET /api/system` and use it for countdowns (approval expiry, budget reset).

### 9.2 Chat

**Thread list**
- Sorted by `lastMessageAt`, newest first; "Load more" using the cursor. Filter by agent; a toggle to show archived threads.
- Each row: title, agent name chip, preview, relative time, and badges for in-flight (spinner), pending approvals and queued messages.
- "New chat": choose an agent (enabled agents only, showing remaining budget) → `POST /api/threads` → open it.

**Thread view**
- Header: editable title (inline rename → `PATCH`), agent name and model, the context indicator (9.5), and a menu: Trace, Archive/Unarchive.
- Messages load with `includeTools=true&limit=100`; "Load earlier messages" uses `beforeSeq` and MUST preserve the scroll position when older rows are prepended.
- Group rows by turn (the correlation id): the user message, an optional collapsed tool-activity block (9.3), and the final assistant message. The in-flight turn shows a streaming bubble fed by the stream manager's buffer, with a caret; before the first delta show "Thinking…" with elapsed time.
- **User message states:** `pending` → "Queued" (shown when another turn is in flight or the agent is away); `claimed` → in progress; `failed` → an error notice using the human-readable reason (9.11) and a **Resend** button that posts the same content as a new message. User text renders as plain text with line breaks preserved. Assistant text renders as markdown (9.6).
- **Composer:** a multiline auto-growing textarea. On pointer-fine devices `Enter` sends and `Shift+Enter` inserts a newline; on touch devices `Enter` inserts a newline and a visible Send button sends; `Ctrl/Cmd+Enter` always sends. Disabled when empty or over `Messages:MaxContentChars` (32,000, with a counter shown near the limit) and while a post is in flight (prevents double submit). Sending while a turn is in flight is allowed (the message queues) with a visible hint. Drafts are kept in memory per thread for the lifetime of the page; they are not written to browser storage.
- **After send:** show the user bubble immediately from the `202` response and de-duplicate it against the later `message.created` event by message id.
- **Banners (from `GET /api/threads/{id}`):** agent disabled ("Messages will queue until it is enabled"); budget used up ("Daily budget used up. Resets at <local time>"); agent not seen for more than 3 minutes while a message is queued ("The agent hasn't connected recently").
- **Scrolling:** stay pinned to the bottom while the user is at the bottom; if they have scrolled up, do not move the view and show a "Jump to latest" pill indicating new content.
- **Performance:** batch streaming updates with `requestAnimationFrame` and re-render the streaming markdown at no more than about 10 Hz; finalise on completion.
- **Accessibility:** a visually hidden polite live region announces "Assistant is replying" and "Reply complete". Do NOT announce every delta.
- There is **no stop/cancel button** in 3.3 (the gateway does not support cancelling a turn). Do not fake one.

### 9.3 Tool activity

- From persisted rows (`tool_request` + `tool_result`, paired by tool-call id) show a collapsed summary line per turn ("Used 3 tools"), expandable to cards. A card shows: tool name, a status icon (ok / failed / denied), a one-line argument summary computed client-side (compact JSON with long strings shortened), and badges: `untrusted` ("not replayed to the model on later turns") and `truncated`.
- Expanded: the stored arguments (pretty-printed JSON) and the stored result (pretty-printed if JSON, otherwise text), in a scrollable monospace box. **Render tool arguments and results as plain text only**, never as HTML or markdown.
- If the trace data contains the provider tool-call id (T0 confirms whether the audit table stores it), enrich cards with risk, decision, approval link and duration. If it does not, show only what the message rows carry; do not guess by matching on names or times.
- During an in-flight turn, `tool.requested` events add live rows (name and "running…"). They are replaced by the persisted rows when `message.completed` arrives.
- Collapsed by default; a setting "Expand tool details by default" stored in `localStorage` (a non-sensitive preference, wrapped in try/catch).

### 9.4 Approvals

- **Inline card** in the thread at the position of the in-flight turn: tool name, a risk badge, the summary, the reason if present, a countdown to `expiresAt` (using the server clock offset), and Approve / Deny buttons. For the **highest risk level** require a second confirmation step (T0 identifies the level names from 3.2).
- Decide with `POST /api/approvals/{id}`. A `409` (already decided or expired) shows a notice and refreshes the card. Cards update when `approval.decided` arrives from any client; expired ones are shown muted as "Expired — the agent was told".
- **Approvals page:** tabs "Pending" (default) and "Recent" (last 50, any status). Each row: agent, tool, risk, summary, age or time left, a link to the thread, and the decision buttons for pending items. An empty state explains that approvals appear here when an agent needs permission.
- The nav badge and tab title count come from `GET /api/approvals?status=pending&limit=200`, kept fresh by events.
- No sounds or browser notifications (they need a secure context).

### 9.5 Context indicator

In the thread header show `~5.1k / 32k tokens` with a thin meter from `thread.context` (the last value computed by the gateway; label it as approximate). When `droppedTurns > 0`, show a banner at the top of the message list: "The model no longer sees the oldest N turns of this conversation."

### 9.6 Rendering safety (markdown and everything else)

- Assistant messages: `react-markdown` + `remark-gfm`; raw HTML disabled (`skipHtml`), no `rehype-raw`; links restricted by a URL transform to `http:`, `https:`, `mailto:` and in-page anchors; external links open with `target="_blank"` and `rel="noopener noreferrer nofollow"`, with the full URL in the `title`.
- **Remote images MUST NOT be loaded.** Replace a markdown image with a text link showing the alt text and the URL (for example `[image: alt text](https://…)`). This blocks data exfiltration through image URLs inserted by prompt-injected content. The CSP (`img-src 'self' data:`) is the backstop; add a test for both.
- Code blocks: monospace, horizontal scroll, a copy button using the clipboard fallback. No syntax highlighting in 3.3.
- GFM tables sit in a horizontally scrollable container.
- A single message longer than 200,000 characters renders as plain text with a "Render formatting" toggle, to avoid freezing the page.
- User text, tool arguments and tool results are always plain text.

### 9.7 Agents

- **List:** name, model, an enabled toggle (`PATCH`), today's spend against the daily budget as a bar, "resets at 00:00 UTC (HH:MM local)", context window, and last seen (relative; shown as a warning when over 3 minutes, "never" when null).
- **Create agent dialog:** name (client-side validation matching the server regex), model (free text; hint that it is an OpenRouter model id), daily budget in USD, optional context window. On success open the **token reveal dialog**: the token value shown once in a selectable box with a copy button (using the fallback), a clear "this will not be shown again" warning, and a required "I've saved it" confirmation to close.
- **Detail page:** edit model, budget and context window; enable or disable; tokens table (prefix, created, expires, last used, revoked) with **Issue token** (optional expiry; same reveal dialog) and **Revoke** (confirmation dialog); the last 20 LLM calls for the agent linking to the Audit page.
- The token value MUST NOT remain in component state, the query cache or the router state after the reveal dialog closes (use mutations that do not cache results and clear on close).
- Show a note: "Persona and tool policy are managed in the agent's config files." There is no delete.

### 9.8 Audit and trace

- **Audit page:** two tabs, LLM calls and Tool calls. Filters: agent, status, time window (1 hour, 24 hours, 7 days), thread (from links). Newest first, "Load more" by cursor. LLM columns: time (local), agent, model, status badge, tokens in/out, estimated prompt tokens, cost, latency, time to first token, error code, thread link. Tool columns: time, agent, tool, risk, decision, outcome, duration, approval, and an amber "unreported" marker when `reportedAt` is null and the call is older than 5 minutes. Show the summed cost of the loaded rows. On mobile each row becomes a card.
- **Trace panel** (side panel on desktop, full-screen sheet on mobile): one entry per turn with status, duration (claimed to completed), and cost and token totals; expandable to its LLM calls (model, tokens, cost, latency, TTFT), tool calls (risk, decision, outcome) and approvals. Clicking a turn scrolls the chat to that user message. It loads on open and refetches on `message.completed` while open.
- Cost formatting: `$0.0042`; below `$0.0001` show `<$0.0001`.

### 9.9 Account

- Shows the signed-in user and the session expiry.
- Change password (current, new, confirm; client-side minimum of 12 characters; server policy errors shown). Sign out (calls logout, clears caches, closes the stream, goes to `/login`).
- System info: version, provider, server time. A short, honest note: "You're connected over plain HTTP on your local network."

### 9.10 Layout, accessibility and polish

- Mobile first. Fully usable at 360 px width; touch targets at least 44 px; the composer is sticky at the bottom and accounts for the on-screen keyboard (use `dvh` units and `visualViewport`, not `100vh`).
- Light and dark via `prefers-color-scheme`, tokenised colours, AA contrast.
- Everything reachable and operable by keyboard with visible focus; a skip link; dialogs and menus from Radix; `Esc` closes panels.
- Respect `prefers-reduced-motion`.
- Relative times with absolute times in tooltips (`Intl.DateTimeFormat`, the browser's locale, defaulting to `en-GB`). Money via `Intl.NumberFormat` in USD.
- Loading skeletons, empty states and error states with a retry action for every list.
- No information may depend on hover alone.
- System font stack; no web fonts.

### 9.11 Error and failure messages

Map API error codes and message `failReason` codes to plain text in one table in `src/api/errors.ts` (unit-tested):

| Code | Message |
|---|---|
| `unauthorized` | (redirect to login) |
| `csrf_failed` | (re-check session, retry once) |
| `rate_limited` | "Too many attempts. Try again in N minutes." |
| `validation_failed` | the server message, next to the field when possible |
| `conflict` | context-specific ("Already exists." / "Already decided.") |
| `not_found` | "Not found. It may have been removed." |
| `agent_disabled` | "This agent is disabled." |
| `budget_exceeded` | "Daily budget used up. Resets at <local time>." |
| `context_too_large` | "That is too large for the model's context window. Shorten it or start a new chat." |
| `provider_error` | "The model provider returned an error. Try again." |
| `provider_timeout` | "The model provider timed out. Try again." |
| `agent_error` | "The agent hit an internal error." |
| `no_approver` | "An approval was needed but nobody was available." |
| `approval_expired` | "An approval request expired before it was answered." |
| `denied_by_user` | "You denied a tool request." |
| `internal_error` / unknown | "Something went wrong. Try again." (unknown fail reasons show "The agent could not complete this turn (<code>).") |

## 10. Build, Docker and developer workflow

- **`ui/` scripts:** `dev`, `build`, `lint`, `typecheck`, `test`, `test:e2e`, `gen:api`, `check:api`, `format`.
- **TypeScript:** `strict` plus `noUncheckedIndexedAccess`. ESLint with `typescript-eslint`, `react-hooks` and `jsx-a11y`; Prettier. Add lint rules (`no-restricted-properties`) that forbid `navigator.clipboard`, `crypto.randomUUID`, `localStorage` for anything but the allowed preference module, and `dangerouslySetInnerHTML`.
- **Bundle guard (guideline):** the build script warns when the initial JavaScript exceeds 250 KB gzipped.
- **`docker/Dockerfile.gateway`:** add a `node:<lts>` build stage (`npm ci`, `npm run build`) and copy `ui/dist` into the final image as `wwwroot`. Add `.dockerignore` entries for `node_modules`, `dist` and test output. The image remains non-root.
- **Compose:** publish the client port as `${CLIENT_BIND:-127.0.0.1}:8080:8080`; add `CLIENT_BIND` to `.env.example` with a comment about LAN use. Port 8081 stays unpublished.
- **Local development:** `docker/compose.dev.yml` (override file) runs the gateway with `Provider__Type=Fake` and `Auth__AllowedOrigins__0=http://localhost:5173`; `cd ui && npm run dev` starts Vite on 5173, proxying `/api`, `/openapi` and `/healthz` to `http://localhost:8080`. The dev origin allowance MUST NOT appear in the base Compose file. Document the steps in `docs/ui.md`.
- **`.gitignore`:** `ui/node_modules`, `ui/dist`, Playwright reports and test results.
- **CI:** if workflows exist (T0 reports), add a UI job: install, lint, typecheck, unit tests, `check:api`, build; and make the gateway job run the OpenAPI and fixture snapshot tests.

## 11. Tests

**Gateway (xUnit, real Kestrel on ephemeral ports, as before)**
- Threads: list ordering and cursor paging, grouped counts (`pendingApprovals`, `inFlight`, `queued`), archived filter, auto-title, preview, `PATCH` validation, `thread.created`/`thread.updated` events, CSRF on `PATCH`.
- Messages: latest page, `beforeSeq`, `afterSeq`, `hasMoreBefore`, `includeTools`, existing `after=<id>` still working.
- Trace: shape and ordering; totals; **contains no prompt, reply, argument or result content**; capped at 100 turns.
- Tool-call audit: filters and cursor paging; LLM audit cursor paging.
- System info: version, provider, `budgetResetAtUtc` with the fake clock. Agent `lastSeenAt` throttling.
- Global stream: carries `delta` and `snapshot` for every thread, `threadId` on all payloads, new thread events, replay by `Last-Event-ID`, and no `Content-Encoding` on the stream.
- Static hosting: the index is served on the client port only; the agent port returns 404 for UI paths; SPA fallback; a missing asset returns 404; cache headers; the exact CSP on HTML; the other security headers on HTML and API; `no-store` on API; **no** `Strict-Transport-Security` and no `upgrade-insecure-requests`; the built `index.html` contains no inline script.
- OpenAPI: authentication required; snapshot comparison; event fixtures comparison.
- Every new endpoint requires authentication (admin key or session) and rejects the wrong listener.

**UI unit and integration (Vitest, React Testing Library, MSW, a fake `EventSource`)**
- Auth gate: redirect with `next`; `next` validation rejects `//evil.example`, absolute URLs and `javascript:`; the 401 and `csrf_failed` flows; CSRF token only in memory.
- Stream manager: automatic reconnect, `lastEventId` query on manual reopen, closed → session check → login or backoff, visibility reopen, `reset` handling, deduplication, state transitions.
- Event reducers for every event type, using the committed fixtures.
- Chat: send with optimistic bubble and dedupe against `message.created`; queued and failed states; Resend; scroll pinning and the jump pill; "load earlier" keeps scroll position; streaming buffer batching; banners; draft kept per thread in memory only.
- Tool cards: plain-text rendering, truncation and untrusted badges, enrichment only when linkage data exists.
- Approvals: approve, deny, second confirmation for the highest risk, 409 handling, countdown (fake timers, server clock offset), badge and tab title.
- **Markdown safety:** `<script>` and raw HTML shown as text or dropped; `javascript:` and `data:` links neutralised; remote images replaced by text links; external link attributes; very large messages fall back to plain text.
- Agents: validation; token reveal appears once and is gone from state and cache after closing; revoke confirmation.
- Audit tables and the trace panel; account page; the error-mapping table (one test per code).
- Accessibility: `jest-axe` on each main view with no violations. A test that runs with `navigator.clipboard` and `crypto.randomUUID` undefined.

**End-to-end (Playwright: Chromium, WebKit, and a mobile-viewport project)**
Runs against the Compose stack with the fake provider and one agent. Global setup creates a user with the CLI. If the fake provider cannot yet script tool calls and approvals, extend it (small change; document it).
- Login success and failure, logout, session expiry (revoke the session via API, then act → redirected to login).
- Chat streams text progressively; a reload in the middle of a reply resumes it (snapshot).
- Tool use with approval: the pending card appears in the thread and in the Approvals page; approve → the turn continues; deny; expiry (short timeout in the test stack).
- Context banner (tiny `contextWindowTokens`), budget-exhausted banner, agent-disabled banner.
- Create an agent, see the token once, revoke a token.
- The audit page and the trace panel show the turn.
- Two tabs: an approval decided in one clears in the other.
- Offline and online toggling reconnects without losing or duplicating text.
- **Every network request is same-origin**, and the console shows no CSP violations.
- Core flows pass in the mobile viewport.

## 12. Configuration and documentation summary

**New configuration**

| Key | Where | Default |
|---|---|---|
| `CLIENT_BIND` | `.env` / Compose | `127.0.0.1` (set to the host's LAN address or `0.0.0.0` for LAN access) |
| `Auth__AllowedOrigins__0` | dev override only | `http://localhost:5173` (never in the base Compose file) |

The UI has no runtime configuration: it is same-origin and discovers everything from the API.

**Documentation to deliver**
- `docs/ui.md`: developer guide (setup, dev workflow, scripts, event table with payloads, how the stream manager works, the CSP and why, the clipboard and secure-context constraints).
- README: a short "Using the web UI" section (LAN binding, first user, never port-forward 8080, the plain-HTTP risk). Keep it concise.
- `docs/tech-debt.md`: add the items in section 15; remove anything paid off.
- The roadmap/plan: mark 3.3 as built, summarise it, and link this spec.
- Supersession notes in earlier specs are not needed unless T0 finds a conflict.

## 13. Task order

| # | Task | Done when |
|---|---|---|
| T0 | **Orient (no code changes).** Read the repo and the earlier specs. Confirm the foundations are merged. Write `docs/3.3-orientation.md`: real endpoint, DTO and event payload names; the risk level names; whether `tool_calls` stores the provider tool-call id; whether the fake provider can script tool calls and approvals; whether CI workflows exist; any conflict with this spec. Stop and ask the human on conflicts. | Note written and reviewed |
| T1 | Gateway API additions: threads (5.1), message pagination (5.2), trace (5.3), tool-call audit and cursor paging (5.4), system info (5.5), agent last seen (5.6), extended global stream and new thread events (5.7). Event fixtures. | Gateway tests for these pass |
| T2 | OpenAPI document and snapshot test (7); static hosting, SPA fallback, caching and security headers (6) with a placeholder index; `CLIENT_BIND` in Compose | Hosting and OpenAPI tests pass |
| T3 | UI scaffold: Vite, strict TypeScript, Tailwind, Radix, Router, TanStack Query, generated API client, lint rules, test setup; shell, login/logout, auth gate, CSRF handling, system footer; Dockerfile build stage; dev override file; `docs/ui.md` start | The stack serves the real UI; login works against Compose; `check:api` passes |
| T4 | `EventStreamManager`, event types from fixtures, reducers, connection indicator | Stream manager and reducer tests pass |
| T5 | Chat core: thread list, create, thread view, composer, streaming bubble, optimistic send, pagination, queued/failed states, Resend, banners, markdown rendering with the safety rules | Chat and markdown-safety tests pass |
| T6 | Tool activity cards, inline approvals, the Approvals page and badges, the context indicator | Tool and approval tests pass |
| T7 | Agents list and detail, create dialog, token reveal and revoke | Agents tests pass |
| T8 | Audit page and trace panel | Audit and trace tests pass |
| T9 | Account page, mobile and accessibility pass, empty/loading/error states, polish | `jest-axe` clean; manual pass at 360 px |
| T10 | Playwright suite, docs (section 12), tech-debt and plan updates, acceptance walk-through (section 16) with the fake provider, then a real OpenRouter session from a phone on the LAN | Checklist complete; results noted in `docs/verification-log.md` |
| T11 | *(Optional)* Usage endpoint (5.8) and a simple Usage page: a daily cost and token table with plain bars, per agent | Tests pass |

## 14. Out of scope

Stop or cancel a turn; editing, regenerating or deleting messages; thread deletion; search; attachments and file upload (since added by the [attachments spec](sharpclaw-attachments-spec.md)); a workspace file browser; editing `soul.md`, agent config files or tool policy in the UI; user management in the UI (CLI only); multi-user data separation; passkeys, OIDC, two-factor; password reset flows; TLS and HTTP/2; PWA, service workers and offline mode; push or browser notifications; syntax highlighting; internationalisation; a manual theme toggle; frontend telemetry or error reporting; charts beyond the optional Usage page; Grafana changes.

## 15. Known gaps accepted in this phase

- **Plain HTTP on the LAN:** the password and session cookie are visible to anyone who can sniff the network. Future fix: a local TLS reverse proxy, which also enables HTTP/2 and secure-context features (clipboard API, notifications, PWA).
- **HTTP/1.1 connection limit:** one SSE stream per tab, so about four tabs per browser before the 6-connection limit bites. A cross-tab shared stream is a possible later improvement.
- No stop button; no thread deletion; drafts are lost on reload.
- Half-open streams are only healed when the tab is hidden and shown again or the browser reports online/offline changes; there is no server-side keep-alive detection in the UI.
- The context meter shows the value from the gateway's last context computation, not a live figure.
- Tool-card enrichment (risk, decision, duration) depends on the audit table storing the provider tool-call id.
- Thread previews duplicate a short slice of message content in the `threads` table.
- Very long threads rely on pagination (no list virtualisation).
- No frontend error reporting: UI crashes are visible only in the browser console.

## 16. Acceptance checklist

- [ ] From a phone on the home LAN, open the gateway's LAN address, sign in, start a chat with an agent and watch the reply stream.
- [ ] Reload the page in the middle of a streaming reply: the text so far reappears and the rest continues without duplication.
- [ ] A turn that uses tools shows a collapsed tool summary that expands to arguments and results (plain text, truncation and untrusted badges where applicable).
- [ ] A tool needing approval shows a pending card in the chat and in the Approvals page; approving lets the turn continue; denying and expiry are reported correctly; a decision in one tab clears the card in another; the nav badge and tab title count stay correct.
- [ ] Closing the tab while an approval is pending and reopening later still shows it (until it expires).
- [ ] A thread pushed past its context window shows the dropped-turns banner and the context meter.
- [ ] Failed turns show a plain-language reason and a Resend button; budget, disabled-agent and agent-away banners appear when relevant.
- [ ] Agents: create one, see its token exactly once, issue and revoke tokens, edit budget, model and context window, enable and disable.
- [ ] The Audit page and the thread trace show LLM calls, tool calls, approvals, costs and latencies; unreported tool calls are highlighted.
- [ ] Session expiry or sign-out from another place returns the user to login without errors or data left on screen; `next` redirects are safe.
- [ ] Markdown safety verified: injected HTML and scripts are inert, dangerous links are neutralised, remote images are never fetched.
- [ ] Every request the UI makes is same-origin, no CSP violations occur, no HSTS or `upgrade-insecure-requests` is sent.
- [ ] The UI works with `navigator.clipboard` and `crypto.randomUUID` unavailable.
- [ ] OpenAPI and event-fixture snapshots are committed, and `check:api` passes in CI.
- [ ] All gateway, UI and end-to-end tests pass; `scripts/verify-isolation.sh` still passes; `docs/` and the README are updated; port 8081 is still unpublished.
- [ ] No out-of-scope features were added; no prompt, reply, tool data, password, cookie, CSRF token or key appears in any log or metric.

## 17. Inputs needed from the human

- Confirm or override the section 2 defaults (in particular: Tailwind with Radix, npm, serving the UI from the gateway).
- The host's LAN address or hostname for `CLIENT_BIND`, and the devices and browsers you will actually use (for example iPhone Safari, Android Chrome) so the end-to-end and manual checks cover them.
- The first username (created with `sharpclaw users create`).
- An app name and favicon if you want something other than "SharpClaw" and a simple generated SVG.
- Model ids and budgets for any agents you want pre-created for the acceptance run.