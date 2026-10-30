---
description: "Use when editing SharpClaw Dockerfiles, docker compose, agent containers, network isolation, verify-isolation.sh, or the Grafana dashboard JSON."
applyTo: ["docker/**", "scripts/verify-isolation.sh", "scripts/build-images.sh", "scripts/install.sh", "scripts/dev-stack.sh"]
---
# SharpClaw Docker & Compose Guidelines

- **Isolation is the point.** `agent-*` services join only the `internal` network
  (`internal: true`, no egress), run `read_only: true` with `tmpfs: [/tmp]`, `cap_drop: [ALL]`,
  `no-new-privileges`, mount `/config` read-only. Never give an agent the `external` network,
  provider keys, OTLP endpoints, or the Docker socket.
- Publish only the client port, bound to loopback by default:
  `${CLIENT_BIND:-127.0.0.1}:${CLIENT_PORT:-8080}:8080`. Port 8081 must never be published.
- Images: multi-stage (`sdk` → `aspnet`/`runtime`), run as the built-in non-root `app` user (there
  is no `adduser` in the .NET images), no secrets baked in. The gateway image sets
  `ASPNETCORE_HTTP_PORTS=` because Kestrel ports are configured in code.
- The runtime images have no `curl`; the gateway healthcheck is `dotnet SharpClaw.Gateway.dll --healthcheck`.
- **The repo builds; installs run.** Nothing personal lives in the repo. `scripts/build-images.sh`
  builds `sharpclaw-gateway`/`sharpclaw-agent` images; `scripts/install.sh [dir]` copies
  `docker/template/` (image-only `compose.yml`, `.env.example`, sample agents `assistant` and
  `coder`) into an install directory (default `~/sharpclaw`) without overwriting. The template
  compose must never use `build:` or repo-relative paths; the gateway and agents run as
  `${SHARPCLAW_UID}:${SHARPCLAW_GID}` and the database is the `./data` bind mount.
- Secrets live in the install's `.env` (mode 600). Never commit `.env` or tokens.
- New sample agents follow the `agent-assistant` pattern: an `agent-<name>` service using the
  `x-agent` anchor with its own `AGENT_<NAME>_TOKEN`, `agents/<name>/config/{soul.md,agent.json}`
  (ro) and `agents/<name>/workspace/` (created by the installer).
- Dev stack: `scripts/dev-stack.sh` builds `:dev` images and runs the template plus
  `docker/compose.dev.yml` (project `sharpclaw-dev`, port 8090) from gitignored `docker/.dev/`.
- After changing compose or networking, bring a stack up (e.g. `scripts/dev-stack.sh`, then
  `cd docker/.dev && docker compose up -d`) and run `bash ../../scripts/verify-isolation.sh` from
  that directory — it must report all checks passed.
- Dashboard JSON (`docker/grafana/sharpclaw-dashboard.json`): select metrics with the landed dotted
  names, e.g. `{__name__="sharpclaw.llm.duration_bucket", agent=~"$agent"}`, and aggregate with
  `sum by`/`max by` (`service.instance.id` changes per restart). See
  [docs/telemetry-notes.md](../../docs/telemetry-notes.md).
