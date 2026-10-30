# SharpClaw install

This directory is your SharpClaw deployment: `compose.yml`, `.env` (secrets), each agent's
`config/` (soul and tool policy) and `workspace/`, and the gateway's SQLite database in `data/`.
It was created by `scripts/install.sh` from the SharpClaw repo and is yours to edit — the
installer never overwrites files here.

Images are built from the repo checkout (`scripts/build-images.sh`), not here.

## First run

If you ran `scripts/quickstart.sh` from the repo, this is already done: the agents' tokens are in
`.env`, your web UI user exists, and everything is running. Open `http://localhost:8080`. Re-run it
(pointing at this directory) to switch provider or add agents.

By hand:

1. Check `.env`: `ADMIN_KEY` and `SHARPCLAW_UID`/`SHARPCLAW_GID` are filled in for you. Set
   `OPENROUTER_API_KEY` (or `PROVIDER_TYPE=Fake`) and, optionally, the OTLP endpoints.
2. Start the gateway: `docker compose up -d gateway`
3. Create the agents and paste each token into `.env` (`AGENT_ASSISTANT_TOKEN`,
   `AGENT_CODER_TOKEN`):
   ```bash
   sharpclaw agents create assistant --model <id> --daily-budget 1
   sharpclaw agents create coder --model <id> --daily-budget 1
   ```
4. Start the agents: `docker compose up -d`
5. Create your web UI user: `sharpclaw users create <name>`, then open `http://localhost:8080`.

## Adding an agent

1. Copy an `agent-*` service block in `compose.yml` and rename it, its token variable and its
   mount paths.
2. Create `agents/<name>/config/soul.md`, `agents/<name>/config/agent.json` and an empty
   `agents/<name>/workspace/` owned by `SHARPCLAW_UID`.
3. `sharpclaw agents create <name> …`, add the token to `.env`, `docker compose up -d`.

## Day to day

`docker compose ps` (status), `docker compose logs -f gateway` (logs), `docker compose down` /
`docker compose up -d` (stop / start; your data stays in `data/`).

To switch from the fake provider to real models, re-run `scripts/quickstart.sh` from the repo and
choose OpenRouter. By hand: set `OPENROUTER_API_KEY` and `PROVIDER_TYPE=OpenRouter` here,
`docker compose up -d`, then give each agent a real model on its tab in the web UI (Settings → Model).

## Upgrading

In the repo: `git pull && scripts/build-images.sh`. Here: `docker compose up -d`. To pin or roll
back, set `SHARPCLAW_TAG` in `.env` to an image tag (`docker images sharpclaw-gateway`).

The installer doesn't update your files. When the repo's `docker/template/` changes, compare it
with yours (`diff -u <repo>/docker/template/compose.yml compose.yml`) and merge by hand.

## Checking isolation

After changing `compose.yml`, run `bash <repo>/scripts/verify-isolation.sh` from this directory.
