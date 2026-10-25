# SharpClaw V3 — Phase 3.1

A personal multi-agent system. Phase 3.1 proves the architecture end to end with the smallest
possible system:

> CLI → gateway → agent → gateway → OpenRouter → back to the CLI, streamed, with the agent
> network-isolated, every LLM call audited, a daily spend cap enforced, and telemetry flowing to
> Grafana.

The **gateway** is the single trust boundary. Agents are untrusted workloads: they hold no
provider keys, have no direct internet access, and reach the world only through the gateway.

See [docs/sharpclaw-3.1-implementation-spec.md](docs/sharpclaw-3.1-implementation-spec.md) for the
full specification.

## Projects

| Project | Purpose |
|---|---|
| `src/SharpClaw.Contracts` | DTOs, error shapes and constants shared by all projects |
| `src/SharpClaw.Gateway` | ASP.NET gateway: two listeners, EF Core/SQLite, providers, telemetry |
| `src/SharpClaw.Agent` | The agent loop (console app, one per Docker container) |
| `src/SharpClaw.Cli` | Admin and chat client (assembly name `sharpclaw`) |
| `tests/SharpClaw.Gateway.Tests` | Gateway integration/unit tests (real Kestrel, fake provider, fake clock) |
| `tests/SharpClaw.Agent.Tests` | Agent loop tests against a fake gateway |
| `tests/SharpClaw.Cli.Tests` | CLI argument parsing and chat-ordering tests |

## Build & test

```bash
dotnet build
dotnet test
```

The gateway tests start the real gateway on two ephemeral localhost ports, back it with a
temporary SQLite file and the `FakeProvider`, and drive time with a `FakeTimeProvider` — no
secrets or network required.

## Architecture overview

- **Two listeners, one process.** The client API (default port 8080) and the agent API (default
  port 8081) are separated by the **local port the connection arrived on**, never the `Host`
  header. A request to the wrong listener returns `404`.
- **Auth.** The client API uses an admin key (`Authorization: Bearer <admin key>`, constant-time
  compared). The agent API uses per-agent tokens (`sc_…`); only a SHA-256 hash and an 8-char
  prefix are stored, and the full value is shown exactly once at creation.
- **Chat completions.** `POST /v1/chat/completions` is OpenAI-compatible and streaming only. The
  gateway always uses the agent's configured model, enforces the daily budget before streaming,
  records every call in `llm_calls`, and tees content deltas to the client's SSE stream.
- **Messaging.** Clients post user messages to a thread; agents claim them via a long-poll inbox
  (atomic single-statement claim), answer via the chat endpoint, and post a reply. Live output
  reaches clients over `GET /api/threads/{id}/stream` (SSE, no replay).
- **Budgets.** Each agent has a daily USD cap. Spend is the sum of `llm_calls.cost_usd` since the
  start of the current UTC day; exceeding it returns `429` with `Retry-After` and records a
  `rejected` call. Budgets reset at UTC midnight.

## Configuration

Gateway configuration comes from `appsettings.json` with environment-variable overrides (standard
ASP.NET `__` syntax).

| Key | Default | Notes |
|---|---|---|
| `Listeners:ClientPort` | `8080` | Client API |
| `Listeners:AgentPort` | `8081` | Agent API; never published to the host |
| `Database:Path` | `/data/sharpclaw.db` | SQLite file (WAL, `busy_timeout=5000`) |
| `Auth:AdminKey` | (required) | `Auth__AdminKey`; the gateway refuses to start if empty |
| `Provider:Type` | `OpenRouter` | `OpenRouter` or `Fake` |
| `OpenRouter:BaseUrl` | `https://openrouter.ai/api/v1` | |
| `OpenRouter:ApiKey` | (required for OpenRouter) | `OpenRouter__ApiKey` |
| `Provider:ConnectTimeoutSeconds` | `10` | |
| `Provider:IdleTimeoutSeconds` | `60` | Max gap between streamed chunks |
| `Fake:ScriptPath` | (none) | Optional JSON script for the fake provider |
| `Fake:CostPerTokenUsd` | `0.000001` | Default per-token cost |
| `Inbox:MaxWaitSeconds` | `60` | Upper clamp for long-poll `wait` |
| `Inbox:ClaimTimeoutSeconds` | `300` | Stale claims return to `pending` |
| `Messages:MaxContentChars` | `32000` | |

Telemetry uses the standard `OTEL_*` variables (see **Telemetry** below).

**Agent** (environment): `GATEWAY_URL` (required), `AGENT_TOKEN` (required), `SOUL_PATH`
(`/config/soul.md`), `WORKSPACE_PATH` (`/workspace`, unused in 3.1), `LONG_POLL_SECONDS` (`30`),
`LOG_LEVEL` (`Information`).

**CLI** (environment): `SHARPCLAW_URL` (default `http://localhost:8080`) and
`SHARPCLAW_ADMIN_KEY` (required).

## Bootstrap (Docker)

1. Copy the example env and set values:
   ```bash
   cp .env.example docker/.env
   # set ADMIN_KEY, and either OPENROUTER_API_KEY or PROVIDER_TYPE=Fake, plus the OTLP endpoints
   ```
2. Start the gateway:
   ```bash
   cd docker && docker compose up -d gateway
   ```
3. Create the first agent and copy its token into `docker/.env` as `AGENT_ASSISTANT_TOKEN`:
   ```bash
   export SHARPCLAW_URL=http://localhost:8080 SHARPCLAW_ADMIN_KEY=<your admin key>
   dotnet run --project src/SharpClaw.Cli -- agents create assistant --model <id> --daily-budget 1
   ```
4. Start the agent:
   ```bash
   cd docker && docker compose up -d agent-assistant
   ```
5. Chat:
   ```bash
   dotnet run --project src/SharpClaw.Cli -- chat assistant
   ```

Containers are started and stopped manually. The gateway never touches the Docker socket. Only the
client port is published (to `127.0.0.1:8080`); the agent port 8081 is never published.

## Running locally without Docker (fake provider)

```bash
Auth__AdminKey=dev Provider__Type=Fake Database__Path=/tmp/sharpclaw.db \
  Listeners__ClientPort=8080 Listeners__AgentPort=8081 \
  dotnet run --project src/SharpClaw.Gateway

export SHARPCLAW_URL=http://localhost:8080 SHARPCLAW_ADMIN_KEY=dev
dotnet run --project src/SharpClaw.Cli -- agents create assistant --model test/model --daily-budget 1
# copy the token, then:
GATEWAY_URL=http://localhost:8081 AGENT_TOKEN=<token> dotnet run --project src/SharpClaw.Agent &
dotnet run --project src/SharpClaw.Cli -- ask assistant "hello"
```

## CLI

| Command | Behaviour |
|---|---|
| `sharpclaw agents create <name> --model <id> --daily-budget <usd>` | Creates the agent and its first token (shown once) |
| `sharpclaw agents list` / `agents show <name>` | List or detail incl. today's spend and remaining budget |
| `sharpclaw agents set <name> [--model …] [--daily-budget …] [--enabled true\|false]` | Patch an agent |
| `sharpclaw tokens issue <agent> [--expires <utc>]` | Issue a token (shown once) |
| `sharpclaw tokens list <agent>` / `tokens revoke <agent> <tokenId>` | Token metadata / revoke |
| `sharpclaw chat <agent> [--thread <id>]` | Interactive chat; `/exit` quits |
| `sharpclaw ask <agent> "<text>"` | One-shot chat |
| `sharpclaw audit [--agent <name>] [--limit N]` | Recent LLM calls |

The chat client opens the SSE stream **before** posting a message (streams have no replay).

## Telemetry

The gateway is the authoritative telemetry source (agents only write JSON logs to stdout; they
have no network egress). OpenTelemetry exports metrics to VictoriaMetrics and logs to Loki over
OTLP/HTTP-protobuf. Exporters are registered only when their endpoint variable is set; with none
set the gateway runs with JSON console logs only, and exporter failures never affect request
handling.

```
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://<host>:8428/opentelemetry/v1/metrics
OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://<host>:3100/otlp/v1/logs
OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=cumulative
OTEL_SERVICE_NAME=sharpclaw-gateway
OTEL_RESOURCE_ATTRIBUTES=deployment.environment=home
```

- **Smoke test:** `scripts/telemetry-smoke-test` emits one counter and one log with a
  `correlation_id` attribute, to discover the exact metric name VictoriaMetrics lands it under and
  confirm Loki accepts `correlation_id` as structured metadata. See
  [docs/telemetry-notes.md](docs/telemetry-notes.md).
- **Dashboard:** `docker/grafana/sharpclaw-dashboard.json` is an importable starter dashboard
  (LLM calls by status, cost vs budget, duration/TTFT percentiles, tokens, inbox depth, last-seen
  age, budget rejections, auth failures, and a Loki logs panel with a `correlation_id` variable).
  Panel queries use the actual landed names (dotted OTel names via `{__name__="…"}`).

## Isolation verification

With the stack running:

```bash
cd docker && bash ../scripts/verify-isolation.sh
```

It checks that the agent network cannot reach the public internet but can reach the gateway agent
listener, that port 8081 is not published to the host, that each route group returns `404` on the
wrong port, and that the agent container's root filesystem and `/config` are read-only.

## Verification results

Verified in this environment with the **fake provider** (no secrets or external network):

- All automated tests pass: `dotnet test` → gateway 37, agent 10, CLI 3.
- End-to-end locally: `sharpclaw ask assistant "…"` streamed `Fake reply to: …`, and the call
  appeared in `sharpclaw audit` with model, tokens, cost, latency and status `ok`.
- End-to-end in Docker Compose: gateway healthy via `--healthcheck`, agent container running,
  `sharpclaw ask` streamed a reply through the containerised agent.
- `scripts/verify-isolation.sh` passed all checks against the running stack.

Verified against the live environment (2026-10-25):

- **Telemetry:** the smoke test and gateway metrics land in VictoriaMetrics, and logs land in Loki
  with `correlation_id` as structured metadata. One message was traced end to end in Loki by
  `correlation_id`. Landed metric names and findings are in
  [docs/telemetry-notes.md](docs/telemetry-notes.md); the dashboard uses those names.
- **Real OpenRouter** (`openai/gpt-4o-mini`, Docker Compose stack): `sharpclaw ask` streamed real
  replies through the isolated agent. Each call was audited with tokens, cost (e.g. 68/28 tokens,
  $0.000027, 1235 ms) and status `ok`.
- **Budget, live:** lowering the cap below today's spend gave `429` → `fail(budget_exceeded)` →
  `Error: budget_exceeded` in the CLI, plus a `rejected` audit row.
- `scripts/verify-isolation.sh` passed all checks on the OpenRouter-backed stack.
- No prompt or reply text found in the gateway logs in Loki.

Not yet done: importing `docker/grafana/sharpclaw-dashboard.json` into Grafana (its API needs
credentials). Import it via *Dashboards → New → Import*; the queries were checked directly against
VictoriaMetrics.

## Known gaps and tech debt

Accepted Phase 3.1 limitations and known code-level debt are tracked in
[docs/tech-debt.md](docs/tech-debt.md).
