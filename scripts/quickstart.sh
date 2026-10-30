#!/usr/bin/env bash
# One command from a fresh clone to a running SharpClaw: builds the images, creates the install,
# starts the gateway, creates the agents (tokens go straight into .env), creates your web UI user
# and starts the agents. Needs only Docker (with compose), curl and openssl on the host.
#
#   scripts/quickstart.sh [dir]                         (default: ~/sharpclaw)
#   OPENROUTER_API_KEY=sk-or-... scripts/quickstart.sh  real models instead of the fake provider
#
# Optional environment: SHARPCLAW_MODEL (OpenRouter model for new agents, default
# openai/gpt-4o-mini), SHARPCLAW_BUDGET (daily USD budget per new agent, default 1),
# SHARPCLAW_USER (web UI user name, default your login name or "admin"), CLIENT_PORT (default 8080).
#
# Safe to re-run: it never overwrites your files, keeps existing tokens and users, and only fills
# in what is missing.
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DEST="${1:-$HOME/sharpclaw}"

for CMD in docker curl openssl; do
  command -v "$CMD" >/dev/null || { echo "quickstart: '$CMD' is required" >&2; exit 1; }
done
docker compose version >/dev/null 2>&1 || { echo "quickstart: 'docker compose' (v2) is required" >&2; exit 1; }

# Noisy steps (image builds, compose progress) go to a log that is shown only if they fail.
LOG="$(mktemp -t sharpclaw-quickstart.XXXXXX.log)"
quiet() {
  if ! "$@" >>"$LOG" 2>&1; then
    echo "quickstart: '$*' failed; last lines of $LOG:" >&2
    tail -n 30 "$LOG" >&2
    exit 1
  fi
}

# --- .env helpers (KEY=value lines; values never contain newlines) ---
env_get() { grep -E "^$1=" "$DEST/.env" | head -n1 | cut -d= -f2- || true; }
env_set() {
  # '|' as the sed delimiter: values may contain '/' (keys, base64) but never '|'.
  if grep -qE "^$1=" "$DEST/.env"; then sed -i "s|^$1=.*|$1=$2|" "$DEST/.env"
  else printf '%s=%s\n' "$1" "$2" >>"$DEST/.env"; fi
}

echo "==> Building images (the first build takes a few minutes; later ones are cached)"
quiet "$REPO/scripts/build-images.sh"

echo "==> Creating the install in $DEST"
"$REPO/scripts/install.sh" "$DEST" | sed 's/^/    /'
DEST="$(cd "$DEST" && pwd)"

# Provider: a key from the environment wins; with no key at all, use the fake provider so the
# stack works without an account or spend.
if [ -n "${OPENROUTER_API_KEY:-}" ]; then
  env_set OPENROUTER_API_KEY "$OPENROUTER_API_KEY"
  env_set PROVIDER_TYPE OpenRouter
elif [ -z "$(env_get OPENROUTER_API_KEY)" ] && [ "$(env_get PROVIDER_TYPE)" != "Fake" ]; then
  echo "    no OPENROUTER_API_KEY: using the fake provider (canned replies, no cost)"
  env_set PROVIDER_TYPE Fake
fi
[ -n "${CLIENT_PORT:-}" ] && env_set CLIENT_PORT "$CLIENT_PORT"

PROVIDER="$(env_get PROVIDER_TYPE)"
PORT="$(env_get CLIENT_PORT)"; PORT="${PORT:-8080}"
BASE="http://127.0.0.1:$PORT"
ADMIN_KEY="$(env_get ADMIN_KEY)"
AUTH=(-H "Authorization: Bearer $ADMIN_KEY" -H "Content-Type: application/json")

cd "$DEST"
echo "==> Starting the gateway ($PROVIDER provider) on $BASE"
quiet docker compose up -d --wait gateway

# Agents: one per AGENT_<NAME>_TOKEN line in .env (the template has assistant and coder).
if [ "$PROVIDER" = "Fake" ]; then MODEL="fake/model"; else MODEL="${SHARPCLAW_MODEL:-openai/gpt-4o-mini}"; fi
BUDGET="${SHARPCLAW_BUDGET:-1}"
for VAR in $(grep -oE '^AGENT_[A-Z0-9_]+_TOKEN=' .env | tr -d '='); do
  [ -z "$(env_get "$VAR")" ] || continue
  NAME="$(echo "${VAR#AGENT_}" | sed 's/_TOKEN$//' | tr 'A-Z_' 'a-z-')"
  BODY="$(curl -sS -w '\n%{http_code}' "${AUTH[@]}" -X POST "$BASE/api/agents" \
    -d "{\"name\":\"$NAME\",\"model\":\"$MODEL\",\"dailyBudgetUsd\":$BUDGET}")"
  if [ "$(echo "$BODY" | tail -n1)" = "409" ]; then
    # The agent exists but .env lost its token: issue a fresh one.
    BODY="$(curl -sS -w '\n%{http_code}' "${AUTH[@]}" -X POST "$BASE/api/agents/$NAME/tokens" -d '{}')"
  fi
  TOKEN="$(echo "$BODY" | grep -oE '"value":"sc_[^"]+"' | cut -d'"' -f4 || true)"
  [ -n "$TOKEN" ] || { echo "quickstart: could not create agent '$NAME': $(echo "$BODY" | head -n1)" >&2; exit 1; }
  env_set "$VAR" "$TOKEN"
  echo "    agent '$NAME' created (model $MODEL, \$$BUDGET/day); token saved in .env"
done

# Web UI user: only when there are none yet. The password is shown once, like tokens.
PASSWORD=""
if [ "$(curl -sS "${AUTH[@]}" "$BASE/api/users" | grep -c '"name"' || true)" = "0" ]; then
  USER_NAME="${SHARPCLAW_USER:-$(id -un | tr 'A-Z' 'a-z')}"
  [[ "$USER_NAME" =~ ^[a-z0-9][a-z0-9._-]{1,31}$ ]] || USER_NAME=admin
  PASSWORD="$(openssl rand -base64 18)"
  curl -fsS "${AUTH[@]}" -X POST "$BASE/api/users" \
    -d "{\"name\":\"$USER_NAME\",\"password\":\"$PASSWORD\"}" >/dev/null
fi

echo "==> Starting the agents"
quiet docker compose up -d

echo
echo "SharpClaw is running: open $BASE"
if [ -n "$PASSWORD" ]; then
  echo "  user:     $USER_NAME"
  echo "  password: $PASSWORD   (shown once; change it under Account in the web UI)"
fi
if [ "$PROVIDER" = "Fake" ]; then
  echo "  Using the fake provider. For real models: set OPENROUTER_API_KEY and PROVIDER_TYPE=OpenRouter"
  echo "  in $DEST/.env, point the agents at a real model, then 'docker compose up -d' (see README)."
fi
rm -f "$LOG"
echo "  Install directory: $DEST (compose.yml, .env, agent souls and policies, data/)"
