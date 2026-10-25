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
| No SSE replay | Clients that connect late must read history via `GET …/messages`. |
| Single gateway process, single SQLite file | No high availability. |
| Streaming only; no tools; no traces | Out of scope for 3.1. |

## Code-level debt

| # | Item | Severity | Why accepted | Fix when / how |
|---|---|---|---|---|
| 1 | **`ChatEndpoints.cs` is the most complex file (~355 lines).** `FinalizeCall` takes 16 parameters and is called from 5 places; streaming, budget, request parsing and recording are all in one class. | Medium | Got the endpoint working end to end first. | Before adding more chat behaviour: introduce a small per-call record (e.g. `LlmCallOutcome`) and move request parsing into its own class. |
| 2 | **`OpenRouterProvider` has no automated tests.** It was verified only manually against the live API. | Medium | The spec's test list focuses on `FakeProvider`; the real provider needed live verification. | Add unit tests using a fake `HttpMessageHandler` serving canned SSE: deltas, final usage chunk, mid-stream `error` chunk, non-2xx, idle timeout. |
| 3 | **Partial-method indirection for wiring.** `GatewayServices` and `GatewayEndpoints` are split into `partial` classes with `partial void` hooks across several files. | Low | Kept registration close to each feature. | It's one more hop than needed. Consider plain extension methods (`AddAuth()`, `MapAgentEndpoints()`) called directly from `GatewayBuilder`. |
| 4 | **String literals instead of constants in `GatewayMetrics.ObserveInboxDepth`** (`"user"`, `"pending"`). | Low | Oversight. | Use `Roles.User` / `MessageStatus.Pending`. |
| 5 | **Observable gauges query the database on every collection.** `budget.remaining_usd` loads all agents and runs one spend query per agent. | Low | Cheap at personal scale (a handful of agents, 60 s export interval). | If agent count grows, replace with a single grouped query or an in-memory cache. |
| 6 | **`fail` publishes the reason code as both `code` and `message`** in the SSE `error` event, so the CLI shows `Error: budget_exceeded` rather than friendly text. | Low | The spec gives the fail endpoint only a short code. | Map known codes to human-readable messages in the gateway or the CLI. |
| 7 | **Hand-rolled CLI argument parser** (`ParsedArgs`) instead of `System.CommandLine`. | Low | `System.CommandLine` was only available as a pre-release; the parser is about 60 lines and tested. | Switch if the CLI grows (subcommand help, validation) or a stable release lands. |
| 8 | **Starter Grafana dashboard not yet imported.** The queries were validated against VictoriaMetrics, but the dashboard was never imported. | Low | The Grafana API needs credentials. | Import via the Grafana UI and fix any panel issues. |
