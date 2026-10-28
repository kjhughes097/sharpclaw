# Post-Phase 3.3 Codebase Review

**Date:** 2026-10-28 · **Commit:** `364915b` (main, after Phase 3.3) · **Scope:** the whole repository —
`src/`, `tests/`, `ui/`, `docker/`, `scripts/`, `docs/` and the Copilot guidance in `.github/`.

> **Follow-up:** every item below was addressed in PRs #131–#135; see [§11 Remediation status](#11-remediation-status).

This review covers architecture, coding strategy, best practice, SOLID, readability and how easy the
repo is for an AI agent to work in. It lists where the code departs from those goals and records the
tech debt found **by reading the code**, not just the debt already in [tech-debt.md](tech-debt.md).

---

## 1. Executive summary

SharpClaw is in good shape for a personal system at the end of Phase 3.3. The security model (the
gateway is the trust boundary, listeners are separated by local port, agents hold no keys, tools go
through one dispatcher) is clear, enforced in code and covered by tests. The code is consistent, well
commented with *why* comments, and the build is clean:

| Check | Result |
|---|---|
| `dotnet build -c Release` | 0 warnings, 0 errors |
| `dotnet test` | 304 passed (Gateway 202, Agent 77, CLI 25); Gateway suite takes about 30 s |
| UI | Strict TypeScript, ESLint with `jsx-a11y`, Vitest + MSW, Playwright (2 specs) |
| CI | **None** (tech-debt #16) |

The main weaknesses are not bugs. They are **pressure points that will hurt in Phase 3.4+**:

1. **AI-agent guidance is stale.** [copilot-instructions.md](../.github/copilot-instructions.md) still
   names only the 3.1 and 3.2 specs as the source of truth and lists **"web UI" as out of scope**,
   which contradicts the shipped 3.3 UI. There are no instructions for `ui/` at all. This is the
   highest-value fix in this report because every future agent session reads that file first.
2. **Fat endpoint handlers.** Business rules (ownership checks, state transitions, transactions,
   event publishing, metrics and logging) live directly in minimal-API handlers that take 10–13
   injected parameters. The gateway has no domain/service layer for messages or threads.
3. **Unbounded in-memory structures.** These are new findings that are not in the register: per-key
   semaphores are never removed, SSE channels are unbounded, and the context endpoint loads a whole
   thread on every turn.
4. **No CI**, so the OpenAPI/fixture snapshots, `check:api`, lint and the test suites protect
   nothing unless someone remembers to run them.
5. **Tests rely on real-time sleeps** in several places, and the test instructions themselves tell
   agents to "give it ~200 ms".

### Scorecard

| Area | Rating | One-line verdict |
|---|---|---|
| Architecture | ★★★★☆ | Clear trust boundary and project split; the gateway is missing a service layer between endpoints and EF. |
| Security & privacy | ★★★★★ | Defence in depth, fixed-time compares, hashed tokens, no content in logs or metrics, architecture test on the tool path. |
| Coding conventions | ★★★★☆ | Strong and mostly followed (TimeProvider, UTC, v7 ids, single error shape); a few magic strings and scope-key drift. |
| SOLID | ★★★☆☆ | Good seams at real boundaries; SRP is stretched in handlers and the large UI components. |
| Readability | ★★★★☆ | Excellent intent comments; long parameter lists and repeated boilerplate add noise. |
| Tests | ★★★★☆ | Broad, behaviour-focused and privacy-aware; some flakiness risk and thin unit coverage of infrastructure. |
| AI-agent suitability | ★★★☆☆ | Good naming and docs, but stale guidance and four layered specs make "what is true now" expensive to work out. |

---

## 2. Architecture

### 2.1 What is there

```
Contracts  ◄── Gateway (ASP.NET Core minimal APIs, EF Core + SQLite, OTel)
    ▲            ├── client listener :8080  /api/*, /openapi, SPA (ui/dist → wwwroot)
    │            └── agent listener  :8081  /agent/*, /v1/chat/completions
    ├── Agent  (console loop → IGatewayClient; ToolDispatcher → workspace tools)
    └── Cli    (sharpclaw → ICliGateway)
ui/  React 19 + TanStack Query + openapi-fetch (types generated from the committed OpenAPI doc)
```

**Strengths**

- **One trust boundary, enforced structurally.** `ListenerFilter` decides by
  `Connection.LocalPort`, never by `Host` ([ListenerFilter.cs](../src/SharpClaw.Gateway/Infrastructure/ListenerFilter.cs)),
  and every route group is created with `.RequireListener(...)` in
  [GatewayEndpoints.cs](../src/SharpClaw.Gateway/GatewayEndpoints.cs). A route on the wrong
  listener returns 404, which is tested.
- **The tool path can't be bypassed.** `ToolDispatcher` is documented as the only caller of
  `ExecuteUncheckedAsync`, and `ToolArchitectureTests` enforces it. This is a good example of
  turning an architectural rule into a test.
- **The contract is pinned.** Wire shapes live in `SharpClaw.Contracts`. The OpenAPI doc and one
  fixture per SSE event are snapshot-tested, and the UI generates its types from the same
  committed doc (`npm run check:api`), so a contract change can't slip through silently (once CI exists).
- **The event model is sound.** Durable events are persisted, then published under one lock, so ids
  are monotonic. `delta` is ephemeral. Streams subscribe *before* computing the replay baseline,
  and snapshots plus `EndOffset` de-duplicate deltas
  ([EventStreamResult.cs](../src/SharpClaw.Gateway/Endpoints/EventStreamResult.cs)). The ordering
  is subtle, and the comments explain it well.
- **The agent is boring in a good way.** It runs one message at a time, has a clear `LoopOutcome`
  state machine, uses exponential backoff with jitter, and fails closed on approval errors.

**Weaknesses**

- **There is no application/service layer in the gateway.** Endpoint classes do validation,
  ownership checks, transactions, `seq` assignment, thread-activity updates, event publishing,
  metrics and logging in one method. For example, `AgentEndpoints.Reply`
  ([AgentEndpoints.cs](../src/SharpClaw.Gateway/Endpoints/AgentEndpoints.cs)) takes 13 parameters
  and does all of these. "Post a user message" is in `ThreadsEndpoints.PostMessage`, while
  "complete a turn" and "fail a turn" are in `AgentEndpoints`. These are three state transitions of
  one aggregate (a message turn) spread across two files with no shared owner.
- **Service style is inconsistent.** Some services are static helpers that take a `DbContext`
  (`InboxRepository`, `MessageQueries`, `AgentQueries`, `ThreadActivity`). Some are DI singletons
  that *also* take the scoped `DbContext` as a parameter (`ApprovalDecisions`). Others resolve their
  own scope (`EventPublisher`, `GatewayMetrics`). All three work, but a reader (or an agent) has to
  learn three patterns.
- **Some files are in the wrong folder.** `Endpoints/` contains non-endpoint types:
  `LlmCallRecorder`, `ChatChunkWriter`, `ChatRequestParser` and `EventStreamResult`. Folders mean
  less than they should when you navigate by them.
- **Partial-method wiring** (`GatewayServices`/`GatewayEndpoints` split into `partial` classes with
  `partial void` hooks) is still present (tech-debt #3). It adds one indirection hop and hides the
  registration list from `GatewayBuilder`.

### 2.2 Data and persistence

- SQLite with EF Core, snake_case naming and a pragma interceptor. There are 10 migrations. The
  `Designer.cs` files make up most of the line count (~5k lines), which is normal.
- Raw SQL is used in the right places: atomic claim/requeue with `RETURNING` in
  [InboxRepository.cs](../src/SharpClaw.Gateway/Services/InboxRepository.cs), with good comments.
- **No optimistic concurrency** anywhere (no concurrency tokens or `RowVersion`). Approvals use a
  conditional `ExecuteUpdate` correctly. Message completion (`Reply`) relies on a transaction plus a
  unique index, but `Fail` is a plain read-modify-write with no status guard in the `UPDATE`. Today
  this is safe only because an agent processes one message at a time.
- State and events aren't written atomically: the state change commits, then
  `PublishDurableAsync` writes the event in a separate scope. A crash between the two loses the
  event, and clients only recover on their next refetch. This is acceptable at this scale, but it
  isn't documented as debt.

---

## 3. Coding strategy and conventions

The conventions in the Copilot instructions are, for the most part, **followed**:

| Convention | Status |
|---|---|
| `TimeProvider` injected, no `DateTime.UtcNow` in product code | ✅ Zero occurrences in `src/` (the only match is a DTO property named `UtcNow`). |
| UTC `DateTime`, `Guid.CreateVersion7()`, money as `double` | ✅ |
| Single error shape via `ApiResults.*`; foreign ids → 404 | ✅ Consistent, including inside `ExceptionMiddleware`. |
| Tokens stored as SHA-256 + prefix; fixed-time compares | ✅ `Hashing.FixedTimeEquals` for admin key and CSRF. |
| No content in logs or metric labels | ✅ Exceptions are logged by **type name** only in the agent and dispatcher; notifier failures likewise. |
| Status/role values via constants | ⚠️ Mostly. Raw `"user"`/`"pending"`/`"claimed"` remain in `InboxRepository` SQL, `GatewayMetrics.ObserveInboxDepth` (tech-debt #4, still unpaid) and `FakeProvider`. |
| Log scope keys `agent`, `agent_id`, `thread_id`, `correlation_id` | ⚠️ The agent uses **`message_id`** instead of `correlation_id` ([AgentLoop.cs](../src/SharpClaw.Agent/AgentLoop.cs) line 100), so agent and gateway logs for one turn don't join on the same key in Loki. |
| `SharpClawJson.Options` for JSON | ⚠️ Two configurations exist. `ConfigureHttpJsonOptions` in `GatewayBuilder` sets `NumberHandling = Strict` for request binding, while `SharpClawJson.Options` (Web defaults, which allow numbers as strings) is used for every response. They differ silently. |

**Patterns worth keeping:** status records instead of exceptions for expected outcomes
(`InboxStatus`, `LoopOutcome`, `ToolAssessment`). Small `sealed` classes, primary constructors and
records everywhere. Comments that say *why* ("Record before [DONE]: clients read until [DONE], so…").

**Patterns to reconsider:**

- **Repeated logging-scope boilerplate.** `logger.BeginScope(new Dictionary<string, object> { ["thread_id"]…, ["correlation_id"]… })`
  appears 10 times, and `loggerFactory.CreateLogger("SharpClaw.Gateway.X")` 12 times inside handlers.
  A tiny `LogScopes.Turn(logger, threadId, correlationId)` helper would remove about 60 lines of
  noise and make the key names impossible to misspell.
- **Repeated ownership checks.** `thread is null || thread.AgentId != agent.Id → NotFound` and the
  "claimed user message belonging to this agent" check are written out in `ChatEndpoints`,
  `AgentEndpoints.Reply`, `AgentEndpoints.Fail`, `ApprovalEndpoints.Create` and others. Each copy is
  slightly different (`Reply` doesn't require `Claimed` before its own status checks; `Fail`
  doesn't check the thread). One `TurnGuard.LoadClaimedAsync(...)` helper would keep the rule in
  one place. That matters because this check *is* the security boundary between agents.
- **Options are not validated.** 14 options classes are bound with `Configure<T>()`. Only
  `AdminKey`, PBKDF2 iterations and the OpenRouter key are checked at startup. Negative timeouts,
  zero limits or a `ClientPort == AgentPort` misconfiguration would start up and misbehave.
  `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` is the idiomatic fix and
  would also shorten `GatewayBuilder`.
- **String constants instead of enums for state.** `MessageStatus`, `ApprovalStatuses`,
  `ToolCallStatuses` and the others are `const string` classes. This is pragmatic for SQLite and
  JSON, but the compiler can't check that a `switch` is exhaustive, and the `status is not (A or B or C or D)`
  validation lists drift from the class (see `ApprovalEndpoints.List`). An `All` array on each
  constants class (as `RiskLevels.All` already does) would be a cheap middle ground.

---

## 4. SOLID, pragmatically

| Principle | Assessment |
|---|---|
| **S**ingle responsibility | ✅ Agent: `AgentLoop`, `ToolDispatcher`, `ToolRegistry`, `WorkspaceRoot` are well separated. `ChatEndpoints.ChatStream` pulls per-call stream state out of the handler. ⚠️ Gateway handlers mix six concerns (see §2.1). ⚠️ UI: `ThreadView.tsx` (469 lines), `AgentDetailPage.tsx` (503) and `AuditPage.tsx` (482) each combine queries, mutations, scrolling and rendering. |
| **O**pen/closed | ✅ New tools plug in through `IAgentTool` with no dispatcher change. Providers through `IChatProvider`. Notifier and summariser seams exist (`IApprovalNotifier`, `IContextSummariser`) with null implementations. ⚠️ Adding a new SSE event touches Contracts, a fixture, the UI union type and `eventEffects.ts`. This is acceptable and snapshot-guarded. |
| **L**iskov | ✅ No inheritance hierarchies to speak of. `KeyedSignal` is the only abstract base and is used correctly. |
| **I**nterface segregation | ✅ Interfaces are small and exist only at real boundaries (12 in total), which matches the "minimal indirection" principle. `IGatewayClient` (agent) is the widest and still cohesive. |
| **D**ependency inversion | ✅ At boundaries: provider, HTTP, time, soul file. ⚠️ Inside the gateway, handlers depend on `SharpClawDbContext` directly. That's fine for a small app, but it's why business rules can't be unit-tested without the whole fixture (all 202 gateway tests are integration tests). |

**Verdict:** the project follows its own rule ("don't let SOLID become an excuse for extra layers")
well. The one place where *adding* a layer earns its keep is a thin turn/message service in the
gateway, because it already has several callers and contains security-critical rules.

---

## 5. Readability

**Good:**

- XML doc comments on almost every public type explain intent and link the governing spec section
  (e.g. "pre-3.3 spec section 6.4").
- Names match the domain: `ActiveTurnBuffers`, `ApprovalDecisions.TryDecideAsync`,
  `InboxRepository.TouchClaimAsync` ("claim heartbeat").
- The streaming code numbers its steps ("1. Subscribe first… 2–4… 5… 6.") so the ordering
  argument can be followed top to bottom.

**Harder than it needs to be:**

- **Long handler signatures** (`Completions` has 11 parameters, `Reply` 13, `Create` approval 10).
  Grouping them with `[AsParameters]` records would make the handler body the first thing a reader sees.
- **`ChatEndpoints.Completions`** runs three guards (loop limit, budget, context), each with a
  record + metric + log + return block. A small `RejectAsync(code, …)` helper would make the
  pipeline readable at a glance.
- **`Dtos.cs` is 364 lines / 64 records in one file.** Splitting by area (threads, agents,
  approvals, audit, auth) would match how the endpoints and OpenAPI tags are organised.
- **`MessageQueries.ListAsync` and `PageAsync`** duplicate the anchor-resolution logic. `ListAsync`
  now only serves the agent history endpoint.
- **`AgentLoop` backoff** clamps twice (`backoff < Max ? backoff*2 : Max; if (backoff > Max) …`).
  It's minor, but it's the kind of thing a reader stops on.

---

## 6. Suitability for AI agents

This matters because the repo is built mostly by agents.

**What works well**

- `.github/copilot-instructions.md` is concise and actionable: build commands, conventions, hard
  security rules and git workflow. `tests.instructions.md` and `docker.instructions.md` are scoped
  by `applyTo`.
- Orientation docs ([3.3-orientation.md](3.3-orientation.md)) map spec terms to real file names.
  This is excellent for agents and worth continuing every phase.
- Architecture rules are *tested*, not just written down (tool dispatcher, listener separation,
  privacy, OpenAPI snapshot). An agent that breaks them finds out from `dotnet test`.
- Files are small. Excluding migrations, no C# file in `src/` exceeds 340 lines.

**What works against agents**

1. **Stale guidance (High).** `copilot-instructions.md`:
   - names only the 3.1 and 3.2 specs as the source of truth, with no 3.3 spec, pre-3.3 foundations
     doc or `ui.md`;
   - lists **"web UI"** as out of scope, so a well-behaved agent will refuse UI work or flag the
     existing UI as a violation;
   - its Architecture section doesn't mention `ui/`, the client OpenAPI doc, `UiHosting`, the
     session/CSRF model, or the two-step snapshot workflow (`UPDATE_OPENAPI`, `npm run gen:api`);
   - has no Build and Test commands for `ui/` (`npm ci`, `lint`, `typecheck`, `test`, `check:api`,
     `e2e`).
   There's also no `ui.instructions.md` (`applyTo: ui/**`) covering tokens-not-colours, query-key
   helpers, the stream manager's ownership of `EventSource`, and the CSP constraints.
2. **The spec stack is layered (Medium).** To learn the current behaviour, an agent has to merge
   3.1 → 3.2 → [sharpclaw-3.2-post-implementation-work.md](sharpclaw-3.2-post-implementation-work.md)
   (the "pre-3.3 foundations" — its filename doesn't say so) → 3.3, with "later wins" rules. That
   is about 2,200 lines of spec before writing any code. A maintained **"current system" reference**
   (`docs/architecture.md`: components, endpoints, events, tables, invariants) would let specs become
   history instead of required reading.
3. **The test guidance teaches sleeps.** "subscribe… (and give it ~200 ms) before triggering the
   event" means agents copy `Task.Delay(200)` into new tests (see §8).
4. **Multiple patterns for the same thing** (three service styles, two JSON configurations, two log
   key names for the correlation id) make "follow the existing pattern" ambiguous.

---

## 7. Where we deviated from our own principles

| Principle (from copilot-instructions) | Deviation |
|---|---|
| "Every change adds or updates unit tests" | Gateway infrastructure such as `PageCursor`, `ThreadEventBus`, `ActiveTurnBuffers`, `KeyedSignal`, `ExceptionMiddleware` and `EventRetentionService` has no focused tests. It is only reached indirectly through integration tests. |
| "Watch for creeping complexity… call it out" | Gateway endpoint handlers and three UI pages have grown past the point where they should have been flagged. Neither is in the tech-debt register. |
| "Minimal indirection" | Partial-method wiring (#3) is still in place. |
| "Use constants" (Roles/MessageStatus) | Magic strings remain (#4 is still unpaid, plus raw SQL and `FakeProvider`). |
| Logging keys `correlation_id` | The agent uses `message_id`. |
| "Commit in small, green increments" | 3.3 landed as a few very large commits (`527ab2a` "T4–T9" in one commit). Reviewing and bisecting is harder. |
| "Tech debt register… add an entry when you knowingly take a shortcut" | Several shortcuts below were taken without an entry (unbounded channels, signal leak, full-thread context load, non-atomic state/event write). |
| Tests use `FakeTimeProvider`, never `DateTime.UtcNow` | `ApprovalTests.cs` (around lines 309–313), CLI fakes and `ToolFakes.cs` use `DateTime.UtcNow`. |

---

## 8. Tech debt found in code review (not already in the register)

Severity uses the register's scale: **High** = likely to cause bugs or block upcoming work;
**Medium** = slows work or hurts readability; **Low** = cosmetic or only matters at scale.

| # | Item | Where | Severity | Suggested fix |
|---|---|---|---|---|
| R1 | **Copilot instructions are stale after 3.3.** "Web UI" is listed as out of scope, there is no 3.3 spec reference and no UI build/test commands. | [.github/copilot-instructions.md](../.github/copilot-instructions.md) | **High** | Update the source-of-truth list and Out-of-scope list, add a `ui/` section and an `ui.instructions.md`. |
| R2 | **`KeyedSignal` never removes semaphores.** `ApprovalSignal` creates one `SemaphoreSlim` per approval id, which lives forever. Inbox signals are per agent, so they're fine. | [AgentInboxSignal.cs](../src/SharpClaw.Gateway/Services/AgentInboxSignal.cs) | Medium | Remove the entry when the approval is decided or expired (`ApprovalDecisions.TryDecideAsync` already signals), or use a `TaskCompletionSource` map with removal. |
| R3 | **Unbounded SSE channels.** A slow or stalled browser tab accumulates every event (including all deltas) in memory until it disconnects. | [ThreadEventBus.cs](../src/SharpClaw.Gateway/Services/ThreadEventBus.cs) | Medium | Bounded channel (e.g. 1,000) with `DropWrite` + close the subscription; the client reconnects with `Last-Event-ID` and gets a replay/snapshot. |
| R4 | **The context endpoint loads every row of the thread on every turn** and the turn grouping runs in memory. Cost grows linearly with thread length on the hot path. | [AgentEndpoints.cs](../src/SharpClaw.Gateway/Endpoints/AgentEndpoints.cs) line 101 | Low (now) / Medium (long threads) | Load newest-first in pages until the budget is full, or cap the scan at N rows. |
| R5 | **Fat endpoint handlers / no turn service.** The rules for claimed-message ownership, completion and failure are spread across `ChatEndpoints`, `AgentEndpoints`, `ApprovalEndpoints` and `ThreadsEndpoints`, with small differences between copies. | `src/SharpClaw.Gateway/Endpoints/` | Medium | Introduce a `MessageTurns` (or `TurnService`) for post/claim/complete/fail plus a single ownership guard; endpoints become thin. |
| R6 | **`Fail` has no conditional update.** Read-modify-write on `messages.status` without a `WHERE status='claimed'` guard; a concurrent `Reply`/requeue could be overwritten. | [AgentEndpoints.cs](../src/SharpClaw.Gateway/Endpoints/AgentEndpoints.cs) line 221 | Low | `ExecuteUpdate … WHERE status = 'claimed'` and 404/409 on 0 rows, mirroring `ApprovalDecisions`. |
| R7 | **State change and durable event aren't atomic.** The event is written in a separate scope after the commit, so a crash in between loses it. | [EventPublisher.cs](../src/SharpClaw.Gateway/Services/EventPublisher.cs) | Low | Record in the register; fix with an outbox (write the event in the same transaction, publish after commit) if it ever matters. |
| R8 | **Two JSON configurations.** Request binding is `Strict` numbers; responses and `SharpClawJson.Options` allow strings. | [GatewayBuilder.cs](../src/SharpClaw.Gateway/GatewayBuilder.cs), [SharpClawJson.cs](../src/SharpClaw.Contracts/SharpClawJson.cs) | Low | Build the HTTP options *from* `SharpClawJson.Create()` (and set `Strict` there) so there's one definition. |
| R9 | **Options aren't validated at startup** beyond three keys. | [Options.cs](../src/SharpClaw.Gateway/Configuration/Options.cs) | Low | DataAnnotations + `ValidateOnStart()`; add a port-collision check. |
| R10 | **Log-scope key drift:** the agent uses `message_id` and the gateway uses `correlation_id`. | [AgentLoop.cs](../src/SharpClaw.Agent/AgentLoop.cs) line 100 | Low | Rename to `correlation_id`; add an `agent` key too. |
| R11 | **Magic strings in raw SQL and `FakeProvider`** (in addition to register #4). | `InboxRepository.cs`, `FakeProvider.cs` line 38 | Low | Parameterise statuses or interpolate constants. |
| R12 | **Logging boilerplate:** 10 hand-built scope dictionaries and 12 string-category loggers. | Gateway endpoints | Low | `LogScopes` helper; typed `ILogger<T>` per endpoint class. |
| R13 | **Misplaced files:** non-endpoint types live under `Endpoints/`. | `LlmCallRecorder`, `ChatChunkWriter`, `ChatRequestParser`, `EventStreamResult` | Low | Move to `Services/Chat/` and `Infrastructure/Sse/`. |
| R14 | **Monolithic `Dtos.cs`** (64 records). | [Dtos.cs](../src/SharpClaw.Contracts/Dtos.cs) | Low | Split by area. |
| R15 | **Classifier parse takes the first risk word**, so "not low — critical" parses as `low`. | [ToolDispatcher.cs](../src/SharpClaw.Agent/Tools/ToolDispatcher.cs) line 229 | Low (smart mode only affects *uncertain* workspace calls; related to register #9) | Require the reply to be exactly one word (after trimming punctuation); otherwise fall back to High. |
| R16 | **All users see all threads/agents/approvals; `decided_by` records `"user"`, not *which* user.** This is consistent with a household/personal system, but it isn't written down as a decision. | `ApprovalEndpoints.Decide`, `ThreadsEndpoints` | Low | Record it as accepted by design; store the user id in `decided_by` for audit. |
| R17 | **Tests use sleeps and real time.** `Task.Delay` in ~20 places (e.g. `GlobalStreamTests`, `EventReplayTests`, `ApprovalTests`), polling loops in `CliTests`, `page.waitForTimeout(1500)` in [chat.spec.ts](../ui/e2e/chat.spec.ts). The test instructions encourage "~200 ms". | `tests/`, `ui/e2e/` | Medium | Add a stream-ready signal (the server already sends `ready`; await it instead of sleeping) and an `EventuallyAsync` helper; update `tests.instructions.md`. |
| R18 | **Large test files:** `ClientApiTests` (516 lines), `CliTests` (514), `AuthTests` (440), `ApprovalTests` (402). | `tests/` | Low | Split by behaviour (e.g. threads / messages / paging). |
| R19 | **Large UI components and scattered invalidation.** `ThreadView`, `AgentDetailPage`, `AuditPage` and `components/ui.tsx`; reconnect recovery invalidates by literal keys (`['thread']`, `['messages']`) rather than `queryKeys`. | `ui/src/features/**`, [eventEffects.ts](../ui/src/api/eventEffects.ts) | Medium | Extract feature hooks (`useThreadMutations`, …) and presentational parts; route all keys through `queryKeys.ts`. |
| R20 | **Stream-manager lifecycle races.** Concurrent `CLOSED`/error callbacks can each start an async session check, and a stale check can schedule a timer after a newer source opened. Guarded today by `!this.source`, but the sequencing isn't deterministic. `LiveStore` rAF/timeout callbacks aren't cancelled on sign-out. | [stream.ts](../ui/src/api/stream.ts), [live.ts](../ui/src/api/live.ts) | Medium | Generation counter per source; ignore results from older generations; `dispose()` on `LiveStore`. |
| R21 | **Thin E2E coverage:** two Playwright specs (chat, tools). No E2E for login/expiry, approvals, agents/tokens, audit, account, or reload mid-stream. | `ui/e2e/` | Medium | Add an approvals spec and a reload-during-stream spec first; they cover the riskiest flows. |

Items already in the register that this review **confirms are still open and still correctly
rated**: #3 (partial wiring), #4 (magic strings in metrics), #5 (gauges query the DB), #6 (fail
reason shown as code), #12 (global publish lock), #16 (no CI). I'd raise **#16 to High**: with
OpenAPI, fixture and `check:api` snapshots in place, CI is now what makes them worth having.

---

## 9. Recommended improvements, prioritised

### Do next (before Phase 3.4 work starts)

1. **Refresh the AI guidance** (R1). Update `copilot-instructions.md` (sources of truth, scope, `ui/`,
   commands), add `.github/instructions/ui.instructions.md`, and replace the "~200 ms" advice in
   `tests.instructions.md` with "await the `ready` event". *Small effort, high leverage.*
2. **Add CI** (register #16). One workflow: `dotnet build -c Release`, `dotnet test`, and in `ui/`:
   `npm ci && npm run lint && npm run typecheck && npm test && npm run check:api && npm run build`.
   Leave Playwright as a manual or nightly job at first.
3. **Fix the two memory growth issues** (R2, R3). Each is a few lines and removes a class of
   long-uptime problems.

### Soon (as part of the next feature that touches the area)

4. **Introduce a turn service + ownership guard** in the gateway (R5, R6). Move post/claim/complete/
   fail and the "claimed message belongs to this agent" rule into one class with unit tests. Keep
   endpoints as thin HTTP adapters. Consider `[AsParameters]` to shorten signatures.
5. **Make tests deterministic** (R17). Add `TestHelpers.WaitForReadyAsync` and `EventuallyAsync`,
   then remove `Task.Delay` from gateway tests and `waitForTimeout` from Playwright.
6. **Split the large UI pages** (R19) into hooks plus presentational components, and centralise query keys.
7. **Harden the stream manager** (R20) with a generation counter, and add tests for concurrent
   close and reopen during a session check.

### Opportunistically (pay down when touching the file)

8. Unify the JSON options (R8), validate options on start (R9), align log keys (R10), remove magic
   strings (R11, #4), add the logging-scope helper (R12), move misplaced files (R13), split `Dtos.cs`
   (R14), tighten classifier parsing (R15).
9. Replace partial-method wiring with plain `AddX()`/`MapX()` extension methods (#3). Do it in the
   same PR as the options-validation change, because both rewrite `GatewayBuilder`.
10. Write a **`docs/architecture.md`** "current system" reference and mark the phase specs as
    historical. Rename `sharpclaw-3.2-post-implementation-work.md` to say "pre-3.3 foundations".
11. Record R7 and R16 in the tech-debt register as accepted-by-design if no fix is planned.

---

## 10. What to keep doing

- Turning architectural rules into tests (tool dispatcher, listener separation, privacy, snapshots).
- Orientation docs that map spec language to real names at the start of each phase.
- *Why* comments at every subtle ordering or concurrency decision.
- Seams only at real boundaries (`IChatProvider`, `IGatewayClient`, `ICliGateway`, `TimeProvider`).
- Fail-closed defaults (approvals, tool config, startup validation).
- Keeping the Release build at zero warnings.

---

## 11. Remediation status

Tracks the follow-up work. Each PR updates its rows.

| Item | Status | PR / notes |
|---|---|---|
| R1 AI guidance | ✅ Done | #131 (`chore/agent-guidance-and-ci`): copilot-instructions refreshed, `ui.instructions.md` added, `docs/architecture.md` added, foundations spec renamed to `sharpclaw-pre-3.3-foundations-spec.md`. Test-instruction wording follows with the test helpers (R17). |
| #16 CI | ✅ Done | #131 (`chore/agent-guidance-and-ci`): `.github/workflows/ci.yml` (.NET, UI checks, Playwright Chromium). |
| #3, R8, R9 wiring, JSON, options | ✅ Done | #132 (`refactor/gateway-structure`): `AddSharpClaw*()`/`MapSharpClawEndpoints()`, validated options (source-generated + cross-field rules), `SharpClawJson.Apply`. |
| R5 turn service, R10–R14, #4 | ✅ Done | #132 (`refactor/gateway-structure`): `MessageTurns` + `TurnGuard`, `[AsParameters]` chat services, `LogScopes`, agent logs `correlation_id`, constants in SQL/metrics, files moved, `Dtos.cs` split. |
| R2, R3 memory | ✅ Done | #133 (`fix/gateway-hardening`): approval signals removed when decided; bounded SSE buffers (`Events:SubscriberBufferSize`) with disconnect-on-overflow and `sharpclaw.sse.overflows`. |
| R6, R7 atomicity | ✅ Done | #133 (`fix/gateway-hardening`): `IEventPublisher.WriteAsync` commits state + events together (lock taken before the transaction; nesting rejected); fail is a conditional update; turn end expires approvals in the same batch. |
| R4 context loading | ✅ Done | #133 (`fix/gateway-hardening`): `ContextBuilder.BuildForThreadAsync` reads the newest 50 turns, doubling only while all fit; equivalence-tested on a 130-turn thread. |
| R15 classifier | ✅ Done | #133 (`fix/gateway-hardening`): a reply naming more than one risk level is ambiguous → High. |
| R16 shared visibility | ✅ Done | #133 (`fix/gateway-hardening`): accepted by design (tech-debt register); `approvals.decided_by_user` records the user (or `admin_key`), shown in the UI card and trace. |
| #6 fail messages | ✅ Done | #133 (`fix/gateway-hardening`): `FailReasons.Describe` in the SSE `error` event and the CLI. |
| Found during the work | ✅ Fixed | UI `LastSeen`/`RelativeTime` didn't re-render when the server clock arrived (flaky `agents.test.tsx`), fixed with `useServerNow()`. A gauge test's global `MeterListener` raced other tests' disposal; now scoped to its own meter. |
| R17 sleeps and wall clock | ✅ Done | #134 (`test/deterministic-and-focused`): no fixed `Task.Delay` waits remain (one deliberate 30 ms overlap in a race test); `WaitForLongPollAsync` (via `KeyedSignal.Waiters`), `WaitForStreamedTextAsync`, `EventuallyAsync`; CLI fakes use a `TaskCompletionSource`; fakes use `TestClock.Now`. Gateway suite green on repeated runs, about 28 s (was 30 s). |
| R18 large test files | ✅ Done | #134 (`test/deterministic-and-focused`): `ClientApiTests.cs` split one class per file; `AuthTests`, `ApprovalTests`, `CliTests` are partial classes split by theme (pure moves, test counts unchanged). |
| Focused unit tests | ✅ Done | `InfrastructureTests` (cursor, turn buffers, signals, exception middleware, retention, approval race), plus `ThreadEventBusTests`, `ConfigurationValidationTests`, `MessageTurnsTests`, `EventAtomicityTests`, `ContextPagingTests` from earlier PRs. |
| R20 stream lifecycle | ✅ Done | #135 (`refactor/ui-structure-and-e2e`): a generation counter makes stale session checks and backoff timers no-ops. New tests reproduced two real bugs on the old code: a stale "signed out" answer could stop a re-opened stream, and a stale backoff timer could open a second `EventSource`. `LiveStore.reset()` on sign-out cancels the pending frame. |
| R19 UI structure | ✅ Done | #135 (`refactor/ui-structure-and-e2e`): `ThreadView` 469 → ~210 lines (header, banners, scroll/approvals/signals hooks, one `useUpdateThread`); `AgentDetailPage` 503 → 94; `AuditPage` 482 → 93; `components/ui.tsx` → `components/ui/` (index keeps imports). All query keys come from `queryKeys.ts`; two `exhaustive-deps` suppressions removed. |
| R21 e2e | ✅ Done | #135 (`refactor/ui-structure-and-e2e`). **Correction:** the original review understated the existing suite, which already covered approvals (approve/deny/two tabs/expiry), agents and tokens, audit and trace, a revoked session, reload mid-stream and offline/online. Added the critical-risk second confirmation and an account spec (wrong current password, other sessions end, sign in with the new password). Replaced `waitForTimeout` with waiting for `navigator.onLine`. Chromium: 16 passed. |
