#!/usr/bin/env bash
# Verifies the SharpClaw isolation guarantees against a running stack.
# Run from an install directory (e.g. ~/sharpclaw, or docker/.dev) after `docker compose up -d`.
set -uo pipefail

PROJECT="$(docker compose config | sed -n 's/^name: //p')"
[ -n "$PROJECT" ] || { echo "Run this from a directory with a compose.yml" >&2; exit 2; }
NET="${PROJECT}_internal"
CURL_IMAGE="curlimages/curl:latest"
FAILURES=0

pass() { echo "PASS: $1"; }
fail() { echo "FAIL: $1"; FAILURES=$((FAILURES + 1)); }

echo "== 1a. Agent network has no route to the public internet =="
if docker run --rm --network "$NET" "$CURL_IMAGE" -sS --max-time 8 https://example.com >/dev/null 2>&1; then
  fail "a container on $NET reached https://example.com (should be blocked)"
else
  pass "public internet is unreachable from $NET"
fi

echo "== 1b. Gateway agent listener is reachable from the internal network =="
if docker run --rm --network "$NET" "$CURL_IMAGE" -sS --max-time 8 http://gateway:8081/healthz | grep -q '"status":"ok"'; then
  pass "http://gateway:8081/healthz is reachable from $NET"
else
  fail "http://gateway:8081/healthz was NOT reachable from $NET"
fi

echo "== 2. Agent port 8081 is NOT published to the host =="
if curl -sS --max-time 3 http://127.0.0.1:8081/healthz >/dev/null 2>&1; then
  fail "http://127.0.0.1:8081 is reachable from the host (port must not be published)"
else
  pass "http://127.0.0.1:8081 is not reachable from the host"
fi

echo "== 3a. Client routes return 404 on the agent port =="
CODE=$(docker run --rm --network "$NET" "$CURL_IMAGE" -s -o /dev/null -w "%{http_code}" --max-time 8 http://gateway:8081/api/agents)
if [ "$CODE" = "404" ]; then
  pass "GET /api/agents on :8081 returned 404"
else
  fail "GET /api/agents on :8081 returned $CODE (expected 404)"
fi

echo "== 3b. Agent routes return 404 on the client port =="
CODE=$(docker run --rm --network "$NET" "$CURL_IMAGE" -s -o /dev/null -w "%{http_code}" --max-time 8 http://gateway:8080/agent/inbox)
if [ "$CODE" = "404" ]; then
  pass "GET /agent/inbox on :8080 returned 404"
else
  fail "GET /agent/inbox on :8080 returned $CODE (expected 404)"
fi

echo "== 4. Agent container root filesystem is read-only and /config is not writable =="
for SVC in $(docker compose config --services | grep '^agent-'); do
  if docker compose exec -T "$SVC" sh -c 'touch /rootfs-write-test' 2>/dev/null; then
    fail "$SVC root filesystem is writable"
  else
    pass "$SVC root filesystem is read-only"
  fi
  if docker compose exec -T "$SVC" sh -c 'touch /config/write-test' 2>/dev/null; then
    fail "$SVC /config is writable"
  else
    pass "$SVC /config is not writable"
  fi
done

echo "== 4b. /skills is mounted read-only in each agent and is not under /workspace (skills spec) =="
for SVC in $(docker compose config --services | grep '^agent-'); do
  CID=$(docker compose ps -q "$SVC")
  if [ -z "$CID" ]; then
    echo "($SVC is not running: skipping its /skills checks)"
    continue
  fi
  SKILLS_SRC=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/skills"}}{{.Source}}{{end}}{{end}}' "$CID")
  SKILLS_RW=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/skills"}}{{.RW}}{{end}}{{end}}' "$CID")
  WS_SRC=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/workspace"}}{{.Source}}{{end}}{{end}}' "$CID")
  if [ -z "$SKILLS_SRC" ]; then
    fail "$SVC has no /skills mount"
    continue
  fi
  if [ "$SKILLS_RW" = "false" ] && ! docker compose exec -T "$SVC" sh -c 'touch /skills/write-test' 2>/dev/null; then
    pass "$SVC /skills is read-only"
  else
    fail "$SVC /skills is writable"
  fi
  # Neither directory may contain the other on the host, and nothing is mounted inside /workspace.
  case "$SKILLS_SRC/" in "$WS_SRC"/*) INSIDE=1 ;; *) INSIDE=0 ;; esac
  case "$WS_SRC/" in "$SKILLS_SRC"/*) INSIDE=1 ;; esac
  NESTED=$(docker inspect -f '{{range .Mounts}}{{.Destination}} {{end}}' "$CID" | xargs -n1 | grep '^/workspace/' || true)
  if [ "$INSIDE" -eq 0 ] && [ -z "$NESTED" ]; then
    pass "$SVC /skills is not under /workspace"
  else
    fail "$SVC /skills and /workspace overlap (skills: $SKILLS_SRC, workspace: $WS_SRC, nested: $NESTED)"
  fi
done

# ----- External tools: the egress proxy and sandboxes (external tools spec section 10) -----

# Secrets for the proxy checks come from the install's .env.
if [ -f .env ]; then set -a; . ./.env; set +a; fi
curl_on() { local NETWORK="$1"; shift; docker run --rm --network "$NETWORK" "$CURL_IMAGE" -s --max-time 10 "$@"; }
upper() { echo "$1" | tr 'a-z-' 'A-Z_'; }
SANDBOXES=$(docker compose ps --services --status running | sed -n 's/^sandbox-//p')

echo "== 5a. Egress port 8082 is NOT published to the host =="
if curl -sS --max-time 3 http://127.0.0.1:8082 >/dev/null 2>&1; then
  fail "127.0.0.1:8082 is reachable from the host (the egress port must not be published)"
else
  pass "127.0.0.1:8082 is not reachable from the host"
fi

if [ -z "$SANDBOXES" ]; then
  echo "(no sandbox-* services running: skipping sandbox checks; start them with COMPOSE_PROFILES=sandboxes)"
else
  echo "== 5b. Agents get 407 from the egress proxy (they have no proxy credential) =="
  CODE=$(curl_on "$NET" -o /dev/null -w "%{http_connect}" -x http://gateway:8082 https://example.com)
  if [ "$CODE" = "407" ]; then pass "CONNECT via gateway:8082 from $NET returned 407"; else fail "CONNECT via gateway:8082 from $NET returned '$CODE' (expected 407)"; fi

  for N in $SANDBOXES; do
    SBX_NET="${PROJECT}_sbx-$N"
    SECRET_VAR="SANDBOX_$(upper "$N")_PROXY_SECRET"
    PROXY="http://sbx-$N:${!SECRET_VAR:-}@gateway:8082"
    CID=$(docker compose ps -q "sandbox-$N")

    echo "== 6. sandbox-$N =="
    if curl_on "$NET" -o /dev/null http://sandbox-$N:8090/healthz; then
      fail "an agent-network container reached sandbox-$N"
    else
      pass "sandbox-$N is unreachable from the agent network"
    fi

    NETS=$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' "$CID" | xargs)
    if [ "$NETS" = "$SBX_NET" ]; then pass "sandbox-$N is only on $SBX_NET"; else fail "sandbox-$N is on: $NETS (expected only $SBX_NET)"; fi

    if curl_on "$SBX_NET" -o /dev/null --noproxy '*' https://example.com; then
      fail "$SBX_NET reached https://example.com without the proxy"
    else
      pass "$SBX_NET has no direct route to the internet"
    fi

    CODE=$(curl_on "$SBX_NET" -o /dev/null -w "%{http_code}" -x "$PROXY" https://example.com)
    if [ "$CODE" = "200" ]; then pass "sandbox-$N credential reaches https://example.com through the proxy"; else fail "proxy fetch of https://example.com returned '$CODE' (expected 200)"; fi

    for TARGET in http://169.254.169.254/latest/meta-data/ http://gateway/ http://192.168.1.1/ http://10.0.0.1/; do
      CODE=$(curl_on "$SBX_NET" -o /dev/null -w "%{http_code}" -x "$PROXY" "$TARGET")
      if [ "$CODE" = "403" ]; then pass "proxy refuses $TARGET"; else fail "proxy answered '$CODE' for $TARGET (expected 403)"; fi
    done
    CODE=$(curl_on "$SBX_NET" -o /dev/null -w "%{http_connect}" -x "$PROXY" https://gateway:8080/)
    if [ "$CODE" = "403" ]; then pass "proxy refuses CONNECT gateway:8080"; else fail "CONNECT gateway:8080 returned '$CODE' (expected 403)"; fi

    for URL in http://gateway:8080/api/agents http://gateway:8081/agent/inbox; do
      CODE=$(curl_on "$SBX_NET" -o /dev/null -w "%{http_code}" "$URL")
      if [ "$CODE" = "401" ]; then pass "$URL from $SBX_NET needs credentials (401)"; else fail "$URL from $SBX_NET returned '$CODE' (expected 401)"; fi
    done

    for M in $SANDBOXES; do
      [ "$M" = "$N" ] && continue
      if curl_on "$SBX_NET" -o /dev/null http://sandbox-$M:8090/healthz; then
        fail "sandbox-$N's network reached sandbox-$M"
      else
        pass "sandbox-$N can't reach sandbox-$M"
      fi
    done

    MOUNTS=$(docker inspect -f '{{range .Mounts}}{{.Destination}}={{.Source}} {{end}}' "$CID" | xargs -n1 | sort | xargs)
    BAD=$(echo "$MOUNTS" | xargs -n1 | grep -vE "^/workspace=.*/agents/$N/workspace$|^/cache=|^/skills=" || true)
    if [ -z "$BAD" ] && echo "$MOUNTS" | grep -qE "/workspace=.*/agents/$N/workspace"; then
      pass "sandbox-$N mounts only its own workspace (and its cache and the skill library)"
    else
      fail "sandbox-$N mounts: $MOUNTS"
    fi

    # The skill library is optional in a sandbox, but read-only when it is there.
    SKILLS_RW=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/skills"}}{{.RW}}{{end}}{{end}}' "$CID")
    if [ -n "$SKILLS_RW" ]; then
      if [ "$SKILLS_RW" = "false" ] && ! docker compose exec -T "sandbox-$N" sh -c 'touch /skills/write-test' 2>/dev/null; then
        pass "sandbox-$N /skills is read-only"
      else
        fail "sandbox-$N /skills is writable"
      fi
    fi

    if docker compose exec -T "sandbox-$N" sh -c 'touch /rootfs-write-test' 2>/dev/null; then
      fail "sandbox-$N root filesystem is writable"
    else
      pass "sandbox-$N root filesystem is read-only"
    fi
  done
fi

echo
if [ "$FAILURES" -eq 0 ]; then
  echo "All isolation checks passed."
  exit 0
else
  echo "$FAILURES isolation check(s) failed."
  exit 1
fi
