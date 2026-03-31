# Blue-Green Deployment Strategy with Automatic Rollback

> **Status**: Design / Specification
> **Issue**: [#15](https://github.com/kjhughes097/sharpclaw/issues/15)

## 1. Overview

SharpClaw agents can modify their own source code. A blue-green deployment model lets SharpClaw apply those changes, rebuild, and switch over to the new version — with an automatic rollback path if the new version fails to start or misbehaves.

### Core Idea

The repository is checked out in **two side-by-side locations** on the host (called **Slot A** and **Slot B**). At any time one slot is *live* (serving traffic) and the other is *idle* (available for the next update). A lightweight **watchdog** monitors the newly deployed container; if it never becomes healthy the watchdog reverts to the last-known-good slot.

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

## 2. Deployment Topology

### 2.1 Directory Layout

```
/opt/sharpclaw/
├── slot-a/                    # Git checkout A
│   ├── .env                   # Shared (symlink → /opt/sharpclaw/.env)
│   ├── docker-compose.yml
│   └── ...source...
├── slot-b/                    # Git checkout B
│   ├── .env                   # Shared (symlink → /opt/sharpclaw/.env)
│   ├── docker-compose.yml
│   └── ...source...
├── .env                       # Single source of truth for secrets/config
└── active -> slot-b/          # Symlink indicating the live slot
```

Both slots share the same `.env` file (via symlink) and connect to the **same PostgreSQL instance** — the database is *not* duplicated.

### 2.2 Container Naming

Each slot uses a distinct `COMPOSE_PROJECT_NAME` so that both can coexist on the same Docker host without container-name collisions:

| Slot | `COMPOSE_PROJECT_NAME` | API Container | Web Container |
|------|------------------------|---------------|---------------|
| A | `sharpclaw-a` | `sharpclaw-a-api` | `sharpclaw-a-ui` |
| B | `sharpclaw-b` | `sharpclaw-b-api` | `sharpclaw-b-ui` |

Only **one** slot's web container publishes port `8080` at a time. The idle slot is either stopped or running on an internal-only port for pre-flight checks.

### 2.3 Shared Services

PostgreSQL remains a single instance managed outside the blue-green rotation:

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

## 3. Health Checks and Readiness Validation

### 3.1 API Container Health Check

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

### 3.2 Readiness Criteria

A new deployment is considered **ready** when **all** of the following are true:

1. `docker compose up -d` exits with code 0.
2. The API container reaches Docker health status `healthy` within the `start_period + (interval × retries)` window (≈ 90 s with the defaults above).
3. An HTTP request to `GET /api/health` on the candidate's internal port returns `200 OK`.

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
 │ 3. Build and start the idle slot (no published port yet)       │
 │    COMPOSE_PROJECT_NAME=sharpclaw-$idle docker compose up -d   │
 │                                                                │
 │ 4. Wait for healthy                                            │
 │    Poll Docker health status until healthy or timeout          │
 │                                                                │
 │ 5a. Healthy → Switch traffic                                   │
 │     • Stop live slot's web container (frees port 8080)         │
 │     • Re-create idle slot's web container with port 8080:80    │
 │     • Update symlink: active → idle slot                       │
 │     • (Optionally stop old slot entirely)                      │
 │                                                                │
 │ 5b. Unhealthy → Rollback (see §5)                             │
 │     • Tear down idle slot's containers                         │
 │     • Live slot continues serving — no downtime                │
 │     • Log the failure for operator review                      │
 └─────────────────────────────────────────────────────────────────┘
```

### 4.2 Port Switching

Because only one Nginx container can bind host port `8080` at a time, the switch is a two-step atomic-ish operation:

1. Stop the current live web container → port `8080` is freed.
2. Start the new web container with `ports: ["8080:80"]`.

This causes a brief interruption (typically < 2 s). For zero-downtime switching in the future, a host-level reverse proxy (Traefik, Caddy) could front both slots and route by header or health.

## 5. Rollback Triggers and Fallback Path

### 5.1 Automatic Rollback Triggers

| Trigger | Detection |
|---------|-----------|
| Build failure | `docker compose build` exits non-zero |
| Container crash on start | Container exits immediately or restart-loops |
| Health check timeout | Docker reports `unhealthy` after retries exhausted |
| HTTP health probe failure | `GET /api/health` does not return `200` within timeout |

### 5.2 Rollback Procedure

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

### 5.3 Manual Rollback

An operator can force a rollback at any time:

```bash
# Switch back to slot A (assuming slot B is currently live)
cd /opt/sharpclaw/slot-b && docker compose down
cd /opt/sharpclaw/slot-a && docker compose up -d
ln -sfn slot-a /opt/sharpclaw/active
```

## 6. Watchdog Design

The watchdog is a lightweight process (script or small service) running on the host that orchestrates deploys and monitors the result.

### 6.1 Responsibilities

- Accept a deploy request (from the RebuildHook webhook, or a CLI invocation).
- Determine the idle slot.
- Execute the deploy flow (§4.1).
- Poll health and decide pass/fail.
- Execute rollback if needed (§5.2).
- Emit structured logs for every action.

### 6.2 Integration with RebuildHook

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

### 6.3 Health Polling Loop (Pseudocode)

```
function waitForHealthy(container, timeout):
    deadline = now + timeout
    while now < deadline:
        status = docker inspect --format '{{.State.Health.Status}}' container
        if status == "healthy":
            return true
        if status == "unhealthy":
            return false
        sleep 5s
    return false   // timed out
```

## 7. Interaction with Persistent State

### 7.1 Database

Both slots share the same PostgreSQL instance and connection string. Database migrations (if any) must be **forward-compatible**: the live slot must still work correctly after a migration has been applied by the candidate. This is the standard blue-green database constraint.

**Guideline**: Use additive-only migrations (add columns/tables, don't drop or rename) during the switchover window. Destructive changes can be cleaned up in a follow-up deploy after the old slot is confirmed stopped.

### 7.2 Workspace Volume

The `SHARPCLAW_WORKSPACE` volume (`/workspace`) is shared between both slots (same host path). File-level conflicts are unlikely because only one API container is processing requests at a time, but care should be taken with long-running agent tasks that span a switchover.

### 7.3 MCP Server State

MCP server processes are spawned by the API container. On switchover the old container is stopped, terminating its MCP child processes. The new container spawns fresh MCP processes as needed. No shared MCP state persists outside the database.

## 8. Operational Workflow

### 8.1 Initial Setup

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

### 8.2 Deploying a New Version

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

### 8.3 Monitoring

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
