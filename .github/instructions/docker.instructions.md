---
description: "Use when editing SharpClaw Dockerfiles, docker compose, agent containers, network isolation, verify-isolation.sh, or the Grafana dashboard JSON."
applyTo: ["docker/**", "scripts/verify-isolation.sh"]
---
# SharpClaw Docker & Compose Guidelines

- **Isolation is the point.** `agent-*` services join only the `internal` network
  (`internal: true`, no egress), run `read_only: true` with `tmpfs: [/tmp]`, `cap_drop: [ALL]`,
  `no-new-privileges`, mount `/config` read-only. Never give an agent the `external` network,
  provider keys, OTLP endpoints, or the Docker socket.
- Publish only the client port, bound to loopback: `127.0.0.1:8080:8080`. Port 8081 must never be
  published.
- Images: multi-stage (`sdk` → `aspnet`/`runtime`), run as the built-in non-root `app` user (there
  is no `adduser` in the .NET images), no secrets baked in. The gateway image sets
  `ASPNETCORE_HTTP_PORTS=` because Kestrel ports are configured in code.
- The runtime images have no `curl`; the gateway healthcheck is `dotnet SharpClaw.Gateway.dll --healthcheck`.
- Secrets live in the repo-root `.env` (gitignored). Run compose from `docker/` with
  `docker compose --env-file ../.env …`. Never commit `.env` or tokens.
- New agents follow the `agent-ade` pattern: a `agent-<name>` service with its own
  `AGENT_<NAME>_TOKEN`, `docker/agents/<name>/config/{soul.md,agent.json}` (ro) and
  `docker/agents/<name>/workspace/` (contents gitignored, `.gitkeep` tracked).
- After changing compose or networking, bring the stack up and run
  `cd docker && bash ../scripts/verify-isolation.sh` — it must report all checks passed.
- Dashboard JSON (`docker/grafana/sharpclaw-dashboard.json`): select metrics with the landed dotted
  names, e.g. `{__name__="sharpclaw.llm.duration_bucket", agent=~"$agent"}`, and aggregate with
  `sum by`/`max by` (`service.instance.id` changes per restart). See
  [docs/telemetry-notes.md](../../docs/telemetry-notes.md).
