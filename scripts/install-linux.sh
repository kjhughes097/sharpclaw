#!/usr/bin/env bash
# install-linux.sh
#
# Installs the prerequisites needed to run SharpClaw locally on Linux
# without Docker.  Supported distributions:
#   - Debian / Ubuntu (apt)
#   - RHEL / Fedora / CentOS Stream (dnf)
#
# What this script does:
#   1. Installs .NET 10 SDK
#   2. Installs Node.js 22 LTS and npm
#   3. Installs PostgreSQL 16
#   4. Creates the PostgreSQL role and database defined in .env
#      (reads POSTGRES_USER, POSTGRES_PASSWORD, POSTGRES_DB from .env if present)
#
# Usage:
#   chmod +x scripts/install-linux.sh
#   sudo ./scripts/install-linux.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# ── Colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

info()    { echo -e "${GREEN}[install]${NC} $*"; }
warn()    { echo -e "${YELLOW}[install]${NC} $*"; }
error()   { echo -e "${RED}[install]${NC} $*" >&2; }
die()     { error "$*"; exit 1; }

# ── Require root ─────────────────────────────────────────────────────────────
if [[ "$EUID" -ne 0 ]]; then
    die "This script must be run as root (or with sudo)."
fi

# ── Detect package manager ────────────────────────────────────────────────────
if command -v apt-get &>/dev/null; then
    PKG_MANAGER="apt"
elif command -v dnf &>/dev/null; then
    PKG_MANAGER="dnf"
else
    die "Unsupported package manager. Only apt (Debian/Ubuntu) and dnf (RHEL/Fedora) are supported."
fi

info "Detected package manager: $PKG_MANAGER"

# ── Load .env for database settings ──────────────────────────────────────────
ENV_FILE="$REPO_ROOT/.env"

if [[ -f "$ENV_FILE" ]]; then
    info "Loading database settings from $ENV_FILE"
    # shellcheck disable=SC1090
    set -o allexport
    source "$ENV_FILE"
    set +o allexport
else
    die ".env not found at $ENV_FILE.
Copy .env.example to .env and fill in the required values before running this script:
  cp .env.example .env && \$EDITOR .env"
fi

[[ -n "${POSTGRES_USER:-}" ]]     || die "POSTGRES_USER is not set in $ENV_FILE."
[[ -n "${POSTGRES_PASSWORD:-}" ]] || die "POSTGRES_PASSWORD is not set in $ENV_FILE."
[[ -n "${POSTGRES_DB:-}" ]]       || die "POSTGRES_DB is not set in $ENV_FILE."

# ── Install .NET 10 SDK ───────────────────────────────────────────────────────
install_dotnet_apt() {
    info "Installing .NET 10 SDK (apt)..."
    apt-get update -y

    # Use Microsoft's official script for reliability across Ubuntu/Debian versions.
    if ! command -v dotnet &>/dev/null || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
        local tmp_pkg
        tmp_pkg="$(mktemp /tmp/dotnet-install.XXXXXX.sh)"
        if command -v curl &>/dev/null; then
            curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp_pkg"
        elif command -v wget &>/dev/null; then
            wget -qO "$tmp_pkg" https://dot.net/v1/dotnet-install.sh
        else
            # Fall back to apt package if network tools are unavailable.
            apt-get install -y dotnet-sdk-10.0 || die "Failed to install .NET 10 SDK."
            return
        fi
        chmod +x "$tmp_pkg"
        DOTNET_INSTALL_DIR="/usr/local/share/dotnet"
        "$tmp_pkg" --channel 10.0 --install-dir "$DOTNET_INSTALL_DIR"
        rm -f "$tmp_pkg"

        # Ensure dotnet is on PATH system-wide.
        if ! grep -qF "$DOTNET_INSTALL_DIR" /etc/environment 2>/dev/null; then
            CURRENT_PATH="$(grep '^PATH=' /etc/environment 2>/dev/null | sed 's/^PATH=["'"'"']*//' | sed 's/["'"'"']*$//')"
            if [[ -n "$CURRENT_PATH" ]]; then
                sed -i "s|^PATH=.*|PATH=\"$DOTNET_INSTALL_DIR:$CURRENT_PATH\"|" /etc/environment
            else
                echo "PATH=\"$DOTNET_INSTALL_DIR:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin\"" >> /etc/environment
            fi
        fi
        export PATH="$DOTNET_INSTALL_DIR:$PATH"
    else
        info ".NET 10 SDK already installed."
    fi
}

install_dotnet_dnf() {
    info "Installing .NET 10 SDK (dnf)..."
    dnf install -y dotnet-sdk-10.0 || {
        warn "dotnet-sdk-10.0 not available in default repos; using Microsoft install script."
        local tmp_pkg
        tmp_pkg="$(mktemp /tmp/dotnet-install.XXXXXX.sh)"
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp_pkg"
        chmod +x "$tmp_pkg"
        DOTNET_INSTALL_DIR="/usr/local/share/dotnet"
        "$tmp_pkg" --channel 10.0 --install-dir "$DOTNET_INSTALL_DIR"
        rm -f "$tmp_pkg"
        export PATH="$DOTNET_INSTALL_DIR:$PATH"
        echo "export PATH=\"$DOTNET_INSTALL_DIR:\$PATH\"" > /etc/profile.d/dotnet.sh
    }
}

if [[ "$PKG_MANAGER" == "apt" ]]; then
    install_dotnet_apt
else
    install_dotnet_dnf
fi

if command -v dotnet &>/dev/null; then
    info "dotnet version: $(dotnet --version)"
else
    warn "dotnet not found on current PATH. Open a new shell or source /etc/environment before running the backend."
fi

# ── Install Node.js 22 LTS and npm ────────────────────────────────────────────
install_node_apt() {
    info "Installing Node.js 22 LTS (apt)..."
    if ! command -v node &>/dev/null || [[ "$(node --version 2>/dev/null | cut -d. -f1 | tr -d 'v')" -lt 18 ]]; then
        apt-get install -y curl ca-certificates gnupg
        curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
        apt-get install -y nodejs
    else
        info "Node.js $(node --version) already installed."
    fi
}

install_node_dnf() {
    info "Installing Node.js 22 LTS (dnf)..."
    if ! command -v node &>/dev/null || [[ "$(node --version 2>/dev/null | cut -d. -f1 | tr -d 'v')" -lt 18 ]]; then
        curl -fsSL https://rpm.nodesource.com/setup_22.x | bash -
        dnf install -y nodejs
    else
        info "Node.js $(node --version) already installed."
    fi
}

if [[ "$PKG_MANAGER" == "apt" ]]; then
    install_node_apt
else
    install_node_dnf
fi

info "node version : $(node --version)"
info "npm version  : $(npm --version)"

# ── Install PostgreSQL 16 ─────────────────────────────────────────────────────
install_postgres_apt() {
    info "Installing PostgreSQL 16 (apt)..."
    if ! command -v psql &>/dev/null; then
        apt-get install -y curl ca-certificates lsb-release
        install -d /usr/share/postgresql-common/pgdg
        curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
            -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
        . /etc/os-release
        echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] \
https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
            > /etc/apt/sources.list.d/pgdg.list
        apt-get update -y
        apt-get install -y postgresql-16
    else
        info "PostgreSQL already installed: $(psql --version)"
    fi

    systemctl enable postgresql || true
    systemctl start postgresql  || true
}

install_postgres_dnf() {
    info "Installing PostgreSQL 16 (dnf)..."
    if ! command -v psql &>/dev/null; then
        dnf install -y https://download.postgresql.org/pub/repos/yum/reporpms/EL-$(rpm -E %{rhel})-x86_64/pgdg-redhat-repo-latest.noarch.rpm
        dnf -qy module disable postgresql 2>/dev/null || true
        dnf install -y postgresql16-server
        /usr/pgsql-16/bin/postgresql-16-setup initdb
        systemctl enable postgresql-16
        systemctl start postgresql-16
    else
        info "PostgreSQL already installed: $(psql --version)"
    fi
}

if [[ "$PKG_MANAGER" == "apt" ]]; then
    install_postgres_apt
else
    install_postgres_dnf
fi

# ── Create PostgreSQL role and database ───────────────────────────────────────
info "Configuring PostgreSQL role '${POSTGRES_USER}' and database '${POSTGRES_DB}'..."

# Run as the postgres system user.
sudo -u postgres psql -v ON_ERROR_STOP=1 <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = '${POSTGRES_USER}') THEN
    CREATE ROLE "${POSTGRES_USER}" WITH LOGIN PASSWORD '${POSTGRES_PASSWORD}';
    RAISE NOTICE 'Role "${POSTGRES_USER}" created.';
  ELSE
    ALTER ROLE "${POSTGRES_USER}" WITH PASSWORD '${POSTGRES_PASSWORD}';
    RAISE NOTICE 'Role "${POSTGRES_USER}" already exists – password updated.';
  END IF;
END
\$\$;

SELECT 'CREATE DATABASE "${POSTGRES_DB}" OWNER "${POSTGRES_USER}"'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '${POSTGRES_DB}') \gexec
SQL

info "PostgreSQL setup complete."

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}══════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN} SharpClaw prerequisites installed successfully${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════════════════${NC}"
echo ""
echo "Next steps:"
echo "  1. Start the backend:"
echo "       ./scripts/start-backend.sh"
echo ""
echo "  2. Start the frontend dev server (in a separate terminal):"
echo "       ./scripts/start-frontend.sh"
echo ""
echo "  3. (Optional) Run the Telegram worker:"
echo "       export TELEGRAM_BOT_TOKEN=<token>"
echo "       export SHARPCLAW_API_URL=http://localhost:5000"
echo "       export SHARPCLAW_API_KEY=<your-api-key>"
echo "       dotnet run --project SharpClaw.Telegram"
echo ""
echo "  See the 'Running Locally on Linux' section of README.md for details."
echo ""
