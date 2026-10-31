#!/usr/bin/env bash
# Runs a throwaway development stack from this checkout, next to a personal install on :8080.
#   scripts/dev-stack.sh   -> builds :dev images, (re)creates docker/.dev, starts the gateway
# Fake provider, gateway on http://127.0.0.1:8090. Then, for UI work:
#   cd ui && SHARPCLAW_GATEWAY=http://localhost:8090 npm run dev
# Use `docker compose` from docker/.dev for anything else (agents, logs, down -v).
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DEV="$REPO/docker/.dev"

"$REPO/scripts/build-images.sh" --tag dev
"$REPO/scripts/install.sh" "$DEV" >/dev/null

# Always track the template here; this directory holds no hand edits worth keeping.
cp "$REPO/docker/template/compose.yml" "$DEV/compose.yml"
cp "$REPO/docker/compose.dev.yml" "$DEV/compose.override.yml"
sed -i \
  -e 's/^SHARPCLAW_TAG=.*/SHARPCLAW_TAG=dev/' \
  -e 's/^CLIENT_PORT=.*/CLIENT_PORT=8090/' \
  -e 's/^PROVIDER_TYPE=.*/PROVIDER_TYPE=Fake/' \
  "$DEV/.env"
# A .env from before the template's sandbox moved to the assistant lacks its secrets.
for VAR in SANDBOX_ASSISTANT_SECRET SANDBOX_ASSISTANT_PROXY_SECRET; do
  grep -qE "^$VAR=.+" "$DEV/.env" || { sed -i "/^$VAR=/d" "$DEV/.env"; echo "$VAR=$(openssl rand -hex 32)" >>"$DEV/.env"; }
done

cd "$DEV"
docker compose up -d gateway

echo
echo "Dev gateway: http://127.0.0.1:8090 (admin key: ADMIN_KEY in $DEV/.env)"
