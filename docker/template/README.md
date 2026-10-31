# SharpClaw install

This directory is your SharpClaw deployment: `compose.yml`, `.env` (secrets), each agent's
`config/` (soul and tool policy) and `workspace/`, the shared skill library in `skills/`, and the
gateway's SQLite database in `data/`.
It was created by `scripts/install.sh` from the SharpClaw repo and is yours to edit — the
installer never overwrites files here.

Images are built from the repo checkout (`scripts/build-images.sh`), not here.

## First run

If you ran `scripts/quickstart.sh` from the repo, this is already done: the agents' tokens are in
`.env`, your web UI user exists, and everything is running. Open `http://localhost:8080` (or the
interface and port you chose). Re-run it (pointing at this directory) to switch provider, change the
interface or ports, add agents, or overwrite `compose.yml` or an agent with fresh defaults.

By hand:

1. Check `.env`: `ADMIN_KEY` and `SHARPCLAW_UID`/`SHARPCLAW_GID` are filled in for you. Set
   `OPENROUTER_API_KEY` (or `PROVIDER_TYPE=Fake`) and, optionally, the OTLP endpoints.
2. Start the gateway: `docker compose up -d gateway`
3. Create the agent and paste its token into `.env` (`AGENT_ASSISTANT_TOKEN`):
   ```bash
   sharpclaw agents create assistant --model <id> --daily-budget 10
   ```
4. Start the agents: `docker compose up -d`
5. Create your web UI user: `sharpclaw users create <name>`, then open `http://localhost:8080`.

## Adding an agent

1. Copy the `agent-assistant` service block (and its `sandbox-assistant`, `sbx-assistant`
   network and cache volume, for external tools) in `compose.yml` and rename it, its token
   variable and its mount paths (keep the `./skills:/skills:ro` line).
2. Create `agents/<name>/config/soul.md`, `agents/<name>/config/agent.json` (copy the assistant's
   and edit) and an empty `agents/<name>/workspace/` owned by `SHARPCLAW_UID`.
3. `sharpclaw agents create <name> …`, add the token to `.env`, `docker compose up -d`.

## Skills

`skills/<name>/SKILL.md` folders are shared, read-only, by every agent whose `agent.json` lists
`skill_read`. The folder name must match the `name` in the frontmatter. Restart the agents
(`docker compose restart`) after adding, removing or renaming a skill or changing its description;
the agent's log lists the skills it loaded and any it skipped, with the reason. **Read every file of
a skill you didn't write before adding it**: skills are trusted like a soul, so a malicious one
could steer your agent. See "Skills" in the repo's README.

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
