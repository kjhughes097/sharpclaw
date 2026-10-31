#!/usr/bin/env bash
# Interactive setup from a fresh clone to a running SharpClaw. It asks where to install, which model
# provider to use (Fake to try it out, or OpenRouter with your API key), which agents to create and
# your web UI user name, then builds the images, creates the install, creates the agents (tokens go
# straight into .env) and your user, and starts everything. Every agent gets its own sandbox and
# all broker tools (ToolBroker__Agents__<name>__AllTools). Needs only Docker (with compose), curl
# and openssl on the host.
#
#   scripts/quickstart.sh [dir]     (dir skips the install-location question)
#
# Safe to re-run on an existing install: it never overwrites your files, keeps existing agents,
# tokens and users, and lets you switch provider or add more agents. CLIENT_PORT=<port> in the
# environment publishes a new install on another port (e.g. for a second install on this host,
# together with COMPOSE_PROJECT_NAME).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
NAME_RE='^[a-z0-9][a-z0-9-]{1,31}$'        # agent names (the gateway's rule)
USER_RE='^[a-z0-9][a-z0-9._-]{1,31}$'      # web UI user names
DEFAULT_MODEL="openai/gpt-4o-mini"
BUDGET=1

for CMD in docker curl openssl; do
  command -v "$CMD" >/dev/null || { echo "quickstart: '$CMD' is required" >&2; exit 1; }
done
docker compose version >/dev/null 2>&1 || { echo "quickstart: 'docker compose' (v2) is required" >&2; exit 1; }

# --- prompts (end of input cancels) ---
ask() { # ask VAR "question" [default]
  local reply
  read -r -p "$2${3:+ [$3]}: " reply || { echo; echo "quickstart: cancelled" >&2; exit 1; }
  printf -v "$1" '%s' "${reply:-${3:-}}"
}
ask_secret() { # ask_secret VAR "question" (not echoed)
  local reply
  read -r -s -p "$2: " reply || { echo; echo "quickstart: cancelled" >&2; exit 1; }
  echo >&2
  printf -v "$1" '%s' "$reply"
}

# --- .env helpers. awk with ENVIRON, not sed, so any value is written literally; rewriting through
# `cat >` keeps the file's mode 600. ---
env_get() { [ -f "$ENV" ] && grep -E "^$1=" "$ENV" | head -n1 | cut -d= -f2- || true; }
env_set() {
  local tmp; tmp="$(mktemp)"
  K="$1" V="$2" awk 'BEGIN { k = ENVIRON["K"]; v = ENVIRON["V"] }
    index($0, k "=") == 1 { if (!done) print k "=" v; done = 1; next } { print }
    END { if (!done) print k "=" v }' "$ENV" >"$tmp"
  cat "$tmp" >"$ENV"; rm -f "$tmp"
}
env_del() {
  local tmp; tmp="$(mktemp)"
  K="$1" awk 'index($0, ENVIRON["K"] "=") != 1' "$ENV" >"$tmp"
  cat "$tmp" >"$ENV"; rm -f "$tmp"
}
token_var() { echo "AGENT_$(echo "$1" | tr 'a-z-' 'A-Z_')_TOKEN"; }
compose_agents() { [ -f "$COMPOSE" ] && grep -oE '^  agent-[a-z0-9-]+:' "$COMPOSE" | sed 's/^  agent-//; s/:$//' || true; }
contains() { local x; for x in "${@:2}"; do [ "$x" = "$1" ] && return 0; done; return 1; }

# --- compose.yml helpers (awk with ENVIRON so names are matched literally). ---
compose_awk() { # compose_awk 'program' (rewrites compose.yml in place)
  local tmp; tmp="$(mktemp)"
  awk "$1" "$COMPOSE" >"$tmp"
  cat "$tmp" >"$COMPOSE"; rm -f "$tmp"
}
remove_block() { # remove_block "  key:" (a service or network block, up to the next sibling or section)
  # A blank line ends the block too, unless the block already had one before it.
  B="$1" compose_awk '$0 == ENVIRON["B"] { skip = 1; next }
    skip && (/^(  [^ ]|[^ \t])/ || ($0 == "" && last != "")) { skip = 0 }
    !skip { print; last = $0 }'
}
append_to_section() { # append_to_section networks|volumes "lines" (at the end of that top-level section)
  S="$1:" L="$2" compose_awk '
    $0 == ENVIRON["S"] { in_s = 1; print; next }
    in_s && ($0 == "" || /^[^ \t#]/) { print ENVIRON["L"]; in_s = 0 }
    { print } END { if (in_s) print ENVIRON["L"] }'
}
sandbox_var() { echo "SANDBOX_$(echo "$1" | tr 'a-z-' 'A-Z_')_${2:-}SECRET"; }

# Every broker tool (BrokerTools.All in SharpClaw.Contracts). Quick-start agents get all of them in
# agent.json; the gateway allows them through ToolBroker__Agents__<name>__AllTools.
BROKER_TOOLS=(http_fetch download_to_workspace web_search extract_text git_clone sandbox_run
  github_read github_write github_create_pr)

# Removes an agent's sandbox: its service, network, cache volume, gateway settings and secrets.
sandbox_remove() {
  local A="$1"
  remove_block "  sandbox-$A:"
  remove_block "  sbx-$A:"
  A="$A" compose_awk '
    BEGIN { a = ENVIRON["A"] }
    $0 == "  sandbox-" a "-cache: {}" { next }
    index($0, "      ToolBroker__Agents__" a "__") == 1 || index($0, "      Egress__Sandboxes__" a "__") == 1 { next }
    /^    networks: \[internal, external/ { sub(", sbx-" a "\\]", "]"); sub(", sbx-" a ",", ",") }
    { print }'
  env_del "$(sandbox_var "$A")"; env_del "$(sandbox_var "$A" PROXY_)"
}

# Gives an agent its own sandbox (the template's sandbox-coder pattern) and all broker tools at the
# gateway. Idempotent: only adds what is missing, so it also finishes off the template's coder.
sandbox_add() {
  local A="$1" SECRET PROXY KEY GATEWAY_ENV=""
  SECRET="$(sandbox_var "$A")"; PROXY="$(sandbox_var "$A" PROXY_)"
  # Two different secrets: gateway -> sandbox, and sandbox -> egress proxy.
  [ -n "$(env_get "$SECRET")" ] || env_set "$SECRET" "$(openssl rand -hex 32)"
  [ -n "$(env_get "$PROXY")" ] || env_set "$PROXY" "$(openssl rand -hex 32)"

  grep -qx "  sbx-$A:" "$COMPOSE" || append_to_section networks "  sbx-$A:"$'\n'"    internal: true"
  grep -qx "  sandbox-$A-cache: {}" "$COMPOSE" || append_to_section volumes "  sandbox-$A-cache: {}"
  A="$A" compose_awk '/^    networks: \[internal, external/ && index($0, "sbx-" ENVIRON["A"] "]") == 0 &&
    index($0, "sbx-" ENVIRON["A"] ",") == 0 { sub(/\]$/, ", sbx-" ENVIRON["A"] "]") } { print }'

  # Gateway settings, before the gateway's ports: (it is the only service that has any).
  for KEY in "ToolBroker__Agents__${A}__AllTools: \"true\"" \
             "ToolBroker__Agents__${A}__SandboxUrl: http://sandbox-$A:8090" \
             "ToolBroker__Agents__${A}__SandboxSecret: \${$SECRET:-}" \
             "Egress__Sandboxes__${A}__Secret: \${$PROXY:-}"; do
    grep -q "^      ${KEY%%:*}:" "$COMPOSE" || GATEWAY_ENV+="      $KEY"$'\n'
  done
  [ -z "$GATEWAY_ENV" ] || L="$GATEWAY_ENV" compose_awk '$0 == "    ports:" && !done {
      printf "%s", ENVIRON["L"]; done = 1 } { print }'

  if ! grep -qx "  sandbox-$A:" "$COMPOSE"; then
    [ -z "$(tail -c1 "$COMPOSE")" ] || echo >>"$COMPOSE"
    cat >>"$COMPOSE" <<YAML

  sandbox-$A:
    # $A's sandbox: its own workspace (rw) and cache, the skill library (ro, so skill scripts can
    # run through sandbox_run), its own network, egress only through the gateway's proxy with its
    # own credential.
    <<: *sandbox
    environment:
      SANDBOX_SECRET: \${$SECRET:-}
      HTTP_PROXY: http://sbx-$A:\${$PROXY:-}@gateway:8082
      HTTPS_PROXY: http://sbx-$A:\${$PROXY:-}@gateway:8082
      NO_PROXY: ""
    networks: [sbx-$A]
    volumes:
      - ./agents/$A/workspace:/workspace
      - sandbox-$A-cache:/cache
      - ./skills:/skills:ro
YAML
  fi
}

# Adds every broker tool to an agent.json's "tools" array (the file's last "  ]").
add_broker_tools() {
  local FILE="$1" tmp
  grep -q '"http_fetch"' "$FILE" && return 0
  tmp="$(mktemp)"
  T="${BROKER_TOOLS[*]}" awk '
    /^  \]/ && prev ~ /"$/ { print prev ","; n = split(ENVIRON["T"], t, " ")
      for (i = 1; i <= n; i++) print "    \"" t[i] "\"" (i < n ? "," : ""); prev = $0; next }
    NR > 1 { print prev } { prev = $0 } END { print prev }' "$FILE" >"$tmp"
  cat "$tmp" >"$FILE"; rm -f "$tmp"
}

echo "SharpClaw quick start. Press Enter to accept the [default]."
echo

# --- 1. Where ---
if [ -n "${1:-}" ]; then DEST="$1"; else ask DEST "Install location" "$HOME/sharpclaw"; fi
DEST="${DEST/#\~/$HOME}"
ENV="$DEST/.env"
COMPOSE="$DEST/compose.yml"
FRESH=true; [ -f "$COMPOSE" ] && FRESH=false

# Agents that already exist in this install (have a token) are kept as they are.
EXISTING=()
for A in $(compose_agents); do [ -n "$(env_get "$(token_var "$A")")" ] && EXISTING+=("$A"); done
if [ ${#EXISTING[@]} -gt 0 ]; then echo "Existing install with agents: ${EXISTING[*]}"; fi

# --- 2. Provider ---
echo
echo "Model provider:"
echo "  1) Fake        canned replies, no account or cost: to try SharpClaw out"
echo "  2) OpenRouter  real models, needs an OpenRouter API key (https://openrouter.ai/keys)"
CURRENT=1; [ "$(env_get PROVIDER_TYPE)" = "OpenRouter" ] && [ -n "$(env_get OPENROUTER_API_KEY)" ] && CURRENT=2
while :; do
  ask CHOICE "Choose 1 or 2" "$CURRENT"
  case "${CHOICE,,}" in
    1|fake) PROVIDER=Fake; break ;;
    2|openrouter) PROVIDER=OpenRouter; break ;;
    *) echo "  Please enter 1 or 2." ;;
  esac
done

API_KEY=""
MODEL="fake/model"
if [ "$PROVIDER" = OpenRouter ]; then
  HAVE_KEY="$(env_get OPENROUTER_API_KEY)"
  while :; do
    if [ -n "$HAVE_KEY" ]; then
      ask_secret API_KEY "OpenRouter API key (Enter keeps the current one)"
      API_KEY="${API_KEY:-$HAVE_KEY}"
    else
      ask_secret API_KEY "OpenRouter API key"
    fi
    [ -n "$API_KEY" ] || { echo "  The key is required for OpenRouter (or choose Fake)."; continue; }
    # A quick check so a typo fails now rather than on the first chat.
    STATUS="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 \
      -H "Authorization: Bearer $API_KEY" https://openrouter.ai/api/v1/key || true)"
    case "$STATUS" in
      200) echo "  Key accepted."; break ;;
      401|403) echo "  OpenRouter rejected that key; please try again." ; HAVE_KEY="" ;;
      *) echo "  Couldn't reach OpenRouter to check the key (HTTP ${STATUS:-none}); using it anyway."; break ;;
    esac
  done
  ask MODEL "Model for new agents (any OpenRouter model id)" "$DEFAULT_MODEL"
fi

# --- 3. Agents ---
echo
NEW=()
# Agents in an existing compose.yml with no token yet are finished off rather than dropped.
if ! $FRESH; then
  for A in $(compose_agents); do contains "$A" "${EXISTING[@]}" || NEW+=("$A"); done
  [ ${#NEW[@]} -gt 0 ] && echo "compose.yml also defines ${NEW[*]} without a token; they will be created."
fi
echo "Agents: each one gets its own container, persona (soul.md), tool policy and workspace."
while :; do
  if [ $((${#EXISTING[@]} + ${#NEW[@]})) -eq 0 ]; then
    ask NAME "Name of your first agent" "assistant"
  else
    ask NAME "Add another agent? Name, or Enter when you're done" ""
    [ -n "$NAME" ] || break
  fi
  NAME="$(echo "$NAME" | tr 'A-Z' 'a-z')"
  if ! [[ "$NAME" =~ $NAME_RE ]]; then
    echo "  2-32 characters: lowercase letters, digits and '-', starting with a letter or digit."
  elif contains "$NAME" "${EXISTING[@]}" "${NEW[@]}"; then
    echo "  '$NAME' is already on the list."
  else
    NEW+=("$NAME")
  fi
done

# --- 4. Web UI user (only for a new database) ---
USER_NAME=""
if [ ! -e "$DEST/data/sharpclaw.db" ]; then
  echo
  DEFAULT_USER="$(id -un | tr 'A-Z' 'a-z')"; [[ "$DEFAULT_USER" =~ $USER_RE ]] || DEFAULT_USER=admin
  while :; do
    ask USER_NAME "Web UI user name" "$DEFAULT_USER"
    USER_NAME="$(echo "$USER_NAME" | tr 'A-Z' 'a-z')"
    [[ "$USER_NAME" =~ $USER_RE ]] && break
    echo "  2-32 characters: lowercase letters, digits, '.', '_' and '-'."
  done
fi

echo
echo "Ready to set up:"
echo "  install:  $DEST"
echo "  provider: $PROVIDER${API_KEY:+ (model $MODEL for new agents, \$$BUDGET/day each)}"
[ ${#EXISTING[@]} -gt 0 ] && echo "  keep:     ${EXISTING[*]}"
[ ${#NEW[@]} -gt 0 ] && echo "  create:   ${NEW[*]}"
[ -n "$USER_NAME" ] && echo "  user:     $USER_NAME (a password is generated and shown at the end)"
ask GO "Continue? (Y/n)" "Y"
[[ "${GO,,}" =~ ^y ]] || { echo "Nothing changed."; exit 0; }
echo

# Noisy steps (image builds, compose progress) go to a log that is shown only if they fail.
LOG="$(mktemp -t sharpclaw-quickstart.XXXXXX.log)"
quiet() {
  if ! "$@" >>"$LOG" 2>&1; then
    echo "quickstart: '$*' failed; last lines of $LOG:" >&2
    tail -n 30 "$LOG" >&2
    exit 1
  fi
}

echo "==> Building images (the first build takes a few minutes; later ones are cached)"
quiet "$REPO/scripts/build-images.sh"

echo "==> Creating the install in $DEST"
"$REPO/scripts/install.sh" "$DEST" | grep -v '^$' | sed 's/^/    /'
DEST="$(cd "$DEST" && pwd)"; ENV="$DEST/.env"; COMPOSE="$DEST/compose.yml"

env_set PROVIDER_TYPE "$PROVIDER"
[ -n "$API_KEY" ] && env_set OPENROUTER_API_KEY "$API_KEY"
[ -n "${CLIENT_PORT:-}" ] && env_set CLIENT_PORT "$CLIENT_PORT"

# A fresh install starts with the template's sample agents; drop the ones you didn't pick.
if $FRESH; then
  for A in $(compose_agents); do
    contains "$A" "${NEW[@]}" && continue
    # The agent's service, and its sandbox if the template gave it one.
    remove_block "  agent-$A:"
    sandbox_remove "$A"
    env_del "$(token_var "$A")"
    # Only the template's untouched sample: never a workspace with files in it.
    [ -n "$(ls -A "$DEST/agents/$A/workspace" 2>/dev/null)" ] || rm -rf "${DEST:?}/agents/$A"
  done
fi

ADDED_SANDBOX=()
# Each new agent: config (the template's own, or a copy of the assistant's renamed), workspace,
# token line and compose service.
for A in "${NEW[@]}"; do
  if [ ! -d "$DEST/agents/$A/config" ]; then
    mkdir -p "$DEST/agents/$A/config"
    SRC="$REPO/docker/template/agents/$A/config"
    if [ -d "$SRC" ]; then cp "$SRC"/* "$DEST/agents/$A/config/"
    else
      TITLE="$(echo "${A:0:1}" | tr 'a-z' 'A-Z')${A:1}"
      cp "$REPO/docker/template/agents/assistant/config/agent.json" "$DEST/agents/$A/config/"
      sed "1s/.*/# $TITLE/" "$REPO/docker/template/agents/assistant/config/soul.md" \
        >"$DEST/agents/$A/config/soul.md"
    fi
    add_broker_tools "$DEST/agents/$A/config/agent.json"
  fi
  mkdir -p "$DEST/agents/$A/workspace"
  VAR="$(token_var "$A")"
  [ -n "$(grep -E "^$VAR=" "$ENV" || true)" ] || env_set "$VAR" ""
  if ! contains "$A" $(compose_agents); then
    [ -z "$(tail -c1 "$COMPOSE")" ] || echo >>"$COMPOSE"
    cat >>"$COMPOSE" <<YAML

  agent-$A:
    <<: *agent
    environment:
      GATEWAY_URL: http://gateway:8081
      AGENT_TOKEN: \${$VAR}
      AGENT_CONFIG_PATH: /config/agent.json
    volumes:
      - ./agents/$A/config:/config:ro
      - ./agents/$A/workspace:/workspace
      - ./skills:/skills:ro
YAML
  fi
done

# Every agent gets its own sandbox and, at the gateway, all broker tools (AllTools). Agents that
# existed before keep their agent.json: add broker tools there yourself to use them.
for A in $(compose_agents); do
  grep -qx "  sandbox-$A:" "$COMPOSE" && grep -q "^      ToolBroker__Agents__${A}__AllTools:" "$COMPOSE" && continue
  sandbox_add "$A"
  contains "$A" "${NEW[@]}" || ADDED_SANDBOX+=("$A")
done
PROFILES="$(env_get COMPOSE_PROFILES)"
[[ ",$PROFILES," == *,sandboxes,* ]] || env_set COMPOSE_PROFILES "${PROFILES:+$PROFILES,}sandboxes"

PORT="$(env_get CLIENT_PORT)"; PORT="${PORT:-8080}"
BASE="http://127.0.0.1:$PORT"
AUTH=(-H "Authorization: Bearer $(env_get ADMIN_KEY)" -H "Content-Type: application/json")

cd "$DEST"
echo "==> Starting the gateway ($PROVIDER provider) on $BASE"
quiet docker compose up -d --wait gateway

for A in "${NEW[@]}"; do
  BODY="$(curl -sS -w '\n%{http_code}' "${AUTH[@]}" -X POST "$BASE/api/agents" \
    -d "{\"name\":\"$A\",\"model\":\"$MODEL\",\"dailyBudgetUsd\":$BUDGET}")"
  if [ "$(echo "$BODY" | tail -n1)" = "409" ]; then
    # The agent exists in the database but .env lost its token: issue a fresh one.
    BODY="$(curl -sS -w '\n%{http_code}' "${AUTH[@]}" -X POST "$BASE/api/agents/$A/tokens" -d '{}')"
  fi
  TOKEN="$(echo "$BODY" | grep -oE '"value":"sc_[^"]+"' | cut -d'"' -f4 || true)"
  [ -n "$TOKEN" ] || { echo "quickstart: could not create agent '$A': $(echo "$BODY" | head -n1)" >&2; exit 1; }
  env_set "$(token_var "$A")" "$TOKEN"
  echo "    agent '$A' created (model $MODEL, \$$BUDGET/day)"
done

# Switching to OpenRouter: agents still on the fake model get the real one.
if [ "$PROVIDER" = OpenRouter ]; then
  for A in "${EXISTING[@]}"; do
    if curl -sS "${AUTH[@]}" "$BASE/api/agents/$A" | grep -q '"model":"fake/model"'; then
      curl -fsS "${AUTH[@]}" -X PATCH "$BASE/api/agents/$A" -d "{\"model\":\"$MODEL\"}" >/dev/null
      echo "    agent '$A' switched to $MODEL"
    fi
  done
fi

PASSWORD=""
if [ "$(curl -sS "${AUTH[@]}" "$BASE/api/users" | grep -c '"name"' || true)" = "0" ]; then
  if [ -z "$USER_NAME" ]; then USER_NAME="$(id -un | tr 'A-Z' 'a-z')"; [[ "$USER_NAME" =~ $USER_RE ]] || USER_NAME=admin; fi
  PASSWORD="$(openssl rand -base64 18)"
  curl -fsS "${AUTH[@]}" -X POST "$BASE/api/users" \
    -d "{\"name\":\"$USER_NAME\",\"password\":\"$PASSWORD\"}" >/dev/null
fi

echo "==> Starting the agents"
quiet docker compose up -d --remove-orphans
rm -f "$LOG"

echo
echo "SharpClaw is running: open $BASE"
if [ -n "$PASSWORD" ]; then
  echo "  user:     $USER_NAME"
  echo "  password: $PASSWORD   (shown once; change it under Account in the web UI)"
fi
echo "  agents:   $(compose_agents | xargs) (each with its own sandbox and every broker tool)"
[ ${#ADDED_SANDBOX[@]} -gt 0 ] && echo "  Sandbox added for ${ADDED_SANDBOX[*]}: list the broker tools in agents/<name>/config/agent.json to use them."
[ "$PROVIDER" = Fake ] && echo "  Fake provider: re-run this script and choose OpenRouter when you want real models."
echo "  Install:  $DEST (compose.yml, .env, agents/<name>/config/soul.md for each persona, data/)"
