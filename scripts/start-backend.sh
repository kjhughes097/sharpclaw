#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# start-backend.sh — Run the SharpClaw API in development mode.
#
# Usage:
#   ./scripts/start-backend.sh
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"

# Load .env if present
if [[ -f "$REPO_DIR/.env" ]]; then
  set -a
  source "$REPO_DIR/.env"
  set +a
fi

echo "==> Starting SharpClaw API (Development)..."
exec dotnet run \
  --project "$REPO_DIR/src/SharpClaw.Api/SharpClaw.Api.csproj" \
  --launch-profile ""
