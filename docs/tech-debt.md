# Tech Debt Register

Known and accepted tech debt. Each item says **why it was accepted** and **what would trigger
fixing it**, so it can be paid down deliberately rather than forgotten.

- Add an entry when you knowingly take a shortcut, or spot complexity creeping in that isn't worth
  fixing right now.
- Remove the entry (in the same PR) when it is paid down.
- Severity: **High** = likely to cause bugs or block upcoming work; **Medium** = slows work or
  hurts readability; **Low** = cosmetic or only matters at larger scale.

## Accepted by design (Phase 3.1 spec, section 20)

These are deliberate scope limits, not oversights. Revisit when the phase that needs them starts.

| Item | Notes |
|---|---|
| No provider retries | A transient provider failure fails the message. |
| Budget overshoot possible | Concurrent or in-flight calls aren't counted; cancelled calls may incur unrecorded upstream cost. |
| Single gateway process, single SQLite file | No high availability. |
| Streaming only; no tools; no traces | Out of scope for 3.1. |

## Accepted by design (Phase 3.2 spec)

| Item | Notes |
|---|---|
| Agent-reported tool outcomes are self-reported | The gateway independently records every tool call the model requested, so a missing report is visible (`status = requested`), but what a local tool actually did is only what the agent says. Docker isolation limits the damage. |
| No network tools yet | Designed in the 3.2 spec (gateway fetches under fixed rules), built in a later phase. |
| No "always allow" for approvals | Every call above the allowed level asks again. |

## Accepted by design (pre-3.3 foundations, section 15)

| Item | Notes |
|---|---|
| No summarisation | Long threads lose their oldest turns from the model's context (the thread's `context.droppedTurns` shows how many). `IContextSummariser` is the seam. |
| A failed turn stores no tool exchanges | After a failure the model doesn't know which tools it already ran in that turn; the `tool_calls` audit still records them. |
| Token estimates are a heuristic | Characters / 4. `llm_calls.estimated_prompt_tokens` sits beside the provider's count so it can be tuned. |
| Login rate-limit counters are in memory | They reset on restart. |
| Durable events duplicate reply text | `message.completed` carries the reply, bounded by `Events:RetentionDays`. |
| A pending approval blocks the agent | It processes one message at a time, so it waits up to the approval timeout. |
| No session list | Sessions are revoked only by logout, password change, an admin reset or disabling the user. |

## Code-level debt

| # | Item | Severity | Why accepted | Fix when / how |
|---|---|---|---|---|
| 3 | **Partial-method indirection for wiring.** `GatewayServices` and `GatewayEndpoints` are split into `partial` classes with `partial void` hooks across several files. | Low | Kept registration close to each feature. | It's one more hop than needed. Consider plain extension methods (`AddAuth()`, `MapAgentEndpoints()`) called directly from `GatewayBuilder`. |
| 4 | **String literals instead of constants in `GatewayMetrics.ObserveInboxDepth`** (`"user"`, `"pending"`). | Low | Oversight. | Use `Roles.User` / `MessageStatus.Pending`. |
| 5 | **Observable gauges query the database on every collection.** `budget.remaining_usd` loads all agents and runs one spend query per agent; `approvals.pending` and `tool.unreported` run a grouped query each. | Low | Cheap at personal scale (a handful of agents, 60 s export interval). | If agent count grows, replace with a single grouped query or an in-memory cache. |
| 6 | **`fail` publishes the reason code as both `code` and `message`** in the SSE `error` event, so the CLI shows `Error: budget_exceeded` rather than friendly text. | Low | The spec gives the fail endpoint only a short code. | Map known codes to human-readable messages in the gateway or the CLI. |
| 7 | **Hand-rolled CLI argument parser** (`ParsedArgs`) instead of `System.CommandLine`. | Low | `System.CommandLine` was only available as a pre-release; the parser is about 60 lines and tested. | Switch if the CLI grows (subcommand help, validation) or a stable release lands. |
| 8 | **Starter Grafana dashboard not yet imported.** The queries were validated against VictoriaMetrics, but the dashboard was never imported. | Low | The Grafana API needs credentials. | Import via the Grafana UI and fix any panel issues. |
| 9 | **Smart-mode classifier can be influenced by the call summary.** The summary includes the path, which the model chose and could word to look harmless. | Low | Only *uncertain* calls are classified, the taint bump still applies, and a misjudged call only touches the agent's own workspace. | If smart mode is extended to riskier tools, have the classifier see structured fields only, or require approval regardless of its answer for those tools. |
| 10 | **Path checks happen before execution (time-of-check vs time-of-use).** A symlink created between the check and the write isn't caught. | Low | Only the agent itself (and the host user) can change its workspace, so there's no concurrent attacker inside the sandbox. | Revisit if anything else ever writes into agent workspaces; open files with no-follow flags. |
| 12 | **Durable events are persisted and published under one global lock** (`EventPublisher`), so streams can drop already-replayed ids safely. | Low | Simple and correct; event volume is tiny. | If event throughput ever matters, assign ids from a sequence and let streams reorder a small window instead. |
| 13 | **Login rate-limit keys are only trimmed when touched.** Spraying many distinct usernames grows the in-memory map until restart. | Low | Single-user deployment behind a proxy; each entry is tiny. | Add a periodic sweep or a size cap when exposing the gateway more widely. |
| 14 | **`seq` is assigned as `max(seq)+1` inside an immediate SQLite transaction.** Concurrent writers to one thread serialise on the database write lock. | Low | SQLite serialises writers anyway; threads have one agent. | Revisit with Postgres (use a per-thread counter row or a sequence). |
