# SharpClaw V3 — Phased Plan

Third iteration, started from absolute basics. Phases 3.1–3.3 are defined in detail; later phases are deliberately rough and will be refined when reached.

**Document map:** this file is the overall plan and roadmap. Each phase gets its own implementation spec once it is ready to build, and that spec is the source of truth for the phase. Phase 3.1: `sharpclaw-3.1-implementation-spec.md`.

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
- **Agent ↔ gateway (tools):** MCP over streamable HTTP, gateway as the MCP server/proxy.
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
- No multi-user support

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

## Phase 3.2 — Tools through the gateway

**Goal:** agents can use tools, and the gateway controls which ones.

**Scope**
- Gateway as MCP server/proxy (streamable HTTP)
- Per-agent tool allow-lists (stored as agent config)
- Policy engine v1 behind an interface: rule-based allow/deny per agent, tool and arguments
- Audit log of every tool call (agent, tool, arguments, result, decision)
- First tools: a small file/workspace tool and one simple external tool
- Loop and depth limits for the agent loop

**Exit criteria**
- An agent completes a multi-step task using tools.
- A tool outside the allow-list is denied and the denial is recorded.

---

## Phase 3.3 — Client API and web UI

**Goal:** replace the CLI with a proper interface.

**Scope**
- Client-facing listener with user auth
- REST endpoints for agents, sessions/threads, messages; SSE stream of agent output and events
- OpenAPI spec and generated client
- Web UI: agent list, chat view with streaming, basic audit/trace view (LLM calls and tool calls for a thread)
- Admin actions in the UI: create agent, issue and revoke tokens

**Exit criteria**
- Everything the CLI did works in the browser.
- The agent-facing listener has no client routes, and the client listener has no agent routes.

---

## Phase 3.4 — Controls *(outline)*

- Human approval: gateway pauses a risky tool call until approved in the UI
- Per-thread budgets, rate limits, global kill switch
- Richer policy rules
- Egress-style tools mediated by the gateway (e.g. web fetch with allow-lists)

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
- Context management for long threads: truncate, summarise, or both, and who owns it? (Gateway, most likely.)
