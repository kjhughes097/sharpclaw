# SharpClaw V3 — Project Guidelines

Personal multi-agent system. The **gateway is the single trust boundary**; agents are untrusted,
network-isolated workloads that hold no provider keys and reach the world only via the gateway.

- **Start here:** [docs/architecture.md](../docs/architecture.md) describes the system as it is
  now (components, listeners, auth, endpoints, events, tables, invariants). Read it before the specs.
- Source of truth for behaviour: the phase specs, applied in order, with later ones winning where
  they differ: [3.1](../docs/sharpclaw-3.1-implementation-spec.md) →
  [3.2](../docs/sharpclaw-3.2-implementation-spec.md) →
  [pre-3.3 foundations](../docs/sharpclaw-pre-3.3-foundations-spec.md) →
  [3.3 (web UI)](../docs/sharpclaw-3.3-implementation-spec.md) →
  [3.3.1 (tabbed UI redesign)](../docs/sharpclaw-3.3.1-ui-redesign-spec.md). Build only what they describe;
  anything still in an "Out of scope" list (agent-to-agent, retries, non-streaming completions,
  Postgres, Docker socket access, network or code-execution tools, stop/cancel turn, message
  editing, attachments, thread deletion, search, multi-user features, push/PWA, TLS, frontend
  telemetry…) must not be added. If the spec is ambiguous, ask rather than guess.
- Setup, configuration and CLI usage: [README.md](../README.md). Web UI: [docs/ui.md](../docs/ui.md).
  Telemetry backends and landed metric names: [docs/telemetry-notes.md](../docs/telemetry-notes.md).

## Engineering Principles

- **Language:** C# on .NET 10 is the default for all code. Bash is fine for scripts
  (e.g. `scripts/verify-isolation.sh`); don't introduce other languages without asking.
- **Small, testable increments:** get something working end to end first, commit, then refine or
  refactor. Prefer several small, green steps over one large change.
- **Tests are not optional:** every change adds or updates unit tests. Add integration tests
  (e.g. via `GatewayFixture`) when behaviour crosses HTTP, the database, streaming or listeners.
- **Simple and readable over clever:** write the most straightforward code that works. Use
  comments generously to explain *why* (intent, constraints, non-obvious decisions), not to
  restate what the code does.
- **Minimal indirection:** don't add interfaces, layers, factories or abstractions unless they earn
  their place — a real second implementation, or a seam needed for testing (as with
  `IChatProvider`, `IGatewayClient`, `ICliGateway`). If fewer layers will do, use fewer.
- **SOLID, pragmatically:** keep classes focused on one responsibility and depend on abstractions
  at real boundaries (providers, HTTP, filesystem, time), but don't let SOLID become an excuse for
  extra layers — simplicity wins when they conflict.
- **Watch for creeping complexity:** when a change makes code noticeably harder to follow, or you
  notice it in existing code (long methods, many parameters, duplicated logic, growing files,
  needless layers), **call it out** to the user and propose a simplification or refactor. Don't
  silently refactor unrelated code in the same change.
- **Tech debt register:** record known and accepted debt in
  [docs/tech-debt.md](../docs/tech-debt.md): what it is, why it was accepted, and when to fix it.
  Add an entry when you knowingly take a shortcut; remove it in the PR that pays it down. Check it
  before working in an area — the fix may be due.

## Build and Test

```bash
dotnet build                 # solution is SharpClaw.slnx
dotnet test                  # all suites; no secrets or network needed (FakeProvider)
dotnet build -c Release      # warnings are errors in Release — keep it at 0 warnings
```

Run a focused suite with `dotnet test tests/SharpClaw.Gateway.Tests --filter "FullyQualifiedName~ClassName"`.

Web UI (in `ui/`, Node 24):

```bash
npm ci
npm run lint && npm run typecheck && npm test   # ESLint, strict tsc, Vitest + MSW
npm run check:api                                # generated API types match the OpenAPI snapshot
npm run build                                    # typecheck + Vite build + dist/CSP checks
npm run test:e2e -- --project=chromium           # Playwright; starts an isolated stack via e2e/stack.sh
```

CI (`.github/workflows/ci.yml`) runs all of the above on every pull request; keep it green.

**Contract changes** (DTOs, endpoints, SSE events) are snapshot-guarded. After changing one:
`UPDATE_OPENAPI=1 dotnet test tests/SharpClaw.Gateway.Tests --filter "FullyQualifiedName~SnapshotTests"`
to rewrite `contracts/openapi/sharpclaw-client.v1.json` and `contracts/events/`, then
`npm run gen:api` in `ui/`, review the diff, and commit all three together.

## Architecture

- `src/SharpClaw.Contracts` — DTOs, `ErrorCodes`, `Roles`, `MessageStatus`, `LlmCallStatus`,
  `SharpClawJson.Options`. Shared by every project; put wire shapes here.
- `src/SharpClaw.Gateway` — one process, two Kestrel listeners. Route groups are separated by
  `HttpContext.Connection.LocalPort` (`ListenerFilter`), **never** the `Host` header:
  client `/api/*` (admin key or session cookie + CSRF, port 8080) vs agent `/agent/*` + `/v1/*`
  (agent token, port 8081). `/api/users*` is admin-key only.
  Wrong listener → `404`. `GatewayBuilder` calls plain extension methods: `AddSharpClawOptions()`
  (every options class is validated at startup), `AddSharpClawAuth/Providers/Services/Telemetry()`
  and `MapSharpClawEndpoints()` (`Endpoints/EndpointMapping.cs`, the one list of route groups).
- `src/SharpClaw.Agent` — console loop; HTTP and soul file behind `IGatewayClient` / `ISoulProvider`.
- `src/SharpClaw.Cli` — assembly `sharpclaw`; gateway access behind `ICliGateway`. Chat opens the
  SSE stream **before** posting (a fresh connect replays nothing) and resumes with `Last-Event-ID`.
- `ui/` — the web UI (React + TypeScript + Vite, TanStack Query, Radix, Tailwind), built into the
  gateway image and served same-origin from the client listener (`Infrastructure/UiHosting.cs`, strict
  CSP). Browser auth is a session cookie plus an `X-CSRF-Token` header on unsafe methods. Each tab
  opens exactly **one** SSE connection (`/api/events`) owned by `ui/src/api/stream.ts`. See
  [docs/ui.md](../docs/ui.md) and `.github/instructions/ui.instructions.md`.
- **Events:** a state change that emits durable client events goes through
  `IEventPublisher.WriteAsync(db, batch => …)`: the change and its `thread_events` rows commit in
  one transaction, then publish. Don't nest it, and don't open other transactions that emit events.
  `PublishDurableAsync` is only for an event with no state change. Only `delta` is ephemeral. Never put tool arguments
  or results in an event.

## Conventions

- **Time:** inject `TimeProvider`; never call `DateTime.UtcNow`/`DateTimeOffset.UtcNow` in product code.
- **Timestamps:** UTC `DateTime` only (a converter forces `Kind=Utc`). Do not use `DateTimeOffset`
  with SQLite. IDs are `Guid.CreateVersion7()`. Money is `double` USD.
- **Database:** tables and columns are snake_case (set in `SharpClawDbContext`). Raw SQL must use
  snake_case names. Schema changes need a new EF migration (`dotnet ef migrations add <Name>
  --project src/SharpClaw.Gateway --output-dir Data/Migrations`); `llm_calls` is append-only.
- **Agent tools:** every tool call goes through the agent's `ToolDispatcher` (risk → approval →
  execute → report). **Never add a tool that executes code or shell commands**, and never read
  agent policy from the writable workspace. See the 3.2 spec.
- **Errors:** always return the single shape via `ApiResults.*` (`{ "error": { code, message } }`).
  Foreign/unowned ids return `404`, not `403`.
- **JSON:** use `SharpClawJson.Options` (camelCase, string enums).
- **Security & privacy (hard rule):** never log or emit as a metric label any prompt/reply
  content, soul contents, tokens, keys or upstream response bodies. Tokens are stored only as
  SHA-256 hex + 8-char prefix; the value is returned once. Secrets come from env vars / `.env`
  (gitignored) — never commit them.
- **Logging:** put `agent`, `agent_id`, `thread_id`, `correlation_id` in the log scope via
  `LogScopes.Turn(...)` / `LogScopes.ForAgent(...)` (log attributes → Loki structured metadata), not
  resource attributes. The agent uses the same `correlation_id` key.
- **Turn state and ownership:** change a user turn's state only through `MessageTurns`, and check
  agent ownership with `TurnGuard` (never re-implement the "claimed message belongs to this agent" rule).
- **Metrics:** meter `SharpClaw.Gateway`; labels limited to agent, model, status, type, listener —
  never thread/message/correlation/token ids. Histograms are in seconds with explicit buckets.
  Landed names keep dots (no `_total`), so query them as `{__name__="sharpclaw.llm.calls"}`.
- **OTLP exporters:** set `Endpoint`/`Protocol` explicitly from the signal-specific env vars — the
  SDK does not apply `OTEL_EXPORTER_OTLP_*_ENDPOINT` by itself here.
- Commits include the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer.

## Git Workflow

- **Never commit directly to `main`.** All work happens on a branch, e.g. `feature/<short-name>`,
  `fix/<short-name>`, `docs/<short-name>`.
- Commit in small, green increments on the branch (build and tests passing).
- When the work is ready, push the branch and open a pull request into `main`
  (`gh pr create --base main`) with a summary of what changed and how it was verified.
  Don't merge it yourself unless asked.
