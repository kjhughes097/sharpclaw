# SharpClaw V3

A personal multi-agent system. Phase 3.1 proved the architecture end to end with the smallest
possible system:

> CLI → gateway → agent → gateway → OpenRouter → back to the CLI, streamed, with the agent
> network-isolated, every LLM call audited, a daily spend cap enforced, and telemetry flowing to
> Grafana.

The **gateway** is the single trust boundary. Agents are untrusted workloads: they hold no
provider keys, have no direct internet access, and reach the world only through the gateway.

Phase 3.2 adds **agent tools with policy and approval**: the agent scores each tool call's risk
from its arguments and asks the user before running anything above its allowed risk level, and
every tool call is audited (see [Agent tools](#agent-tools-phase-32)).

The **pre-3.3 foundations** prepare for the web UI: browser login with session cookies, tool
exchanges kept in thread history, per-agent context windows, replayable SSE events, and an
approval queue that works with nobody watching.

Phase 3.3 adds the **web UI**: login, streamed chat, inline tool activity and approvals,
agents and tokens, audit and per-thread traces (see [Using the web UI](#using-the-web-ui)).

How the system fits together today: [docs/architecture.md](docs/architecture.md).
Specifications: [Phase 3.1](docs/sharpclaw-3.1-implementation-spec.md),
[Phase 3.2](docs/sharpclaw-3.2-implementation-spec.md),
[pre-3.3 foundations](docs/sharpclaw-pre-3.3-foundations-spec.md),
[Phase 3.3](docs/sharpclaw-3.3-implementation-spec.md). Roadmap: [docs/sharpclaw-v3-plan.md](docs/sharpclaw-v3-plan.md).

## Projects

| Project | Purpose |
|---|---|
| `src/SharpClaw.Contracts` | DTOs, error shapes and constants shared by all projects |
| `src/SharpClaw.Gateway` | ASP.NET gateway: two listeners, EF Core/SQLite, providers, telemetry |
| `src/SharpClaw.Agent` | The agent loop (console app, one per Docker container) |
| `src/SharpClaw.Cli` | Admin and chat client (assembly name `sharpclaw`) |
| `ui/` | Web UI (React + TypeScript + Vite), served by the gateway; see [docs/ui.md](docs/ui.md) |
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
secrets or network required. The web UI has its own checks (`cd ui && npm ci && npm run lint &&
npm run typecheck && npm test && npm run check:api`); see [docs/ui.md](docs/ui.md).
GitHub Actions ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs the .NET build and
tests, the UI checks and the Playwright suite (Chromium) on every pull request.

## Architecture overview

- **Two listeners, one process.** The client API (default port 8080) and the agent API (default
  port 8081) are separated by the **local port the connection arrived on**, never the `Host`
  header. A request to the wrong listener returns `404`.
- **Auth.** The client API accepts the admin key (`Authorization: Bearer <admin key>`,
  constant-time compared; used by the CLI and scripts) or a browser session cookie. The agent API
  uses per-agent tokens (`sc_…`); only a SHA-256 hash and an 8-char prefix are stored, and the full
  value is shown exactly once at creation.
- **Browser login.** `POST /api/auth/login` with a username and password (PBKDF2-SHA256, 600,000
  iterations; at least 12 characters) sets an `HttpOnly`, `SameSite=Strict` session cookie and
  returns a CSRF token. Cookie-authenticated `POST`/`PUT`/`PATCH`/`DELETE` need a same-origin
  `Origin` (or `Referer`) and `X-CSRF-Token`; otherwise `403 csrf_failed`. Failed logins are
  rate limited per username and per IP (`429 rate_limited`). Sessions slide (7 days idle, 30 days
  absolute). `/api/users*` accepts only the admin key. TLS is terminated by a reverse proxy; set
  `Auth:ForwardedHeaders:*` so the gateway trusts its forwarded headers, and `Auth:AllowedOrigins`
  if the browser's origin differs from what the gateway sees. No CORS. Create users with
  `sharpclaw users create <name>`.
- **Chat completions.** `POST /v1/chat/completions` is OpenAI-compatible and streaming only. The
  gateway always uses the agent's configured model, enforces the daily budget before streaming,
  records every call in `llm_calls`, and tees content deltas to the client's SSE stream.
- **Messaging.** Clients post user messages to a thread; agents claim them via a long-poll inbox
  (atomic single-statement claim), answer via the chat endpoint, and post a reply. Live output
  reaches clients over SSE: `GET /api/threads/{id}/stream` for one thread, `GET /api/events` for
  all threads.
- **Event replay.** Durable events (`message.created`, `message.completed`, `error`,
  `tool.requested`, `approval.requested`, `approval.decided`) are stored in `thread_events` and
  carry an SSE `id:`. Reconnecting with `Last-Event-ID` (or `?lastEventId=`) replays what was
  missed, then a `ready` event marks the baseline and a `snapshot` carries any reply streamed so
  far. Deltas are not stored. Events are kept for `Events:RetentionDays`; a last id older than that
  gets `reset` (reload history).
- **Tools (3.2).** The agent owns its tool loop, risk scoring and approval decisions; its container
  is the sandbox. The gateway forwards tools to the model, records every tool call the model
  requests, brokers approvals to the CLI, and caps LLM calls per message.
- **Budgets.** Each agent has a daily USD cap. Spend is the sum of `llm_calls.cost_usd` since the
  start of the current UTC day; exceeding it returns `429` with `Retry-After` and records a
  `rejected` call. Budgets reset at UTC midnight.
- **Context window.** The agent gets its history from `GET /agent/threads/{id}/context`, which
  fits whole turns, newest first, into the agent's window (`contextWindowTokens`, default
  `Context:DefaultWindowTokens`) minus the agent's reserve for its soul and tool schemas and
  `Context:ReplyReserveTokens`. Older turns are dropped and replaced by a note; the thread's
  `context.droppedTurns` shows it. Tokens are estimated as characters / 4. A turn that alone
  doesn't fit, a chat request over the window, or a provider context-length error fails with
  `context_too_large`.

## Configuration

Gateway configuration comes from `appsettings.json` with environment-variable overrides (standard
ASP.NET `__` syntax).

| Key | Default | Notes |
|---|---|---|
| `Listeners:ClientPort` | `8080` | Client API |
| `Listeners:AgentPort` | `8081` | Agent API; never published to the host |
| `Database:Path` | `/data/sharpclaw.db` | SQLite file (WAL, `busy_timeout=5000`) |
| `Auth:AdminKey` | (required) | `Auth__AdminKey`; the gateway refuses to start if empty |
| `Auth:Session:CookieName` | `sharpclaw_session` | |
| `Auth:Session:CookieSecure` | `Auto` | `Auto` (Secure when the request is HTTPS), `Always`, `Never` |
| `Auth:Session:IdleTimeoutHours` / `AbsoluteTimeoutDays` | `168` / `30` | Session lifetime |
| `Auth:Password:Pbkdf2Iterations` | `600000` | Never lower; hashes are upgraded on login |
| `Auth:Login:MaxFailures` / `WindowMinutes` | `5` / `15` | Login rate limit (per username and per IP) |
| `Auth:AllowedOrigins` | empty | Extra browser origins accepted (reverse proxy); not CORS |
| `Auth:ForwardedHeaders:Enabled` / `KnownProxies` | `false` / empty | Trust `X-Forwarded-*` from these proxies only |
| `Provider:Type` | `OpenRouter` | `OpenRouter` or `Fake` |
| `OpenRouter:BaseUrl` | `https://openrouter.ai/api/v1` | |
| `OpenRouter:ApiKey` | (required for OpenRouter) | `OpenRouter__ApiKey` |
| `OpenRouter:ManagementKey` | empty | `OpenRouter__ManagementKey`; a [management key](https://openrouter.ai/docs/guides/overview/auth/management-api-keys), used only to show the remaining credits in the UI. Empty: the credits card says "not configured" |
| `Credits:RefreshHours` / `LowThresholdUsd` | `24` / `5` | How often the credits balance is fetched (hourly while failing); below the threshold the UI shows it in red |
| `Models:Allowed` | empty | Model ids a thread may switch to in the UI (e.g. `Models__Allowed__0=openai/gpt-4o-mini`); the agent's own model is always available. Empty: no per-thread model picker |
| `Provider:ConnectTimeoutSeconds` | `10` | |
| `Provider:IdleTimeoutSeconds` | `60` | Max gap between streamed chunks |
| `Fake:ScriptPath` | (none) | Optional JSON script for the fake provider |
| `Fake:CostPerTokenUsd` | `0.000001` | Default per-token cost |
| `Inbox:MaxWaitSeconds` | `60` | Upper clamp for long-poll `wait` |
| `Inbox:ClaimTimeoutSeconds` | `300` | Stale claims return to `pending` |
| `Messages:MaxContentChars` | `32000` | |
| `Approvals:TimeoutSeconds` | `300` | Pending approvals expire (count as denied) after this |
| `Approvals:SweepIntervalSeconds` | `10` | How often overdue approvals are expired |
| `Chat:MaxLlmCallsPerMessage` | `20` | Loop backstop: LLM calls allowed per user message |
| `Events:RetentionDays` | `14` | Durable SSE events kept for replay |
| `Events:SubscriberBufferSize` | `1000` | Live events buffered per SSE connection; a client that falls further behind is disconnected and catches up by reconnecting with `Last-Event-ID` |
| `Tools:MaxStoredResultChars` | `4000` | Stored tool results are cut to this |
| `Tools:MaxStoredArgumentStringChars` | `500` | Each string in stored tool arguments is cut to this |
| `Tools:MaxExchangesPerReply` | `50` | Tool rounds accepted with one reply |
| `Context:DefaultWindowTokens` | `32000` | Used when the agent has no `contextWindowTokens` |
| `Context:ReplyReserveTokens` | `2000` | Kept free for the model's reply |
| `Context:CharsPerToken` | `4` | Estimation heuristic |
| `Context:MessageOverheadTokens` | `4` | Added per message |

Telemetry uses the standard `OTEL_*` variables (see **Telemetry** below).

**Agent** (environment): `GATEWAY_URL` (required), `AGENT_TOKEN` (required), `SOUL_PATH`
(`/config/soul.md`), `AGENT_CONFIG_PATH` (`/config/agent.json`, tool policy), `WORKSPACE_PATH`
(`/workspace`), `LONG_POLL_SECONDS` (`30`), `LOG_LEVEL` (`Information`).

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
3. Create the first agent and copy its token into `docker/.env` as `AGENT_ASSISTANT_TOKEN`
   (also set `AGENT_UID`/`AGENT_GID` to the owner of `docker/agents/assistant/workspace` if it
   isn't 1000:1000, so workspace tools can write):
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
client port is published (to `${CLIENT_BIND:-127.0.0.1}:8080`); the agent port 8081 is never published.

## Using the web UI

The gateway image builds and serves the web UI on the client port: open `http://<host>:8080`.

- **Reach it from your LAN:** set `CLIENT_BIND` in `.env` to the host's LAN address (or `0.0.0.0`)
  and recreate the gateway. It defaults to `127.0.0.1` (this machine only).
- **First user:** `sharpclaw users create <name>` (user management stays in the CLI), then sign in.
- **Plain HTTP:** your password and session cookie cross the LAN unencrypted. That's accepted for a
  home network; **never port-forward 8080** from your router. A local TLS reverse proxy is the
  future fix (see [docs/tech-debt.md](docs/tech-debt.md)).
- Each browser tab holds one live connection; over plain HTTP about four tabs per browser is the
  practical limit.

UI development, the event stream and the CSP are described in [docs/ui.md](docs/ui.md).

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

To get a `sharpclaw` command on your PATH, publish the CLI and add a small launcher (assumes
`~/.local/bin` is on your PATH, as it is by default on Ubuntu):

```bash
dotnet publish src/SharpClaw.Cli -c Release -o ~/.local/share/sharpclaw-cli
cat > ~/.local/bin/sharpclaw <<'EOF'
#!/usr/bin/env bash
SHARPCLAW_REPO="${SHARPCLAW_REPO:-$HOME/projects/sharpclaw}"
export SHARPCLAW_URL="${SHARPCLAW_URL:-http://localhost:8080}"
# Fall back to the admin key in the repo's .env when SHARPCLAW_ADMIN_KEY isn't set.
if [ -z "${SHARPCLAW_ADMIN_KEY:-}" ] && [ -f "$SHARPCLAW_REPO/.env" ]; then
  SHARPCLAW_ADMIN_KEY="$(grep -E '^ADMIN_KEY=' "$SHARPCLAW_REPO/.env" | head -n1 | cut -d= -f2-)"
  export SHARPCLAW_ADMIN_KEY
fi
exec dotnet "$HOME/.local/share/sharpclaw-cli/sharpclaw.dll" "$@"
EOF
chmod +x ~/.local/bin/sharpclaw
```

Re-run the `dotnet publish` line after pulling CLI changes. Without installing, use
`dotnet run --project src/SharpClaw.Cli -- <command>` instead of `sharpclaw <command>`.

| Command | Behaviour |
|---|---|
| `sharpclaw agents create <name> --model <id> --daily-budget <usd> [--context-window <tokens>]` | Creates the agent and its first token (shown once) |
| `sharpclaw agents list` / `agents show <name>` | List or detail incl. today's spend and remaining budget |
| `sharpclaw agents set <name> [--model …] [--daily-budget …] [--enabled true\|false] [--context-window …]` | Patch an agent |
| `sharpclaw tokens issue <agent> [--expires <utc>]` | Issue a token (shown once) |
| `sharpclaw tokens list <agent>` / `tokens revoke <agent> <tokenId>` | Token metadata / revoke |
| `sharpclaw chat <agent> [--thread <id>]` | Interactive chat; `/exit` quits |
| `sharpclaw ask <agent> "<text>" [--unattended]` | One-shot chat; `--unattended` denies any approval at once |
| `sharpclaw approvals list [--status pending] [--agent <name>]` | The approval queue |
| `sharpclaw approvals approve <id>` / `approvals deny <id>` | Answer a pending approval from any terminal |
| `sharpclaw users create <name> [--password-stdin]` | Create a browser user (prompts twice without echo) |
| `sharpclaw users list` | Users and whether they are disabled |
| `sharpclaw users set-password <name> [--password-stdin]` | Reset a password; signs out their sessions |
| `sharpclaw users disable <name>` / `users enable <name>` | Disabling signs out their sessions |
| `sharpclaw audit [--agent <name>] [--limit N]` | Recent LLM calls |
| `sharpclaw audit tools [--agent <name>] [--limit N]` | Recent tool calls: status, decision, risk, taint |

The chat client opens the SSE stream before posting a message. If the stream drops it reconnects
(1 s backoff doubling to 10 s, giving up after 60 s) and resumes from the last event it handled,
printing each character once. During
`chat` and `ask` it prints a `[tool] <name>` line for each tool call and, when the agent needs
approval, shows the request and asks `Allow? [y/N]` (anything but `y`/`yes` denies).

## Agent tools (Phase 3.2)

Each agent reads its tool policy once at startup from a read-only file (`/config/agent.json`,
e.g. [docker/agents/assistant/config/agent.json](docker/agents/assistant/config/agent.json)):

```json
{
  "allowedRiskLevel": "low",
  "smartMode": true,
  "maxToolIterations": 10,
  "tools": ["workspace_list", "workspace_read", "workspace_write", "workspace_delete"]
}
```

A missing file runs the agent without tools; an invalid one stops it (exit code 1).

| Tool | Risk |
|---|---|
| `workspace_list` | low |
| `workspace_read` | low (content is untrusted: later calls in the turn are scored one level higher) |
| `workspace_write` | new file low, overwrite medium; scripts, executables and dotfiles uncertain |
| `workspace_delete` | high |

How a call is decided (all in the agent's `ToolDispatcher`):

1. **Hardline rules** block it outright: absolute paths, paths outside the workspace, symbolic
   links, size caps. Blocked calls are never offered for approval.
2. **Risk** comes from the arguments. *Uncertain* calls are classified by an extra LLM call when
   `smartMode` is on (only the tool name and a content-free summary are sent), otherwise treated
   as high. After untrusted content has been read in the turn, risk goes up one level.
3. **Above `allowedRiskLevel`**, the agent asks the user through the gateway. The approval waits
   in a queue (`GET /api/approvals`, `sharpclaw approvals list`) whether or not anyone is
   watching, and expires after 300 s; a message posted `unattended` has its approvals denied at
   once. When a turn ends (reply, failure, claim timeout) its pending approvals expire. Denials
   go back to the model as a tool error so it can adapt.
4. Every call is **audited**: the gateway records what the model requested, the agent reports the
   outcome (`sharpclaw audit tools`). Arguments are truncated in the database and never logged.
5. **Thread history keeps the tool exchanges.** With its reply the agent posts each round's tool
   calls and results; they are stored as `tool_request` / `tool_result` rows (arguments and results
   truncated, see `Tools:*`), shown by `GET …/messages?includeTools=true`, and replayed to the
   model on later turns, except results of tools that return untrusted content (`workspace_read`),
   which are replaced by a stub so injected text can't persist in the thread.

No tool executes code or shell commands. The agent container (no network, read-only root
filesystem, only its own `/workspace`) contains anything a tool does.

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
- **Dashboard:** `docker/grafana/sharpclaw-dashboard.json` (imported into Grafana as uid
  `sharpclaw-gateway`; re-import via *Dashboards → New → Import* or the API after changes)
  (LLM calls by status, cost vs budget, duration/TTFT percentiles, tokens, inbox depth, last-seen
  age, budget rejections, tool calls, approvals (rate, pending, decision time), unreported tool
  calls, context truncations/rejections, logins and auth failures by method, and a Loki logs panel
  with a `correlation_id` variable).
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

Dated manual and live verification (Phase 3.1, Phase 3.2, pre-3.3 foundations) is recorded in
[docs/verification-log.md](docs/verification-log.md).

## Known gaps and tech debt

Accepted design limits (Phase 3.1, 3.2 and the pre-3.3 foundations) and known code-level debt are
tracked in [docs/tech-debt.md](docs/tech-debt.md).
