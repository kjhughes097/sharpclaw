# SharpClaw V3 — Phased Plan

Third iteration, started from absolute basics. Phases 3.1–3.3 are defined in detail; later phases are deliberately rough and will be refined when reached.

**Document map:** this file is the overall plan and roadmap. Each phase gets its own implementation spec once it is ready to build, and that spec is the source of truth for the phase. Phase 3.1: `sharpclaw-3.1-implementation-spec.md`; Phase 3.2: `sharpclaw-3.2-implementation-spec.md`; pre-3.3 foundations: `sharpclaw-pre-3.3-foundations-spec.md`; Phase 3.3: `sharpclaw-3.3-implementation-spec.md`.

## 1. Architecture

| Component | Role |
|---|---|
| **Gateway** | Policy, LLM calls, tool calls, audit, agent key issuing. The only holder of provider keys and tool credentials. |
| **Agents** | One Docker container each. Own the agent loop. Reach only the gateway. |
| **API** | Client-facing surface, served by the gateway process on a separate listener. |
| **UI** | Web interface. Talks only to the API. |

### Principles

- The gateway is the single trust boundary and policy point. It routes, authorises and records; it contains no agent logic.
- Agents are untrusted workloads. No secrets, no direct egress, no Docker socket.
- Start simple, keep seams (interfaces) where growth is likely, and add things only when a real need appears.

## 2. Decisions made

- **Language:** C# / .NET for the gateway.
- **Gateway and API:** same codebase and process, with two Kestrel listeners (agent-facing, internal network only; client-facing, host/reverse proxy), separate route groups and separate auth schemes.
- **Client ↔ API:** REST + JSON, SSE for streaming, OpenAPI spec.
- **Agent ↔ gateway (LLM):** OpenAI-compatible `/v1/chat/completions` with SSE streaming. Gateway translates to OpenRouter (or Anthropic) behind it.
- **Tools:** the agent owns tool execution, policy, risk scoring and approval decisions (as Hermes Agent and GitHub Copilot do); the agent container is the sandbox. The gateway brokers approvals, audits every model-requested tool call and will perform network fetches for future network tools under fixed rules. (Revised in 3.2; MCP is no longer planned for agent tools.)
- **Agent ↔ agent:** routed through the gateway as a durable DB-backed mailbox with at-least-once delivery. No external broker initially; keep it behind an `IMessageBus` interface (NATS JetStream is the likely upgrade path).
- **Persistence:** SQLite via EF Core to start (Postgres possible later). Audit trail is append-only.
- **Agent keys:** issued by the gateway, shown once, only a hash stored, revocable. Injected via the agent's env in Compose.
- **Provider keys:** gateway env only (gitignored `.env`).
- **Agent volumes:** config mounted read-only (`soul.md` etc.); memory/workspace mounted read-write; one folder per agent, nothing shared unless deliberate.
- **Deployment:** Docker Compose. Agents on an `internal: true` network; only the gateway is attached to both networks. Containers started and stopped manually for now.
- **Agent loop lives in the agent container**, not the gateway.
- **Agent runtime:** a custom C# console app (small loop), using the gateway's OpenAI-compatible endpoint as its only dependency.
- **Conversation history** is stored in the gateway DB; agents are close to stateless.
- **First provider:** OpenRouter.
- **Telemetry from day one:** OpenTelemetry (OTLP) exporting logs and metrics to the existing Grafana stack. The gateway is the authoritative source; see the 3.1 implementation spec.

## 3. Non-goals for V3 (until a phase says otherwise)

- No Telegram or Slack integration
- No self-updating containers or webhook rebuild listener
- No multiple LLM backend implementations beyond what the gateway's single provider adapter needs
- No hierarchical memory architecture
- No gateway-managed container lifecycle (no Docker socket access)
- No multi-user support (the pre-3.3 schema allows several browser users, but there are no per-user permissions or data separation)

---

## Phase 3.1 — Thin slice

> **Source of truth:** `sharpclaw-3.1-implementation-spec.md`. That document holds the full detail (schema, endpoints, configuration, telemetry, Compose, tests, task order, acceptance checklist). This section is a summary only; if the two ever differ, the spec wins.

**Goal:** prove the architecture end to end with the smallest possible system: CLI → gateway → agent → gateway → OpenRouter → back to the CLI, streamed, with the agent network-isolated, every LLM call audited, a daily spend cap enforced, and telemetry flowing to Grafana.

**Scope**
- Gateway (C#/ASP.NET, SQLite, EF Core) with two listeners: client API (8080, published) and agent API (8081, internal network only)
- Per-agent tokens issued by the gateway (hash stored, shown once) and an admin key for the client API
- OpenAI-compatible streaming `/v1/chat/completions` backed by OpenRouter, plus a scriptable fake provider for testing
- Daily per-agent USD budget (UTC reset), calculated from the audit log
- Append-only `llm_calls` audit log
- Threads and messages in the gateway DB; agent inbox via long poll; live streaming to the CLI over SSE
- Agent: custom C# console app in Docker, mounted config (read-only) and workspace
- CLI for admin and chat
- OpenTelemetry logs (to Loki) and metrics (to VictoriaMetrics), with a starter Grafana dashboard
- Docker Compose with an `internal: true` agent network and a script that verifies the isolation

**Out of scope:** tools and MCP, web UI, agent-to-agent messaging, context truncation, retries, scheduled agents, approvals, tracing.

**Task order:** T0 scaffold → T1 telemetry smoke test → T2 schema → T3 listeners → T4 auth and agents API → T5 providers → T6 chat completions → T7 threads, inbox and SSE → T8 agent → T9 CLI → T10 Docker and isolation → T11 dashboard → T12 acceptance.

**Exit criteria**
- Chat with an agent from the CLI and see the reply streamed token by token.
- The agent container can reach nothing but the gateway.
- Exceeding the daily budget stops the agent with a clear error, and it works again after the next UTC midnight.
- Every LLM call appears in the audit log with tokens, cost and latency.
- The whole stack runs on the fake provider with no network access to OpenRouter.
- Logs and metrics appear in Grafana, and a single message can be followed by its correlation id.

---

## Phase 3.2 — Agent tools with policy and approval

> **Source of truth:** `sharpclaw-3.2-implementation-spec.md`. This section is a summary only.

**Goal:** agents can use tools safely, with the decision made in the agent and every call audited.

**Scope**
- Tool calling through the gateway's OpenAI-compatible `/v1/chat/completions`
- Agent-owned policy: tools and an allowed risk level from read-only config; each call scored from
  its arguments (`low`/`medium`/`high`/`critical`); hardline blocklist; taint tracking; optional
  smart mode (auxiliary LLM scores uncertain calls)
- Human approval when the risk is above the allowed level, brokered by the gateway to the CLI
  (pulled forward from 3.4); deny when nobody can answer or after a timeout
- Workspace tools only (list, read, write, delete); no code or shell execution
- Audit of every tool call (gateway records what the model requested; the agent reports outcomes)
- Loop limits (agent iteration cap; gateway backstop)

**Deferred:** network tools (`download_to_workspace`, `http_fetch`) — designed in the 3.2 spec,
built in a later phase. Superseded by the (now implemented)
[external tools spec](sharpclaw-external-tools-spec.md) (credentialed tools in the gateway; network
access, cloning and code execution in a per-agent `sandbox-<agent>` whose only exit is an egress
proxy built into the gateway).

**Exit criteria**
- An agent completes a multi-step task using tools.
- A call above the allowed risk prompts the user and runs only if approved; denials are recorded.

---

## Pre-3.3 foundations (built)

> **Source of truth:** `sharpclaw-pre-3.3-foundations-spec.md`. This section is a summary only.

Five things the web UI needs, built before it so the UI doesn't have to work around them:

- **Browser authentication:** gateway-native username/password login, server-side sessions in an
  `HttpOnly`, `SameSite=Strict` cookie, CSRF and origin checks, login rate limiting. The admin key
  stays for the CLI and scripts and is the only credential for user management. The UI is served
  same-origin; TLS ends at a reverse proxy.
- **Tool exchanges in thread history:** stored (truncated) and replayed to the model; untrusted
  results from earlier turns are replaced by a stub. Rows are ordered by a per-thread `seq`.
- **Context management:** the gateway fits whole turns into a per-agent window (default 32,000
  tokens, character-based estimate) and reports dropped turns; a summariser seam exists but
  summarisation is deferred.
- **SSE replay:** durable events in `thread_events` with `Last-Event-ID` replay, snapshots of
  in-flight replies, a global `/api/events` stream, 14-day retention; the CLI reconnects.
- **Approvals without a watcher:** a durable approval queue with an expiry sweeper and a
  notifier seam; `unattended` messages deny at once.

---

## Phase 3.3 — Client API and web UI (built)

> **Source of truth:** `sharpclaw-3.3-implementation-spec.md`; UI notes in `ui.md`. Built: thread
> list/detail/rename/archive, message paging, a per-thread trace, audit cursor paging, system info
> and agent last-seen on the client API; an OpenAPI snapshot and generated TypeScript client; the
> React UI served by the gateway with a strict CSP (chat with streaming, tool cards, inline and
> inbox approvals, agents and tokens, audit and trace, account), one global event stream per tab,
> and a Playwright suite against an isolated fake-provider stack.

**Goal:** replace the CLI with a proper interface.

**Scope**
- Client-facing listener with user auth (login, sessions and CSRF are in place from the pre-3.3 foundations)
- REST endpoints for agents, sessions/threads, messages; SSE stream of agent output and events (replayable)
- OpenAPI spec and generated client
- Web UI: agent list, chat view with streaming, basic audit/trace view (LLM calls and tool calls for a thread)
- Admin actions in the UI: create agent, issue and revoke tokens

**Exit criteria**
- Everything the CLI did works in the browser.
- The agent-facing listener has no client routes, and the client listener has no agent routes.

---

## Phase 3.3.1 — Tabbed UI redesign (built)

> **Source of truth:** `sharpclaw-3.3.1-ui-redesign-spec.md` (mockups: `sample-overview-tab.png`,
> `sample-prompts-tab.png`). UI notes in `ui.md`.

**Goal:** replace the 3.3 sidebar shell with a tabbed, dashboard-style UI.

**Scope**
- Tabs: Overview (usage charts, cost by model, provider credits, live per-agent cards), Prompts
  (agent/model pickers, sessions, chat with inline tool metrics and a status bar), one tab per
  agent, and the existing Approvals, Audit and Agents pages; Account in a header menu
- Gateway additions: `GET /api/usage`, `GET /api/activity` (with the message sender recorded), a
  per-thread model override from a `Models:Allowed` allow-list, a cached OpenRouter credits balance

**Exit criteria**
- The acceptance checklist in the spec passes, with the fake provider and with a real OpenRouter session.

---

## Phase 3.4 — Controls *(outline)*

- Human approval in the UI (the CLI flow lands in 3.2; the queue API in the pre-3.3 foundations)
- Per-thread budgets, rate limits, global kill switch
- Richer policy rules
- Network tools mediated by the gateway (design agreed in the 3.2 spec)
- More agent-local tools (workspace search/find/edit/append/move/copy, read paging,
  `current_time`, `calculate`): see the [agent tools spec](sharpclaw-agent-tools-spec.md)
- Skills: operator-curated instructions (and scripts) on a shared read-only `/skills` volume,
  loaded on demand with `skill_read`: see the [skills spec](sharpclaw-skills-spec.md)

## Phase 3.5 — Agent-to-agent messaging *(outline)*

- Mailbox table, `POST /messages`, delivery via long-poll/SSE, acks, idempotency keys
- Policy: allowed sender → recipient pairs
- Correlation IDs for request/reply, thread hop limits and budgets, TTL and dead-letter status
- `IMessageBus` interface

## Phase 3.6 — Autonomy *(outline)*

- Wake/interval agents running on a schedule
- Wake and chat modes sharing memory with single-writer-per-file logs
- Visibility in the UI of what agents did in the background

## Phase 3.7+ — Backlog *(undefined, to be refined)*

- Observability beyond 3.1: alerts, direct agent telemetry, trace view in the UI
- Agent memory model and tooling
- External channels (Slack, Telegram)
- Container supervisor / gateway-managed lifecycle (needs a deliberate security decision)
- Trust-tier brokers (research agent, trusted-actions broker)
- Google A2A compatibility for talking to outside agents
- Postgres, backups, upgrade-without-downtime story

---

## Open questions

- Agent memory beyond conversation history: what lives in the gateway DB vs. mounted workspace files, and who can write what? (Deferred past 3.1.)
- Does the full agent definition (persona, tools, limits) eventually move into the gateway as data, with `soul.md` as the only mounted file?
- Context management for long threads: truncate, summarise, or both, and who owns it? (Decided in the pre-3.3 foundations: the gateway drops whole oldest turns; summarisation is a future extension behind `IContextSummariser`.)
