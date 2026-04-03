#!/usr/bin/env bash
# start-backend.sh
#
# Starts the SharpClaw ASP.NET Core API locally without Docker.
#
# Prerequisites:
#   - .NET 10 SDK installed (run scripts/install-linux.sh first)
#   - PostgreSQL running and the SharpClaw database created
#   - .env file present at the repo root (copy from .env.example and edit)
#
# Usage:
#   ./scripts/start-backend.sh
#
# The script loads environment variables from .env, validates the required
# ones, then starts the API with `dotnet run`.  Pass any additional
# `dotnet run` flags after the script name:
#   ./scripts/start-backend.sh --launch-profile Development

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# ── Colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

info()  { echo -e "${GREEN}[backend]${NC} $*"; }
warn()  { echo -e "${YELLOW}[backend]${NC} $*"; }
error() { echo -e "${RED}[backend]${NC} $*" >&2; }
die()   { error "$*"; exit 1; }

# ── Load .env ─────────────────────────────────────────────────────────────────
ENV_FILE="$REPO_ROOT/.env"
if [[ -f "$ENV_FILE" ]]; then
    info "Loading environment from $ENV_FILE"
    # shellcheck disable=SC1090
    set -o allexport
    source "$ENV_FILE"
    set +o allexport
else
    warn ".env not found at $ENV_FILE"
    warn "Copy .env.example to .env and fill in the required values, or export them manually."
fi

# ── Build the connection string from .env if not already set ──────────────────
# The API accepts SHARPCLAW_DB_CONNECTION or ConnectionStrings__DefaultConnection.
# If neither is set but POSTGRES_* variables are present, construct a connection
# string automatically (matching the docker-compose pattern).
if [[ -z "${SHARPCLAW_DB_CONNECTION:-}" && -z "${ConnectionStrings__DefaultConnection:-}" ]]; then
    if [[ -n "${POSTGRES_DB:-}" && -n "${POSTGRES_USER:-}" && -n "${POSTGRES_PASSWORD:-}" ]]; then
        export SHARPCLAW_DB_CONNECTION="Host=localhost;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
        info "Connection string built from POSTGRES_* variables."
    else
        die "Database connection is not configured.
Set SHARPCLAW_DB_CONNECTION or POSTGRES_DB/POSTGRES_USER/POSTGRES_PASSWORD in .env."
    fi
fi

# ── Validate dotnet is available ──────────────────────────────────────────────
if ! command -v dotnet &>/dev/null; then
    die ".NET SDK not found. Run scripts/install-linux.sh first, then open a new shell."
fi

DOTNET_VERSION="$(dotnet --version 2>/dev/null)"
info "Using .NET SDK $DOTNET_VERSION"

if ! echo "$DOTNET_VERSION" | grep -q '^10\.'; then
    warn "Expected .NET 10 SDK but found $DOTNET_VERSION. The build may fail."
fi

# ── Restore and run ───────────────────────────────────────────────────────────
info "Restoring NuGet packages..."
dotnet restore "$REPO_ROOT/SharpClaw.slnx"

info "Starting SharpClaw API (Ctrl-C to stop)..."
info "API will be available on http://localhost:5000 (or https://localhost:5001)"
info ""
info "Environment:"
info "  SHARPCLAW_DB_CONNECTION : ${SHARPCLAW_DB_CONNECTION:-(not set; using ConnectionStrings__DefaultConnection)}"
info "  SHARPCLAW_WORKSPACE     : ${SHARPCLAW_WORKSPACE:-(not set; defaults to cwd)}"
info "  SHARPCLAW_JWT_SECRET    : ${SHARPCLAW_JWT_SECRET:+(configured)}"
info "  ANTHROPIC_API_KEY       : ${ANTHROPIC_API_KEY:+(configured)}"
info "  OPENAI_API_KEY          : ${OPENAI_API_KEY:+(configured)}"
info "  OPENROUTER_API_KEY      : ${OPENROUTER_API_KEY:+(configured)}"
info "  GITHUB_COPILOT_TOKEN    : ${GITHUB_COPILOT_TOKEN:+(configured)}"
info ""

cd "$REPO_ROOT"
exec dotnet run --project SharpClaw.Api/SharpClaw.Api.csproj "$@"
