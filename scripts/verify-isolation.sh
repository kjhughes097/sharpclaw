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

echo
if [ "$FAILURES" -eq 0 ]; then
  echo "All isolation checks passed."
  exit 0
else
  echo "$FAILURES isolation check(s) failed."
  exit 1
fi
