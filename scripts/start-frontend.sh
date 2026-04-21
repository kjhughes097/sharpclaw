#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# start-frontend.sh — Run the SharpClaw Web frontend in development mode.
#
# Starts the CRA dev server on port 3000, proxying /api to localhost:5100.
#
# Usage:
#   ./scripts/start-frontend.sh
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WEB_DIR="$REPO_DIR/src/SharpClaw.Web"

# Install dependencies if needed
if [[ ! -d "$WEB_DIR/node_modules" ]]; then
  echo "==> Installing npm dependencies..."
  npm --prefix "$WEB_DIR" ci --no-audit --no-fund
fi

echo "==> Starting SharpClaw Web (Development) on http://localhost:3000"
exec npm --prefix "$WEB_DIR" start
