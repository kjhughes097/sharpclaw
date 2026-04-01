# Blue-Green Deployment Strategy with Automatic Rollback

> **Status**: Design / Specification
> **Issue**: [#15](https://github.com/kjhughes097/sharpclaw/issues/15)

## 1. Overview

SharpClaw agents can modify their own source code. A blue-green deployment model lets SharpClaw apply those changes, rebuild, and switch over to the new version — with an automatic rollback path if the new version fails to start or misbehaves.

SharpClaw supports two deployment modes, and this design covers both:

| Mode | Stack | Managed by |
|------|-------|------------|
| **Docker Compose** | Containers (API + Web + PostgreSQL) | `docker compose` / RebuildHook |
| **Systemd services** | Native binaries + nginx + system PostgreSQL | `install-service-linux.sh` / systemd |

### Core Idea

The repository is checked out in **two side-by-side locations** on the host (called **Slot A** and **Slot B**). At any time one slot is *live* (serving traffic) and the other is *idle* (available for the next update). A lightweight **watchdog** monitors the newly deployed service; if it never becomes healthy the watchdog reverts to the last-known-good slot.

```
┌──────────────┐         ┌──────────────┐
│   Slot A     │         │   Slot B     │
│  (idle)      │◄───┐    │  (live)      │
│  /opt/sc-a   │    │    │  /opt/sc-b   │
└──────────────┘    │    └──────────────┘
                    │
           Watchdog switches
           back on failure
```

The switching mechanism differs by deployment mode (Docker port-swap vs nginx upstream change) but the overall flow — build candidate → health-check → switch or rollback — is the same.

## 2. Deployment Topology

### 2.1 Directory Layout

```
/opt/sharpclaw/
├── slot-a/                    # Git checkout A
│   ├── .env                   # Shared (symlink → /opt/sharpclaw/.env)
│   ├── docker-compose.yml
│   ├── scripts/
│   └── ...source...
├── slot-b/                    # Git checkout B
│   ├── .env                   # Shared (symlink → /opt/sharpclaw/.env)
│   ├── docker-compose.yml
│   ├── scripts/
│   └── ...source...
├── .env                       # Single source of truth for secrets/config
└── active -> slot-b/          # Symlink indicating the live slot
```

Both slots share the same `.env` file (via symlink) and connect to the **same PostgreSQL instance** — the database is *not* duplicated.

### 2.2 Docker Compose Mode — Container Naming

Each slot uses a distinct `COMPOSE_PROJECT_NAME` so that both can coexist on the same Docker host without container-name collisions:

| Slot | `COMPOSE_PROJECT_NAME` | API Container | Web Container |
|------|------------------------|---------------|---------------|
| A | `sharpclaw-a` | `sharpclaw-a-api` | `sharpclaw-a-ui` |
| B | `sharpclaw-b` | `sharpclaw-b-api` | `sharpclaw-b-ui` |

Only **one** slot's web container publishes port `8080` at a time. The idle slot is either stopped or running on an internal-only port for pre-flight checks.

### 2.3 Systemd Service Mode — Unit and Path Naming

Each slot publishes to a separate install directory and runs under a dedicated systemd unit, so both can coexist on the same host:

| Slot | API Install Dir | Systemd Unit | Backend Port |
|------|-----------------|--------------|--------------|
| A | `/opt/sharpclaw/api-a` | `sharpclaw-api-a.service` | `127.0.0.1:5000` |
| B | `/opt/sharpclaw/api-b` | `sharpclaw-api-b.service` | `127.0.0.1:5001` |

The frontend is static files served by nginx. Both slots share the same web root (`/var/www/sharpclaw`); the frontend build is deployed as part of the switchover after the candidate API passes health checks.

nginx always proxies `/api/` to the **active** backend port. Switching is done by updating the nginx upstream and reloading.

### 2.4 Shared Services

PostgreSQL remains a single instance managed outside the blue-green rotation.

**Docker Compose mode:**

```yaml
# /opt/sharpclaw/docker-compose.db.yml  (started once, shared)
services:
  postgres:
    image: postgres:16
    container_name: sharpclaw-db
    restart: unless-stopped
    ...
```

Each slot's `docker-compose.yml` uses `external_links` or a shared Docker network to reach the database.

**Systemd mode:**

PostgreSQL is installed as a system service by `scripts/install-linux.sh` and runs independently. Both API slots connect to it via the shared `SHARPCLAW_DB_CONNECTION` in `/etc/sharpclaw/env`.

## 3. Health Checks and Readiness Validation

### 3.1 API Health Check — Docker Compose Mode

The API container should declare a Docker `healthcheck` so that Docker (and the watchdog) can determine readiness:

```yaml
# In each slot's docker-compose.yml
services:
  sharpclaw:
    build: .
    healthcheck:
      test: ["CMD", "curl", "-sf", "http://localhost:8080/api/health"]
      interval: 10s
      timeout: 5s
      retries: 6
      start_period: 30s
```

The existing `GET /api/health` endpoint already returns `200 OK` with `{"status":"ok","service":"SharpClaw"}`, so no application-level changes are needed.

### 3.2 API Health Check — Systemd Service Mode

When running as a systemd service, health is validated by probing the candidate's backend port directly:

```bash
curl -sf http://127.0.0.1:5001/api/health   # candidate on slot B's port
```

The systemd unit already uses `Restart=on-failure` with `RestartSec=5`. The watchdog adds an explicit health-poll loop on top of systemd's restart behaviour to decide whether the candidate is truly ready before switching nginx.

### 3.3 Readiness Criteria

A new deployment is considered **ready** when **all** of the following are true:

**Docker Compose mode:**

1. `docker compose up -d` exits with code 0.
2. The API container reaches Docker health status `healthy` within the `start_period + (interval × retries)` window (≈ 90 s with the defaults above).
3. An HTTP request to `GET /api/health` on the candidate's internal port returns `200 OK`.

**Systemd service mode:**

1. `dotnet publish` completes with exit code 0.
2. `systemctl start sharpclaw-api-{slot}` succeeds and the unit remains `active`.
3. An HTTP request to `GET /api/health` on the candidate's port (e.g. `127.0.0.1:5001`) returns `200 OK` within the timeout window.

## 4. Switching Mechanism

### 4.1 Deploy Flow

The sequence when SharpClaw (or an operator) wants to deploy new code:

```
 ┌─────────────────────────────────────────────────────────────────┐
 │ 1. Identify idle slot                                          │
 │    idle = (A if active → B) else B                             │
 │                                                                │
 │ 2. Pull / apply changes in idle slot                           │
 │    cd /opt/sharpclaw/$idle && git pull origin main              │
 │                                                                │
 │ 3. Build and start the idle slot                               │
 │    Docker:  COMPOSE_PROJECT_NAME=sharpclaw-$idle               │
 │             docker compose up -d                               │
 │    Systemd: dotnet publish ... -o /opt/sharpclaw/api-$idle     │
 │             systemctl start sharpclaw-api-$idle                │
 │                                                                │
 │ 4. Wait for healthy                                            │
 │    Docker:  Poll Docker health status until healthy or timeout │
 │    Systemd: Poll GET /api/health on candidate port             │
 │                                                                │
 │ 5a. Healthy → Switch traffic                                   │
 │     Docker:                                                    │
 │       • Stop live slot's web container (frees port 8080)       │
 │       • Re-create idle slot's web container with port 8080:80  │
 │       • Update symlink: active → idle slot                     │
 │       • (Optionally stop old slot entirely)                    │
 │     Systemd:                                                   │
 │       • Build and deploy frontend to /var/www/sharpclaw        │
 │       • Update nginx upstream to candidate port                │
 │       • Reload nginx                                           │
 │       • Stop old slot's systemd unit                           │
 │       • Update symlink: active → idle slot                     │
 │                                                                │
 │ 5b. Unhealthy → Rollback (see §5)                             │
 │     • Tear down idle slot                                      │
 │     • Live slot continues serving — no downtime                │
 │     • Log the failure for operator review                      │
 └─────────────────────────────────────────────────────────────────┘
```

### 4.2 Port Switching — Docker Compose Mode

Because only one Nginx container can bind host port `8080` at a time, the switch is a two-step atomic-ish operation:

1. Stop the current live web container → port `8080` is freed.
2. Start the new web container with `ports: ["8080:80"]`.

This causes a brief interruption (typically < 2 s). For zero-downtime switching in the future, a host-level reverse proxy (Traefik, Caddy) could front both slots and route by header or health.

### 4.3 Upstream Switching — Systemd Service Mode

When running as systemd services, nginx already sits in front of the API. Switching is done by changing the `proxy_pass` target port in the nginx site configuration and reloading:

```bash
# Update the nginx config to proxy to the candidate port
sudo sed -i 's|proxy_pass http://127.0.0.1:500[0-9]/|proxy_pass http://127.0.0.1:5001/|' \
    /etc/nginx/sites-available/sharpclaw   # or conf.d/sharpclaw.conf

# Validate and reload
sudo nginx -t && sudo systemctl reload nginx
```

This causes **zero downtime** — nginx finishes in-flight requests on the old upstream before routing new ones to the candidate. The old API unit is stopped after the reload completes.

## 5. Rollback Triggers and Fallback Path

### 5.1 Automatic Rollback Triggers

| Trigger | Detection |
|---------|-----------|
| Build failure | `docker compose build` or `dotnet publish` exits non-zero |
| Service crash on start | Container exits / systemd unit enters `failed` state |
| Health check timeout | Docker reports `unhealthy` / HTTP probe not `200` within timeout |
| HTTP health probe failure | `GET /api/health` does not return `200` within timeout |

### 5.2 Rollback Procedure — Docker Compose Mode

1. **Tear down** the failed candidate slot's containers:
   ```bash
   COMPOSE_PROJECT_NAME=sharpclaw-$idle docker compose down
   ```
2. **Ensure** the live slot is still running (it should never have been stopped):
   ```bash
   COMPOSE_PROJECT_NAME=sharpclaw-$live docker compose up -d
   ```
3. **Log** the failure reason (build output, container logs, health check result).
4. **Revert** the idle slot's source to match the last-known-good commit:
   ```bash
   cd /opt/sharpclaw/$idle && git checkout $LAST_GOOD_SHA
   ```
5. The `active` symlink is **not** updated — it still points to the live slot.

### 5.3 Rollback Procedure — Systemd Service Mode

1. **Stop** the failed candidate's systemd unit:
   ```bash
   sudo systemctl stop sharpclaw-api-$idle
   ```
2. **Ensure** the live slot's unit is still running:
   ```bash
   sudo systemctl is-active sharpclaw-api-$live || sudo systemctl start sharpclaw-api-$live
   ```
3. **Verify** that the nginx upstream still points to the live slot's port (it should — nginx config is only updated *after* the candidate passes health checks).
4. **Log** the failure reason (`journalctl -u sharpclaw-api-$idle`, build output).
5. **Revert** the idle slot's source:
   ```bash
   cd /opt/sharpclaw/$idle && git checkout $LAST_GOOD_SHA
   ```
6. The `active` symlink and nginx config are **not** updated.

### 5.4 Manual Rollback

An operator can force a rollback at any time:

**Docker Compose mode:**

```bash
# Switch back to slot A (assuming slot B is currently live)
cd /opt/sharpclaw/slot-b && docker compose down
cd /opt/sharpclaw/slot-a && docker compose up -d
ln -sfn slot-a /opt/sharpclaw/active
```

**Systemd service mode:**

```bash
# Switch back to slot A (assuming slot B is currently live)
sudo systemctl stop sharpclaw-api-b
sudo systemctl start sharpclaw-api-a

# Point nginx to slot A's port
sudo sed -i 's|proxy_pass http://127.0.0.1:5001/|proxy_pass http://127.0.0.1:5000/|' \
    /etc/nginx/sites-available/sharpclaw
sudo nginx -t && sudo systemctl reload nginx

ln -sfn slot-a /opt/sharpclaw/active
```

## 6. Watchdog Design

The watchdog is a lightweight process (script or small service) running on the host that orchestrates deploys and monitors the result.

### 6.1 Responsibilities

- Accept a deploy request (from the RebuildHook webhook, or a CLI invocation).
- Determine the idle slot.
- Execute the deploy flow (§4.1).
- Poll health and decide pass/fail.
- Execute rollback if needed (§5.2 / §5.3).
- Emit structured logs for every action.

### 6.2 Integration with RebuildHook (Docker Compose Mode)

The existing `SharpClaw.RebuildHook` service already accepts `POST /rebuild` requests and runs `docker compose build && up -d`. The watchdog extends this behaviour:

```
  Agent or Webhook ──► POST /rebuild ──► RebuildHook
                                             │
                                      Watchdog logic
                                         │       │
                                      healthy?  unhealthy?
                                         │       │
                                      switch    rollback
```

The watchdog can be implemented as:
- **Option A**: Enhanced logic inside `DockerComposeService.RebuildAsync()`, adding health-poll and slot-management steps.
- **Option B**: A separate shell script or systemd service invoked by the RebuildHook after a successful build.

Option A is recommended for a first iteration because it keeps the deploy logic in one place and benefits from .NET's structured logging.

### 6.3 Integration with install-service-linux.sh (Systemd Mode)

The existing `scripts/install-service-linux.sh` already performs the full publish → deploy → restart cycle for a single-slot setup. The watchdog extends this by:

1. Publishing to the **idle** slot's install directory (`/opt/sharpclaw/api-{a|b}`) instead of the shared `/opt/sharpclaw/api`.
2. Starting the idle slot's dedicated systemd unit (`sharpclaw-api-{a|b}.service`) on its own port.
3. Running the health-poll loop against the candidate port.
4. On success: deploying the frontend, updating the nginx upstream, and reloading nginx.
5. On failure: stopping the candidate unit and logging the error.

The install script can be adapted with a `--slot` flag to target a specific slot, or the watchdog can call `dotnet publish` and `systemctl` directly.

### 6.4 Health Polling Loop (Pseudocode)

```
function waitForHealthy(target, timeout):
    // target = container name (Docker) or http://127.0.0.1:PORT (systemd)
    deadline = now + timeout
    while now < deadline:
        if mode == docker:
            status = docker inspect --format '{{.State.Health.Status}}' target
            if status == "healthy": return true
            if status == "unhealthy": return false
        else:  // systemd
            if curl -sf {target}/api/health returns 200: return true
            if systemctl is-failed sharpclaw-api-{slot}: return false
        sleep 5s
    return false   // timed out
```

## 7. Interaction with Persistent State

### 7.1 Database

Both slots share the same PostgreSQL instance and connection string. Database migrations (if any) must be **forward-compatible**: the live slot must still work correctly after a migration has been applied by the candidate. This is the standard blue-green database constraint.

**Guideline**: Use additive-only migrations (add columns/tables, don't drop or rename) during the switchover window. Destructive changes can be cleaned up in a follow-up deploy after the old slot is confirmed stopped.

In systemd mode, the shared connection string lives in `/etc/sharpclaw/env` and is read by both slot units via `EnvironmentFile=`.

### 7.2 Workspace Volume

The `SHARPCLAW_WORKSPACE` volume is shared between both slots (same host path: `/workspace` in Docker, `/opt/sharpclaw/workspace` for systemd). File-level conflicts are unlikely because only one API instance is processing requests at a time, but care should be taken with long-running agent tasks that span a switchover.

### 7.3 MCP Server State

MCP server processes are spawned by the API. On switchover the old process is stopped, terminating its MCP child processes. The new instance spawns fresh MCP processes as needed. No shared MCP state persists outside the database.

### 7.4 Frontend Assets

In systemd mode the compiled React frontend is served from `/var/www/sharpclaw`. This is a single shared directory — the frontend build is only deployed **after** the candidate API has passed health checks, as part of the switch step. If the candidate fails, the existing frontend continues to be served unchanged.

## 8. Operational Workflow

### 8.1 Initial Setup — Docker Compose Mode

```bash
# 1. Create the directory structure
sudo mkdir -p /opt/sharpclaw/{slot-a,slot-b}

# 2. Clone the repo into both slots
git clone https://github.com/kjhughes097/sharpclaw.git /opt/sharpclaw/slot-a
git clone https://github.com/kjhughes097/sharpclaw.git /opt/sharpclaw/slot-b

# 3. Shared config
cp .env /opt/sharpclaw/.env
ln -s /opt/sharpclaw/.env /opt/sharpclaw/slot-a/.env
ln -s /opt/sharpclaw/.env /opt/sharpclaw/slot-b/.env

# 4. Start the database (shared)
cd /opt/sharpclaw && docker compose -f docker-compose.db.yml up -d

# 5. Start slot A as the initial live deployment
cd /opt/sharpclaw/slot-a
COMPOSE_PROJECT_NAME=sharpclaw-a docker compose up -d
ln -sfn slot-a /opt/sharpclaw/active
```

### 8.2 Initial Setup — Systemd Service Mode

```bash
# 1. Create the directory structure and clone
sudo mkdir -p /opt/sharpclaw/{slot-a,slot-b}
git clone https://github.com/kjhughes097/sharpclaw.git /opt/sharpclaw/slot-a
git clone https://github.com/kjhughes097/sharpclaw.git /opt/sharpclaw/slot-b

# 2. Install prerequisites (once)
cp .env.example /opt/sharpclaw/slot-a/.env && $EDITOR /opt/sharpclaw/slot-a/.env
ln -s /opt/sharpclaw/slot-a/.env /opt/sharpclaw/slot-b/.env
sudo /opt/sharpclaw/slot-a/scripts/install-linux.sh

# 3. Deploy slot A as the initial live service
sudo /opt/sharpclaw/slot-a/scripts/install-service-linux.sh
ln -sfn slot-a /opt/sharpclaw/active
```

### 8.3 Deploying a New Version — Docker Compose Mode

```bash
# Automated via RebuildHook / watchdog, or manually:
IDLE=slot-b   # if active → slot-a
cd /opt/sharpclaw/$IDLE
git pull origin main

COMPOSE_PROJECT_NAME=sharpclaw-b docker compose build
COMPOSE_PROJECT_NAME=sharpclaw-b docker compose up -d

# Wait for healthy (watchdog does this automatically)
# ... then switch port and symlink
```

### 8.4 Deploying a New Version — Systemd Service Mode

```bash
IDLE=slot-b   # if active → slot-a
cd /opt/sharpclaw/$IDLE
git pull origin main

# Publish to the idle slot's install dir
dotnet publish SharpClaw.Api/SharpClaw.Api.csproj -c Release -o /opt/sharpclaw/api-b

# Start the candidate on its own port and health-check
sudo systemctl start sharpclaw-api-b
# Wait for healthy (watchdog does this automatically)
# ... then update nginx upstream, reload, deploy frontend, update symlink
```

### 8.5 Monitoring

- **Container health**: `docker ps --filter "name=sharpclaw" --format "{{.Names}}\t{{.Status}}"`
- **Application logs**: `docker logs sharpclaw-a-api --tail 50 -f`
- **Deploy history**: Structured logs from the RebuildHook/watchdog service.

## 9. Required Architecture Changes

| Change | Scope | Description |
|--------|-------|-------------|
| Add Docker healthcheck to API service | `docker-compose.yml` | Uses existing `/api/health` endpoint |
| Remove hardcoded `container_name` values | `docker-compose.yml` | Allow `COMPOSE_PROJECT_NAME` to namespace containers |
| Extract PostgreSQL to a shared compose file | New `docker-compose.db.yml` | Database is started once, independent of blue-green slots |
| Enhance `DockerComposeService` | `SharpClaw.RebuildHook` | Add slot management, health polling, and rollback logic |
| Add deploy configuration | `appsettings.json` / env vars | Slot paths, health timeout, rollback policy |

## 10. Future Enhancements

- **Host-level reverse proxy** (Traefik/Caddy) for true zero-downtime switching.
- **Canary deploys**: Route a percentage of traffic to the candidate before full switchover.
- **Automated smoke tests**: Run a test suite against the candidate before switching.
- **Notification integration**: Alert via Telegram (see [#11](https://github.com/kjhughes097/sharpclaw/issues/11)) on deploy success/failure.
- **Database migration guard**: Verify migration compatibility before applying.
