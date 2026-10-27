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
| Full thread history sent on every LLM call | No truncation/summarisation; very long threads will eventually fail with a provider error. |
| No provider retries | A transient provider failure fails the message. |
| Budget overshoot possible | Concurrent or in-flight calls aren't counted; cancelled calls may incur unrecorded upstream cost. |
| Single gateway process, single SQLite file | No high availability. |
| Streaming only; no tools; no traces | Out of scope for 3.1. |

## Accepted by design (Phase 3.2 spec)

| Item | Notes |
|---|---|
| Agent-reported tool outcomes are self-reported | The gateway independently records every tool call the model requested, so a missing report is visible (`status = requested`), but what a local tool actually did is only what the agent says. Docker isolation limits the damage. |
| Tool exchanges are not stored in thread history | Only the final reply is stored; the model doesn't see earlier turns' tool results. Visible through the tool-call audit. |
| No network tools yet | Designed in the 3.2 spec (gateway fetches under fixed rules), built in a later phase. |
| No "always allow" for approvals | Every call above the allowed level asks again. |

## Code-level debt

| # | Item | Severity | Why accepted | Fix when / how |
|---|---|---|---|---|
| 3 | **Partial-method indirection for wiring.** `GatewayServices` and `GatewayEndpoints` are split into `partial` classes with `partial void` hooks across several files. | Low | Kept registration close to each feature. | It's one more hop than needed. Consider plain extension methods (`AddAuth()`, `MapAgentEndpoints()`) called directly from `GatewayBuilder`. |
| 4 | **String literals instead of constants in `GatewayMetrics.ObserveInboxDepth`** (`"user"`, `"pending"`). | Low | Oversight. | Use `Roles.User` / `MessageStatus.Pending`. |
| 5 | **Observable gauges query the database on every collection.** `budget.remaining_usd` loads all agents and runs one spend query per agent. | Low | Cheap at personal scale (a handful of agents, 60 s export interval). | If agent count grows, replace with a single grouped query or an in-memory cache. |
| 6 | **`fail` publishes the reason code as both `code` and `message`** in the SSE `error` event, so the CLI shows `Error: budget_exceeded` rather than friendly text. | Low | The spec gives the fail endpoint only a short code. | Map known codes to human-readable messages in the gateway or the CLI. |
| 7 | **Hand-rolled CLI argument parser** (`ParsedArgs`) instead of `System.CommandLine`. | Low | `System.CommandLine` was only available as a pre-release; the parser is about 60 lines and tested. | Switch if the CLI grows (subcommand help, validation) or a stable release lands. |
| 8 | **Starter Grafana dashboard not yet imported.** The queries were validated against VictoriaMetrics, but the dashboard was never imported. | Low | The Grafana API needs credentials. | Import via the Grafana UI and fix any panel issues. |
| 9 | **Smart-mode classifier can be influenced by the call summary.** The summary includes the path, which the model chose and could word to look harmless. | Low | Only *uncertain* calls are classified, the taint bump still applies, and a misjudged call only touches the agent's own workspace. | If smart mode is extended to riskier tools, have the classifier see structured fields only, or require approval regardless of its answer for those tools. |
| 10 | **Path checks happen before execution (time-of-check vs time-of-use).** A symlink created between the check and the write isn't caught. | Low | Only the agent itself (and the host user) can change its workspace, so there's no concurrent attacker inside the sandbox. | Revisit if anything else ever writes into agent workspaces; open files with no-follow flags. |
| 11 | **Approval expiry is lazy.** An expired approval only becomes `expired` (and is counted in the metric) when it is next read. | Low | The agent always polls until decided, so in practice it is read promptly. | Add a background sweep if approvals can be abandoned (e.g. agent crash mid-wait). |
