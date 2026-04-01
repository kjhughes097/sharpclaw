#!/usr/bin/env bash
# start-frontend.sh
#
# Starts the SharpClaw React + Vite frontend dev server locally without Docker.
#
# Prerequisites:
#   - Node.js 18+ and npm installed (scripts/install-linux.sh installs Node.js 22 LTS)
#   - The backend API is already running (scripts/start-backend.sh)
#
# Usage:
#   ./scripts/start-frontend.sh
#
# The Vite dev server proxies /api/ requests to http://localhost:5000 by
# default.  Override the backend URL with the VITE_API_URL environment
# variable if your backend listens on a different port.
#
# The dev server is available on http://localhost:5173 by default.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WEB_DIR="$REPO_ROOT/SharpClaw.Web"

# ── Colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

info()  { echo -e "${GREEN}[frontend]${NC} $*"; }
warn()  { echo -e "${YELLOW}[frontend]${NC} $*"; }
error() { echo -e "${RED}[frontend]${NC} $*" >&2; }
die()   { error "$*"; exit 1; }

# ── Validate node/npm ─────────────────────────────────────────────────────────
if ! command -v node &>/dev/null; then
    die "Node.js not found. Run scripts/install-linux.sh first."
fi
if ! command -v npm &>/dev/null; then
    die "npm not found. Run scripts/install-linux.sh first."
fi

NODE_MAJOR="$(node --version | cut -d. -f1 | tr -d 'v')"
if [[ "$NODE_MAJOR" -lt 18 ]]; then
    die "Node.js 18 or later is required. Found $(node --version). Run scripts/install-linux.sh to upgrade."
fi

info "node : $(node --version)"
info "npm  : $(npm --version)"

# ── Install npm dependencies if needed ────────────────────────────────────────
cd "$WEB_DIR"

if [[ ! -d node_modules ]]; then
    info "node_modules not found – running npm ci..."
    npm ci
else
    info "node_modules already present – skipping npm ci."
    info "(Run 'npm ci' manually in SharpClaw.Web if you need a clean install.)"
fi

# ── Start Vite dev server ─────────────────────────────────────────────────────
info ""
info "Starting Vite dev server (Ctrl-C to stop)..."
info "Frontend will be available on http://localhost:5173"
info "API requests are proxied to the backend (see SharpClaw.Web/vite.config.ts)"
info ""

exec npm run dev
