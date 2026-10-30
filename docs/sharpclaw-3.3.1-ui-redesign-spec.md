# SharpClaw V3 — Phase 3.3.1 Implementation Spec: Tabbed UI Redesign

**Audience:** an implementing coding agent (and the human reviewing its work).
**Status:** draft. Ready to implement once the human confirms the defaults in section 2.
**Prerequisite:** Phase 3.3 is merged: the client API, OpenAPI snapshot and generated client, the
React UI served by the gateway, `EventStreamManager`, and the Playwright suite. T0 checks this.
**Mockups:** [`sample-overview-tab.png`](sample-overview-tab.png) and
[`sample-prompts-tab.png`](sample-prompts-tab.png). They are prototypes. Where they show something
this spec leaves out (agent→agent messages, skills, sub-agent "message" rows), this spec wins.

## 0. How to use this document

- **T0 is mandatory and first.** Map this spec onto the code as it exists today and report
  conflicts before changing anything. Names here are illustrative where they refer to existing code.
- Implement in the task order in section 12. Each task gets its own branch and pull request with
  passing tests before the next one starts.
- "MUST" / "MUST NOT" are hard requirements. Build only what is described here. If something is
  ambiguous, stop and ask the human.
- Standing rules still apply. The 3.3 spec and `docs/ui.md` stay in force unless this document says
  otherwise: CSP, rendering safety, no secure context, one event stream per tab, the error shape,
  and no logging of content, tokens or keys.

## 1. Goal and scope

Replace the 3.3 sidebar shell with a **tabbed, dashboard-style interface**. The landing view
shows at a glance what every agent is doing and what it costs. Chat becomes a dense
"Prompts" workspace, and each agent gets its own tab.

> Open SharpClaw → **Overview**: token and cost charts for today, 7 days and 30 days, cost by
> model, remaining OpenRouter credits, and a card per agent with its spend and latest messages, all
> updating live → **Prompts**: pick an agent and model, pick or start a session, chat, and watch
> tool activity and per-turn cost inline, with a status bar for context, tokens and cost →
> **agent tab** (e.g. *Ade*): everything about one agent, including settings and tokens.

### In scope

- Gateway additions (section 3):
  - a usage aggregation endpoint
  - a recent-activity endpoint and recording who sent each message
  - a per-thread model override with a configured allow-list
  - a cached OpenRouter credits balance
- A new UI shell: header, tab bar and header menu (section 4).
- The tabs:
  - **Overview**, **Prompts** and one tab per agent (sections 5–7).
  - **Approvals**, **Audit** and **Agents**: the existing pages, re-homed (section 8).
  - **Account** moves to the header menu.
- A dark visual design matching the mockups, plus a light theme from the same tokens (section 9).
- Phone layout (section 10). Tests, docs and task order (sections 11–12).

### Out of scope (do NOT build)

Section 13. Notably:

- agent-to-agent messaging (Phase 3.5)
- skills, and "message"/sub-agent rows in tool activity
- the Controls phase (3.4) features
- a stop button
- user-configurable dashboards or tab ordering
- a chart library

## 2. Decisions and defaults

| Decision | Value |
|---|---|
| Phase name / file | 3.3.1, `docs/sharpclaw-3.3.1-ui-redesign-spec.md`. 3.4 stays "Controls". |
| Relationship to 3.3 UI | **Replace the shell**, keep the stack (React, Vite, Tailwind, Radix, TanStack Query, React Router, openapi-fetch) and reuse the feature components (chat turns, tool activity, approvals, agent settings/tokens, audit tables, trace panel). The 3.3 routes `/chats*` redirect to their `/prompts*` equivalents. |
| Tabs | `Overview` · `Prompts` · *one per agent* · `More ▾` · then, right-aligned, `Approvals (n)` · `Audit` · `Agents`. Account, sign-out and system info sit in a header menu, not a tab. |
| Agent tabs | Enabled agents, alphabetical, at most **6** inline. The rest, and all disabled agents, go into `More ▾`. |
| Model override | Per thread and changeable at any time. It applies from the thread's next LLM call. The choices come from a gateway allow-list `Models:Allowed`, exposed by `GET /api/system`. |
| Usage | New `GET /api/usage`, aggregated by the gateway from `llm_calls`. Buckets are in UTC (the budget day is UTC too). The UI labels them in local time. |
| Activity feed | New `GET /api/activity` (recent user/assistant messages across threads, with a 120-character preview). It stays live by **invalidating the query on events**, as 3.3 does; event payloads are not enriched. |
| Credits | The gateway fetches OpenRouter `GET /credits` with a separate **management key**. It fetches at startup, then every 24 h (hourly while failing), and caches the result **in memory**. `GET /api/credits` exposes it. |
| Charts | Hand-rolled SVG (stacked bars, horizontal bars). No chart library. Each chart has a text alternative. |
| Theme | Dark, matching the mockups, regardless of the system setting. A light theme from the same CSS tokens, and (added later) an LCARS theme, are opt-in from the user menu's Theme group and remembered in `localStorage` (`lib/prefs.ts`); see [ui.md](ui.md). |
| Phone | Supported. The tab bar becomes a horizontally scrolling strip, and the Prompts session list becomes a drawer. |
| Unchanged | SQLite, EF Core, UTC `DateTime`, `TimeProvider`, the error shape, port-based listener separation, the CSP, one SSE stream per tab, shared visibility for every user. |

## 3. Gateway additions

All new endpoints are on the **client listener only** (`/api/*`, session cookie + CSRF or admin key).
Every response uses `SharpClawJson.Options`, and every error uses the `ApiResults.*` shape. Each task
updates the OpenAPI snapshot, regenerates `schema.d.ts` and adds gateway tests (unit tests, plus
`GatewayFixture` integration tests for HTTP and the database).

### 3.1 Usage — `GET /api/usage?range=today|7d|30d&agent=<name>`

Aggregates `llm_calls` (every status; failed calls can still carry cost). `agent` is optional; an
unknown name returns `404`. Any other `range` value returns `400 validation_failed`.

| `range` | Window (UTC) | Bucket | Buckets returned |
|---|---|---|---|
| `today` | budget-day start (`AgentQueries.DayStartUtc`) → now | 1 h | 00:00 up to and including the current hour |
| `7d` | `floor(now, 6 h) − 27 × 6 h` → now | 6 h, aligned to 00/06/12/18 | 28 |
| `30d` | `today − 29 days` → now | 1 day | 30 |

```json
{
  "range": "7d",
  "from": "2026-10-21T18:00:00Z",
  "to": "2026-10-28T18:04:03Z",
  "bucketSeconds": 21600,
  "buckets": [ { "start": "…Z", "calls": 0, "promptTokens": 0, "completionTokens": 0, "costUsd": 0.0 } ],
  "byModel": [ { "model": "gpt-a-mini", "calls": 3, "promptTokens": 3800, "completionTokens": 292, "costUsd": 0.01 } ],
  "byAgent": [ { "agentId": "…", "agentName": "ade", "calls": 3, "promptTokens": 3800, "completionTokens": 292, "costUsd": 0.01 } ],
  "totals": { "calls": 3, "promptTokens": 3800, "completionTokens": 292, "costUsd": 0.01, "unknownUsageCalls": 0 }
}
```

- Buckets MUST be zero-filled and in ascending order.
- `byModel` is sorted by cost, highest first. `byAgent` includes every agent, even with zero usage,
  and is sorted by name. When `agent` is given, everything is scoped to that agent.
- Calls with `usage_known = false` add 0 tokens and their recorded cost, and are counted in
  `unknownUsageCalls`.
- Time comes from the injected `TimeProvider`.
- Implementation: select the needed columns for the window and aggregate in C#. Add an index on
  `llm_calls(created_at)` in a migration (only `(agent_id, created_at)` exists today). In-memory
  aggregation is fine at household volume; record it in `docs/tech-debt.md`.

### 3.2 Activity — `GET /api/activity?agent=<name>&limit=50&before=<cursor>`

Returns recent conversational messages (`kind = message`, roles `user` and `assistant`), newest
first, across all threads. Tool request and tool result rows are never included.

```json
{
  "items": [
    {
      "messageId": "…", "threadId": "…", "threadTitle": "Harness spec",
      "agentId": "…", "agentName": "cody",
      "role": "user", "from": "ken", "to": "cody",
      "status": "completed", "createdAt": "…Z",
      "preview": "ping from ken"
    }
  ],
  "nextCursor": null
}
```

- `limit` runs from 1 to 100 (default 50). The cursor is opaque, the same scheme as the thread list.
- `preview`: the first 120 characters of the content with whitespace collapsed. It is plain text,
  and the UI MUST render it as text, never markdown.
- **Sender.** Add a nullable `messages.sent_by_user` column (migration), set when a user message is
  posted. Its value is the signed-in user's name, or `admin_key`, the same convention as
  `approvals.decided_by_user`.
  - For `role = user`: `from` is `sent_by_user` (`"user"` when null, e.g. older rows) and `to` is
    the agent name.
  - For `role = assistant`: `from` is the agent name and `to` is the sender of the turn's user
    message (joined by correlation id), falling back to `"user"`.
- Also add `sentByUser` to `MessageDto`, so the Prompts tab can label user turns with a name
  (`KEN · 10:40:00`).
- The UI invalidates `activity` queries on `message.created`, `message.completed` and `error`
  (section 5.3). No event payload changes.

### 3.3 Per-thread model override

- **Config:** `Models:Allowed` is a string array (default empty) of model ids a thread may switch
  to. It is validated at startup: non-empty, unique strings, each at most 200 characters.
  Document it in the README configuration section.
- **System info:** `GET /api/system` gains `models: string[]`, the allow-list.
- **Schema:** add a nullable `threads.model` column (migration). Null means "use the agent's model".
  Add `model` to `ThreadDto` and `ThreadSummaryDto`.
- **Create and update:**
  - `CreateThreadRequest` gains an optional `model`.
  - In `UpdateThreadRequest`, `model` works as follows:
    - absent or null: no change
    - `""`: clear the override
    - anything else: MUST be in `Models:Allowed`, otherwise `400 validation_failed`
  - Setting it to the agent's own model is allowed and stored as given.
- **Resolution:** in `/v1/chat/completions` the gateway already overwrites the model with
  `agent.Model`. Change that to:
  - use `thread.Model` when it is non-null **and still in the current allow-list**
  - otherwise use `agent.Model`, and log a warning (ids only) when an override was ignored
  - `llm_calls.model` records the model actually used (it already does)
- **Events:** `thread.updated` gains `model` (string or null). Update its fixture, `events.ts` and
  `tests/events.test.ts`.
- Budgets stay per agent. The context window stays the agent's setting; the override does not
  change it (section 14).

### 3.4 Provider credits — `GET /api/credits`

- **Config:**
  - `OpenRouter:ManagementKey` (env `OpenRouter__ManagementKey`, in `.env`, never committed)
  - `Credits:RefreshHours` (default 24, range 1–168)
  - `Credits:LowThresholdUsd` (default 5)
- **`CreditsRefresher`** is a `BackgroundService`:
  - It calls `GET {OpenRouter:BaseUrl}/credits` with the management key at startup, then every
    `RefreshHours`. After a failure it retries every hour until it succeeds.
  - It uses a 10 s timeout and no other retries.
  - It keeps the last result **in memory** (`totalCredits`, `totalUsage`, `asOf`) plus the last
    attempt time and outcome.
  - It MUST NOT log the key or the response body. Log only the HTTP status or exception type.
  - It does not run when the provider is `Fake` or the key is empty.
- **Response:**

```json
{
  "status": "ok",
  "remainingUsd": 3.36,
  "totalCreditsUsd": 20.0,
  "totalUsageUsd": 16.64,
  "asOf": "2026-10-28T06:00:00Z",
  "lastAttemptAt": "2026-10-28T06:00:00Z",
  "lowThresholdUsd": 5.0
}
```

- **`status`:**

  | Value | Meaning |
  |---|---|
  | `ok` | The last attempt succeeded and is no more than `RefreshHours` + 1 h old. |
  | `stale` | The last success is older than that. |
  | `failing` | The last attempt failed. `asOf` and the amounts are those of the last success, or null if it never succeeded. |
  | `not_configured` | No management key is set. |
  | `unsupported` | The provider is `Fake`. |

  Amounts are null when there has never been a success.
- Tests use a fake `HttpMessageHandler` and `FakeTimeProvider`: success, failure and hourly retry,
  stale, not configured, Fake provider, and no key or body in the logs.

## 4. Shell, tabs and routing

### 4.1 Header

One row, as in the mockups:

- **Left:** `SharpClaw 3` (accent colour).
- **Middle:**
  - a subtle subtitle with the connection state (`live` / `reconnecting…` / `offline`), from
    `EventStreamManager`
  - the gateway version
  - a prominent **Fake provider** badge when the provider is `Fake`
- **Right:** the user menu (user name ▾). It holds Account (`/account`), System info (version,
  provider, server time, budget reset time), and Sign out.

The browser title stays `SharpClaw`, prefixed `(N)` when N approvals are pending.

### 4.2 Tab bar

A Radix `Tabs`-styled navigation bar. Each tab is a **link**, so every view is a URL.

- The active tab is underlined in the accent colour and marked `aria-current="page"`.
- Order: `Overview`, `Prompts`, the agent tabs (agent name, capitalised, e.g. `Ade`), `More ▾`
  (only when needed), then a right-aligned group: `Approvals` with the pending-count badge, `Audit`
  and `Agents`.
- A disabled agent's tab, when shown through `More`, is greyed out with a "disabled" tooltip.
- When the current route is an agent in `More`, the `More` button shows `More: fin` as active.
- Agent tabs update live. The `agents` query is already invalidated by agent changes; a new or
  re-enabled agent appears without a reload.

### 4.3 Routes

| Route | View |
|---|---|
| `/login` | Unchanged |
| `/` | Redirects to `/overview` |
| `/overview` | Overview tab (section 5) |
| `/prompts`, `/prompts/:threadId` | Prompts tab (section 6). `?agent=<name>` preselects the agent; `?panel=trace` opens the trace panel. |
| `/agents/:name` | Agent tab (section 7) |
| `/agents` | Agents admin tab (section 8.3) |
| `/approvals`, `/audit` | As in 3.3, inside the new shell (sections 8.1, 8.2) |
| `/account` | As in 3.3, reached from the header menu; no tab is active |
| `/chats`, `/chats/:threadId` | `replace` redirect to `/prompts`, `/prompts/:threadId` (old bookmarks) |
| `*` | Not-found page |

The auth gate, `next` handling, 401 and CSRF behaviour are unchanged from 3.3. After login the
default destination is `/overview`.

## 5. Overview tab

The layout follows `sample-overview-tab.png`.

### 5.1 Live bar

A thin bar with `Live messages ●` on the left. The dot uses the connection-state colour: green when
live, amber when reconnecting, grey when offline. A `pause` / `resume` button sits on the right.

- **Paused:** activity and usage queries on this tab stop refetching on events. The bar shows
  `Paused · N new` (the count of relevant events seen since pausing).
- **Resume:** refetches.
- The pause lives in memory only and resets on reload.

### 5.2 Summary cards (top row)

Five cards in a responsive grid: five columns at ≥ 1280 px, wrapping below that.

1. **Today**, **Last 7 days** and **Last 30 days**. Data: `/api/usage?range=…`.
   - The title is on the left; the bucket caption is on the right (`tokens per hour` /
     `tokens per 6 hours` / `tokens per day`).
   - A stacked bar chart: input tokens in blue at the bottom, output tokens in amber at the top.
   - First and last bucket labels sit under the chart in local time (`26 Oct 00:00`,
     `26 Oct 13:00`; days as `27 Sep`).
   - A legend (`■ in ■ out`) and a footer: `Total cost today` / `Total cost, 7 days` /
     `Total cost, 30 days` with `$0.00` in accent.
   - Hovering or focusing a bar shows a tooltip: local bucket time, in and out tokens, calls, cost.
2. **Cost by model** (caption `last 7 days`). Data: `byModel` from the 7-day usage.
   - Horizontal bars scaled to the most expensive model, with the model id and the cost.
   - Shows the top 6; the rest are summed into "other".
   - Empty state: "No model calls in the last 7 days."
3. **Credits Remaining**. Data: `/api/credits`.
   - A large amount (2 dp). It is shown in the danger colour when below `lowThresholdUsd`.
   - Footer left: `as of <local date>`. Top-right caption and footer right, by status:

     | `status` | Top-right caption | Footer right |
     |---|---|---|
     | `ok` | none | none |
     | `stale` | `stale` | none |
     | `failing` | none | `daily fetch failing` (danger) |
     | `not_configured` | none | "Set `OpenRouter__ManagementKey` to show credits." in place of the amount |
     | `unsupported` | none | "Not available with the fake provider." in place of the amount |

Zero usage still renders axes and labels, with a `$0.00` footer. Values are formatted as follows:

- money: `$0.00`; amounts under $0.01 but above zero show `<$0.01`
- tokens: with thousands separators

Numbers use the monospace font.

### 5.3 Agent cards (second row)

One card per agent: enabled agents first, then disabled ones dimmed, alphabetical. Two columns on
desktop, one on mobile.

- **Header:**
  - the agent name (bold), linking to `/agents/:name`
  - right-aligned and muted: the agent's model
  - a small status dot from `lastSeenAt` (seen within 3 minutes = online)
  - an amber dot when a turn is in flight
- **Usage rows:** `Today` / `Last 7 days` / `Last 30 days`, each with `N tokens  $x.xx`
  (in + out), from the three usage responses' `byAgent`. No extra request.
- **MESSAGES:** a scrollable list (fixed height, about 8 rows visible) from
  `/api/activity?agent=<name>&limit=50`, newest at the top. Each row has four columns:
  - local `HH:mm:ss`
  - `from → to`
  - a kind label (`prompt` for user, `reply` for assistant, `failed` for a failed user message)
  - the preview, as plain text and truncated with an ellipsis

  Clicking a row opens `/prompts/:threadId`. Empty state: "No messages yet."
- **Live updates:** while not paused, debounce invalidations to 1 s:
  - `message.created`, `message.completed` and `error` invalidate `['activity']`
  - `message.completed` and `error` also invalidate `['usage']`

  Also refetch usage every 60 s and credits every 10 minutes while the tab is visible.

## 6. Prompts tab

The layout follows `sample-prompts-tab.png`. This is the 3.3 chat, re-laid out.

### 6.1 Toolbar

`Agent [select]` and `Model [select]`.

- **Agent:** enabled agents. The default comes from `?agent`, then the open thread's agent, then
  the single preference in `src/lib/prefs.ts` (last agent used), then the first agent.
  - Changing the agent filters the session list. If the open thread belongs to another agent, it
    navigates to `/prompts?agent=<name>`.
- **Model:** `<agent model> (default)` first, then each id in `models` from `/api/system` (minus the
  agent's own model).
  - With a thread open, it shows the thread's override (or default). Changing it sends
    `PATCH /api/threads/{id}` with `model` (`""` for default) and shows a toast:
    "Model changes apply from the next reply."
  - With no thread open, it sets the model for the next **New session**.
  - It is hidden when the allow-list is empty, and disabled while the PATCH is in flight.

### 6.2 Sessions sidebar

The 3.3 thread list, restyled. "Session" is UI wording for a thread; the API is unchanged.

- Header `SESSIONS` with a collapse button (`⇤`). The collapsed state is in memory.
- `+ New session` (accent button): `POST /api/threads` with the selected agent (and model, if
  chosen), then opens it.
- Rows: title, with the active row highlighted. They keep the 3.3 badges: in-flight spinner,
  pending approvals, queued count. Relative time and preview appear on a second muted line.
- Load-more, the archived toggle and the agent filter (now driven by the toolbar) keep their 3.3
  behaviour.

### 6.3 Thread view

The 3.3 thread view, with the same behaviour (paging, scroll pinning, banners, streaming, approvals,
failed and Resend, markdown safety) and restyled:

- The title appears small and muted at the top. Rename, Archive and Trace are in a `⋯` menu next to
  it.
- **User turn:** a label `KEN · 10:40:00` (`sentByUser` upper-cased, or `YOU` when null; local
  time), then the text in a raised block with an accent left border. Plain text.
- **Assistant turn:** a label `ADE · 10:40:00` (agent name), then the markdown reply in the
  assistant colour.
- **Tool activity:** compact monospace rows under the reply, connected by a thin vertical guide.
  Each row:
  - a status glyph (`┌` done, `⟳` running)
  - kind `tool`
  - the tool name
  - the approval or risk summary text (when there is one)
  - the status with its duration, right-aligned: `done 0.4s` (green), `running…` (amber),
    `denied` / `failed` (red), `awaiting approval` (amber, with the inline approval card under it
    as in 3.3)

  A second muted line per model round shows `812 in · 96 out · gpt-a · 1.8s · $0.0024`, from the
  thread trace's LLM call for that round, joined as in 3.3.
  - Tool arguments and results are still shown only when expanded, and only as plain text, as
    in 3.3.
  - There are no `skill` or `message` rows (section 13).
- **Composer:** pinned to the bottom, placeholder
  `Message the agent (Enter sends, Shift+Enter newline; markdown supported)`. Behaviour as in 3.3.

### 6.4 Status bar

A single monospace line pinned to the bottom of the tab:

`Session: <title>   Agent: <name>   Model: <effective model>   Context <est> / <window> (<pct>%)   Tokens <total> (in <in> · out <out>)   Cost $<cost>`

- Context comes from `thread.context`.
- Tokens and cost are the sums of `totals` over all turns in the thread's trace. The trace is
  invalidated on `message.completed` and `error`.
- The model is the override when one is set, otherwise the agent's model.
- Show `—` for unknown values, and no session fields when no thread is open.
- On phones it wraps to two lines.

## 7. Agent tab (`/agents/:name`)

Everything about one agent, built only from existing endpoints plus `/api/usage?agent=` and
`/api/activity?agent=`.

1. **Header strip:**
   - name, model, and an enabled/disabled badge
   - last seen (relative, with an online dot)
   - context window
   - today's spend against the budget, as the 3.3 `Meter` (`$spent / $budget`, reset time from
     `/api/system`)
   - Buttons: `Open in Prompts` (`/prompts?agent=<name>`) and `Enable`/`Disable`
     (`PATCH /api/agents/{name}`, confirmed when disabling).
2. **Usage:** the Today / 7 days / 30 days cards from 5.2, scoped with `agent=<name>`, plus Cost by
   model for this agent.
3. **Activity:** the 5.3 messages list, taller (about 20 rows), with "Load more" through the cursor.
4. **Sessions:** the latest 10 threads from `GET /api/threads?agentId=` (title, preview, time,
   badges). Each links to `/prompts/:threadId`. "View all" goes to `/prompts?agent=<name>`.
5. **Pending approvals:** the agent's pending approvals, as 3.3 `ApprovalCard`s. Filter
   client-side by agent unless T0 finds an agent filter on `/api/approvals`. Empty state:
   "Nothing waiting."
6. **Settings and tokens:** the 3.3 `AgentSettingsForm` and `AgentTokens` (with the one-time token
   reveal dialog), unchanged in behaviour.
7. **Recent LLM calls:** the 3.3 `AgentLlmCalls` table, with a link to `/audit?tab=llm&agent=<name>`.

An unknown name shows the not-found state inside the shell.

## 8. Re-homed tabs

### 8.1 Approvals (`/approvals`)

The 3.3 Approvals inbox, inside the new shell. The tab shows the pending count badge. No behaviour
change.

### 8.2 Audit (`/audit`)

The 3.3 Audit page (LLM calls / tool calls sub-tabs, filters, cursor paging, trace links), restyled.
Trace links go to `/prompts/:threadId?panel=trace`.

### 8.3 Agents (`/agents`)

Administration of all agents, including disabled ones:

- A table with columns: name (links to its agent tab), model, enabled toggle, today's spend /
  budget, last seen, created.
- `Create agent`: the 3.3 `CreateAgentDialog`, with the one-time token reveal. When it completes,
  the new agent's tab appears.

### 8.4 Account (`/account`, header menu)

The 3.3 Account page (password change, sign out, system info), inside the shell, with no tab
selected.

## 9. Visual design

- **Tokens:** define CSS custom properties in `src/styles/index.css` and map them into Tailwind.
  Components MUST use tokens, never hard-coded colours.

  | Token | Dark (from the mockups) | Purpose |
  |---|---|---|
  | `--bg` | near-black `#151517` | page |
  | `--surface` | `#1c1c1f` | cards, sidebar |
  | `--surface-raised` | `#26262a` | user message block, inputs |
  | `--border` | `#2e2e33` | card borders, dividers |
  | `--text` / `--text-muted` | `#e6e6e6` / `#8a8a90` | |
  | `--accent` | amber `#f5b82e` | active tab, primary button, totals |
  | `--chart-in` / `--chart-out` | blue `#7aa2f7` / amber `#f5b82e` | token bars |
  | `--assistant` | blue `#7aa2f7` | assistant text |
  | `--ok` / `--warn` / `--danger` | green / amber / red `#f26d6d` | status |

  Light- and LCARS-theme values (opt-in from the user menu) are chosen to meet WCAG AA contrast.
- **Type:** the system UI font for prose; the system monospace stack
  (`ui-monospace, SFMono-Regular, Menlo, monospace`) for numbers, tables, timestamps, the tool rows
  and the status bar. No web fonts (CSP).
- **Density:** compact. Cards use 8–12 px padding, 6 px corner radius and a 1 px border.
- **Charts:** SVG with `role="img"` and an `aria-label` summary (for example "Last 7 days: 4,092
  tokens, $0.01"), plus a visually hidden table of the buckets. Bars use the `fill` and `height`
  attributes, never inline `style` attributes (CSP).
- **Motion:** none beyond the existing spinner; respect `prefers-reduced-motion`.

## 10. Phone layout (≤ 640 px)

- The tab bar becomes a single horizontally scrollable strip, with the active tab scrolled into
  view. The right-aligned group joins the strip. `More ▾` remains.
- Overview: the cards stack in one column, and agent cards follow.
- Prompts: the toolbar wraps. The sessions sidebar becomes a drawer (button `Sessions` in the
  toolbar), and the thread fills the screen. The status bar wraps.
- Agent tab: sections stack.
- The `@core` Playwright mobile project covers Overview and Prompts.

## 11. Tests

**Gateway (xUnit, `GatewayFixture`)**

- **Usage:**
  - bucket alignment for each range, using `FakeTimeProvider` across a UTC midnight
  - zero-fill, the agent filter and an unknown agent (`404`), a bad range (`400`)
  - unknown-usage counting, and the `byModel` sort
- **Activity:**
  - ordering and the cursor, the agent filter
  - tool rows excluded
  - preview truncation and whitespace collapsing
  - `from`/`to` for user and assistant rows, including the null-sender fallback
  - `sentByUser` set from a session user and from the admin key
- **Model override:**
  - create and PATCH, with an allowed id, a disallowed id (`400`), and `""` to clear
  - chat completions use the override, and fall back to the agent model once the override leaves
    the allow-list
  - `llm_calls.model` records the model used
  - `thread.updated` carries `model`
  - `/api/system` lists the models
- **Credits:** the cases in 3.4.
- The listener-separation tests cover the new routes (`404` on the agent listener).
- The OpenAPI snapshot is updated.

**UI (Vitest + RTL + MSW + jest-axe)**

- **Shell:**
  - the tab order
  - the agent tabs and `More` overflow, including a disabled agent and `More: name` when active
  - the approvals badge
  - the `/chats` redirects
  - the header menu
  - the Fake badge
- **Overview:**
  - the charts render the right bucket count and text alternative
  - cost formatting (`<$0.01`)
  - every credits state
  - agent card usage rows and message rows, a row click navigating
  - pause stops refetching and counts new events; resume refetches
- **Prompts:**
  - the model select (default, allow-list, PATCH and toast, hidden when empty, model passed when
    creating)
  - the agent select filtering
  - the user label from `sentByUser`
  - the tool row status glyphs and LLM-call metric lines
  - the status bar sums
  - the sessions drawer on mobile
- **Agent tab:** each section renders from MSW data; an unknown agent shows not-found.
- jest-axe is clean on every tab.

**End to end (Playwright)**

- Update the existing specs for the new routes and selectors, without losing coverage.
- Add `overview.spec.ts`: send a message and see the agent card's MESSAGES row and the today total
  update without a reload; no CSP violations.
- Add a model-override step to `chat.spec.ts`: the fake provider's recorded `llm_calls.model`
  matches.

## 12. Task order

| # | Task | Done when |
|---|---|---|
| T0 | **Orient (no code changes).** Confirm 3.3 is merged. List the 3.3 components to reuse and those to retire. Check for an agent filter on `/api/approvals`. Confirm where `/v1/chat/completions` sets the model, the indexes on `llm_calls`, and whether `MessageDto` can carry `sentByUser`. Note conflicts and ask the human. | Notes posted to the human |
| T1 | Usage endpoint (3.1) and the `llm_calls(created_at)` index | Gateway tests pass; snapshot updated |
| T2 | `messages.sent_by_user`, `MessageDto.sentByUser`, and the activity endpoint (3.2) | Gateway tests pass; snapshot updated |
| T3 | Model override (3.3): config, system info, column, create/PATCH, resolution, `thread.updated.model` with its fixture | Gateway tests pass; snapshot updated |
| T4 | Credits refresher and endpoint (3.4); `.env.example` and README config | Gateway tests pass; snapshot updated |
| T5 | UI shell: tokens and theme, header and menu, tab bar with overflow, routes and redirects; the existing pages mounted in the new shell (8.1–8.4) | Shell tests pass; existing UI tests pass with updated selectors |
| T6 | Overview tab (5): chart components, cards, credits, agent cards, live bar and pause | Overview tests pass |
| T7 | Prompts tab (6): toolbar with model select, sessions sidebar, restyled thread view and tool rows, status bar | Prompts tests pass |
| T8 | Agent tab (7) | Agent-tab tests pass |
| T9 | Phone layout and accessibility pass (9, 10); remove the retired 3.3 shell code | jest-axe clean; manual pass at 360 px |
| T10 | Playwright updates and new specs; docs: `docs/ui.md` (shell, tabs, charts, credits), README config, `docs/tech-debt.md`, `docs/sharpclaw-v3-plan.md` (add 3.3.1); walk through section 15 with the fake provider, then a real OpenRouter session | Checklist complete; noted in `docs/verification-log.md` |

## 13. Out of scope

- Agent-to-agent messages, and agent → agent rows in activity (Phase 3.5).
- Skills, and `skill` or `message` rows in tool activity.
- Per-thread budgets, rate limits and a kill switch (Phase 3.4).
- A model override per message, or per agent from the Prompts tab. (The agent's default model is
  still edited in the agent tab's settings.)
- Fetching the model list from OpenRouter.
- Persisting credits history, or alerts on low credits.
- Usage beyond 30 days, custom date ranges, CSV export.
- Chart libraries, user-configurable dashboards, tab reordering.
- A stop/cancel button, thread deletion, search (unchanged from 3.3).

## 14. Known gaps accepted in this phase (add to `docs/tech-debt.md`)

| Item | Notes |
|---|---|
| Usage is aggregated in memory per request | Fine at household volume. Fix: SQL `GROUP BY` or rollup tables if `/api/usage` slows down. |
| Credits are cached in memory | After a restart the card is empty until the first fetch succeeds. No history. |
| The model override doesn't change the context window | The window is per agent. A much smaller model may fail with `context_window_exceeded`. |
| A model override can be silently ignored | When an id leaves `Models:Allowed`, the thread falls back to the agent model; a warning is logged, but the UI shows the stored override until the thread is updated. |
| Activity previews duplicate content | The same 120-character slice already shown in the thread list. |
| Overview refetches on events | Several queries per completed turn (debounced). Fine for a household; revisit with event payloads if it becomes chatty. |

## 15. Acceptance checklist

- [ ] `/` opens Overview. Tabs show Overview, Prompts, one tab per enabled agent (overflow in
      `More`), Approvals with a badge, Audit and Agents. Account is in the header menu.
- [ ] The Today / 7-day / 30-day charts show the in/out token split and total cost, and match
      `llm_calls`.
- [ ] Cost by model shows the last 7 days.
- [ ] The credits card shows the OpenRouter balance (real key), and the not-configured, fake,
      stale and failing states.
- [ ] Sending a message from another tab updates the agent card's MESSAGES and the today totals
      without a reload. Pause stops this; resume catches up.
- [ ] Prompts: pick agent and model, create a session, chat with streaming. Tool rows show status,
      duration and per-round tokens, model, latency and cost. The status bar shows context, tokens
      and cost.
- [ ] Changing the model mid-thread uses the new model from the next reply. `llm_calls.model`
      confirms it.
- [ ] Each agent tab shows usage, activity, sessions, pending approvals, settings, tokens and LLM
      calls. Create/disable an agent and the tabs update.
- [ ] `/chats/...` bookmarks redirect.
- [ ] Phone at 360 px: tabs scroll, the sessions drawer works, the status bar wraps.
- [ ] No CSP violations. jest-axe is clean. Release build has 0 warnings. `npm run check:api`
      passes.
- [ ] Every new endpoint returns `404` on the agent listener.
