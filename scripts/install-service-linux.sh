#!/usr/bin/env bash
# install-service-linux.sh
#
# Builds and installs SharpClaw as systemd services on Linux.
# This is the "deploy to local machine as persistent services" path, distinct
# from the interactive dev-server workflow in start-backend.sh /
# start-frontend.sh.
#
# What this script does:
#   1. Validates prerequisites (.NET 10 SDK, Node.js 18+, npm, PostgreSQL)
#   2. Creates a dedicated system user 'sharpclaw'
#   3. Publishes the .NET API to /opt/sharpclaw/api
#   4. Publishes the Telegram worker to /opt/sharpclaw/telegram
#   5. Builds the React frontend and deploys it to /var/www/sharpclaw
#   6. Installs and configures nginx to serve the frontend and proxy /api/
#   7. Writes /etc/sharpclaw/env from your .env values
#   8. Installs and enables sharpclaw-api.service
#   9. Optionally installs and enables sharpclaw-telegram.service
#  10. Enables and restarts nginx
#
# Supported distributions:
#   - Debian / Ubuntu (apt)
#   - RHEL / Fedora / CentOS Stream (dnf)
#
# Prerequisites (installed by scripts/install-linux.sh):
#   - .NET 10 SDK
#   - Node.js 22 LTS and npm
#   - PostgreSQL 16
#
# Usage:
#   chmod +x scripts/install-service-linux.sh
#   sudo ./scripts/install-service-linux.sh [--no-telegram]
#
# Options:
#   --no-telegram   Skip installing the Telegram worker service
#
# To update an already-installed deployment, run the script again.  It stops
# the service, republishes, redeploys, and restarts.

set -euo pipefail

# ── Parse arguments ───────────────────────────────────────────────────────────
ENABLE_TELEGRAM_SERVICE="true"
for arg in "$@"; do
    case "$arg" in
        --no-telegram)
            ENABLE_TELEGRAM_SERVICE="false"
            ;;
        *)
            die "Unknown argument: $arg"
            ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# ── Install paths ─────────────────────────────────────────────────────────────
INSTALL_USER="sharpclaw"
API_INSTALL_DIR="/opt/sharpclaw/api"
TELEGRAM_INSTALL_DIR="/opt/sharpclaw/telegram"
WEB_INSTALL_DIR="/var/www/sharpclaw"
ENV_DIR="/etc/sharpclaw"
ENV_FILE="$ENV_DIR/env"
SERVICE_NAME="sharpclaw-api"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
TELEGRAM_SERVICE_NAME="sharpclaw-telegram"
TELEGRAM_SERVICE_FILE="/etc/systemd/system/${TELEGRAM_SERVICE_NAME}.service"

# ── Colour helpers ────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

info()  { echo -e "${GREEN}[service-install]${NC} $*"; }
warn()  { echo -e "${YELLOW}[service-install]${NC} $*"; }
error() { echo -e "${RED}[service-install]${NC} $*" >&2; }
die()   { error "$*"; exit 1; }

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

# ── Load .env ─────────────────────────────────────────────────────────────────
DOT_ENV="$REPO_ROOT/.env"
if [[ -f "$DOT_ENV" ]]; then
    info "Loading environment from $DOT_ENV"
    # shellcheck disable=SC1090
    set -o allexport
    source "$DOT_ENV"
    set +o allexport
else
    warn ".env not found at $DOT_ENV"
    warn "Copy .env.example to .env and fill in the required values, then re-run."
    warn "Continuing with any values already exported in the environment."
fi

# ── Resolve DB connection string ──────────────────────────────────────────────
if [[ -z "${SHARPCLAW_DB_CONNECTION:-}" && -z "${ConnectionStrings__DefaultConnection:-}" ]]; then
    if [[ -n "${POSTGRES_DB:-}" && -n "${POSTGRES_USER:-}" && -n "${POSTGRES_PASSWORD:-}" ]]; then
        export SHARPCLAW_DB_CONNECTION="Host=localhost;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
        info "SHARPCLAW_DB_CONNECTION derived from POSTGRES_* variables."
    else
        die "Database connection not configured.
Set SHARPCLAW_DB_CONNECTION in .env, or set POSTGRES_DB / POSTGRES_USER / POSTGRES_PASSWORD."
    fi
fi

# ── Validate prerequisites ─────────────────────────────────────────────────────
# dotnet
DOTNET_BIN=""
DOTNET_ROOT=""
if command -v dotnet &>/dev/null; then
    DOTNET_BIN="$(command -v dotnet)"
    # Determine DOTNET_ROOT from the known install locations, falling back to
    # the binary's parent directory for the install-script layout.
    if [[ -d "/usr/local/share/dotnet/sdk" ]]; then
        DOTNET_ROOT="/usr/local/share/dotnet"
    elif [[ -d "/usr/lib/dotnet" ]]; then
        DOTNET_ROOT="/usr/lib/dotnet"
    else
        DOTNET_ROOT="$(dirname "$DOTNET_BIN")"
    fi
elif [[ -x "/usr/local/share/dotnet/dotnet" ]]; then
    DOTNET_BIN="/usr/local/share/dotnet/dotnet"
    DOTNET_ROOT="/usr/local/share/dotnet"
    export PATH="/usr/local/share/dotnet:$PATH"
else
    die ".NET SDK not found. Run scripts/install-linux.sh first."
fi
DOTNET_VERSION="$("$DOTNET_BIN" --version 2>/dev/null)"
info ".NET SDK $DOTNET_VERSION found at $DOTNET_BIN"
if ! echo "$DOTNET_VERSION" | grep -q '^10\.'; then
    warn "Expected .NET 10 SDK but found $DOTNET_VERSION. The build may fail."
fi

# node / npm
if ! command -v node &>/dev/null; then
    die "Node.js not found. Run scripts/install-linux.sh first."
fi
if ! command -v npm &>/dev/null; then
    die "npm not found. Run scripts/install-linux.sh first."
fi
NODE_MAJOR="$(node --version | cut -d. -f1 | tr -d 'v')"
if [[ "$NODE_MAJOR" -lt 18 ]]; then
    die "Node.js 18+ required (found $(node --version)). Run scripts/install-linux.sh to upgrade."
fi
info "Node.js $(node --version) / npm $(npm --version)"

# postgresql
if ! command -v psql &>/dev/null; then
    die "PostgreSQL client not found. Run scripts/install-linux.sh first."
fi
info "PostgreSQL: $(psql --version)"

# ── Detect PostgreSQL service name ────────────────────────────────────────────
if systemctl list-units --type=service --all 2>/dev/null | grep -q 'postgresql-16'; then
    PG_SERVICE="postgresql-16"
elif systemctl list-units --type=service --all 2>/dev/null | grep -q 'postgresql'; then
    PG_SERVICE="postgresql"
else
    PG_SERVICE="postgresql"
    warn "Could not detect PostgreSQL service name; defaulting to 'postgresql'."
fi
info "PostgreSQL service name: $PG_SERVICE"

# ── Install nginx if missing ──────────────────────────────────────────────────
if ! command -v nginx &>/dev/null; then
    info "Installing nginx..."
    if [[ "$PKG_MANAGER" == "apt" ]]; then
        apt-get install -y nginx
    else
        dnf install -y nginx
    fi
fi
info "nginx: $(nginx -v 2>&1)"

# ── Create system user ────────────────────────────────────────────────────────
if ! id "$INSTALL_USER" &>/dev/null; then
    info "Creating system user '$INSTALL_USER'..."
    useradd --system --no-create-home --shell /usr/sbin/nologin "$INSTALL_USER"
else
    info "System user '$INSTALL_USER' already exists."
fi

# ── Stop existing service if running (for updates) ───────────────────────────
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    info "Stopping existing $SERVICE_NAME service for update..."
    systemctl stop "$SERVICE_NAME"
fi

if systemctl is-active --quiet "$TELEGRAM_SERVICE_NAME" 2>/dev/null; then
    info "Stopping existing $TELEGRAM_SERVICE_NAME service for update..."
    systemctl stop "$TELEGRAM_SERVICE_NAME"
fi

# ── Create directories ────────────────────────────────────────────────────────
info "Creating install directories..."
mkdir -p "$API_INSTALL_DIR" "$TELEGRAM_INSTALL_DIR" "$WEB_INSTALL_DIR" "$ENV_DIR"

# ── Publish the .NET API ──────────────────────────────────────────────────────
info "Publishing SharpClaw API to $API_INSTALL_DIR ..."
"$DOTNET_BIN" publish \
    "$REPO_ROOT/SharpClaw.Api/SharpClaw.Api.csproj" \
    -c Release \
    -o "$API_INSTALL_DIR" \
    --nologo
info "API published."

# ── Publish the Telegram worker ──────────────────────────────────────────────
if [[ "$ENABLE_TELEGRAM_SERVICE" == "true" ]]; then
    info "Publishing SharpClaw Telegram worker to $TELEGRAM_INSTALL_DIR ..."
    "$DOTNET_BIN" publish \
        "$REPO_ROOT/SharpClaw.Telegram/SharpClaw.Telegram.csproj" \
        -c Release \
        -o "$TELEGRAM_INSTALL_DIR" \
        --nologo
    info "Telegram worker published."
else
    info "Skipping Telegram worker publish (--no-telegram)."
fi

# ── Build the frontend ────────────────────────────────────────────────────────
WEB_DIR="$REPO_ROOT/SharpClaw.Web"
info "Installing frontend npm dependencies..."
cd "$WEB_DIR"
npm ci --silent
info "Building React frontend..."
npm run build --silent
info "Deploying frontend to $WEB_INSTALL_DIR ..."
rm -rf "${WEB_INSTALL_DIR:?}"/*
cp -r "$WEB_DIR/dist/." "$WEB_INSTALL_DIR/"
cd "$REPO_ROOT"
info "Frontend deployed."

# ── Set ownership and permissions ─────────────────────────────────────────────
info "Setting file ownership..."
chown -R "$INSTALL_USER":"$INSTALL_USER" "$API_INSTALL_DIR"
chown -R "$INSTALL_USER":"$INSTALL_USER" "$TELEGRAM_INSTALL_DIR"
chown -R www-data:www-data "$WEB_INSTALL_DIR" 2>/dev/null \
    || chown -R nginx:nginx "$WEB_INSTALL_DIR" 2>/dev/null \
    || chown -R "$INSTALL_USER":"$INSTALL_USER" "$WEB_INSTALL_DIR"

# ── Write environment file ────────────────────────────────────────────────────
info "Writing environment file to $ENV_FILE ..."

# Collect all SharpClaw-relevant env vars and write them in systemd EnvironmentFile
# format (KEY=VALUE, one per line, no export keyword).  Secrets are included so
# the file must be restricted to root:sharpclaw read-only.

{
    echo "# SharpClaw service environment – generated by install-service-linux.sh"
    echo "# Restrict access: chmod 640 $ENV_FILE && chown root:$INSTALL_USER $ENV_FILE"
    echo ""

    # Database
    echo "SHARPCLAW_DB_CONNECTION=${SHARPCLAW_DB_CONNECTION:-}"

    # MCP / integration credentials
    echo "GITHUB_PERSONAL_ACCESS_TOKEN=${GITHUB_PERSONAL_ACCESS_TOKEN:-}"

    # Auth / workspace
    echo "SHARPCLAW_JWT_SECRET=${SHARPCLAW_JWT_SECRET:-}"
    echo "SHARPCLAW_WORKSPACE=${SHARPCLAW_WORKSPACE:-/opt/sharpclaw/workspace}"
    echo "SHARPCLAW_KNOWLEDGE_BASE=${SHARPCLAW_KNOWLEDGE_BASE:-/var/lib/sharpclaw/knowledge}"

    # Telegram worker service integration
    echo "SHARPCLAW_API_URL=${SHARPCLAW_API_URL:-http://127.0.0.1:5000}"
    echo "SHARPCLAW_API_TOKEN=${SHARPCLAW_API_TOKEN:-}"

    # ASP.NET Core
    echo "ASPNETCORE_URLS=http://127.0.0.1:5000"
    echo "DOTNET_ROOT=${DOTNET_ROOT}"
} > "$ENV_FILE"

chmod 640 "$ENV_FILE"
chown root:"$INSTALL_USER" "$ENV_FILE"
info "Environment file written (permissions: 640, owner: root:$INSTALL_USER)."

# Ensure the workspace directory exists and is owned by the service user.
WORKSPACE_DIR="${SHARPCLAW_WORKSPACE:-/opt/sharpclaw/workspace}"
mkdir -p "$WORKSPACE_DIR"
chown "$INSTALL_USER":"$INSTALL_USER" "$WORKSPACE_DIR"
info "Workspace directory: $WORKSPACE_DIR"

KNOWLEDGE_DIR="${SHARPCLAW_KNOWLEDGE_BASE:-/var/lib/sharpclaw/knowledge}"
mkdir -p "$KNOWLEDGE_DIR"
chown "$INSTALL_USER":"$INSTALL_USER" "$KNOWLEDGE_DIR"
info "Knowledge base directory: $KNOWLEDGE_DIR"

TELEGRAM_MAPPING_FILE="${TELEGRAM__MAPPINGSTOREPATH:-/var/lib/sharpclaw/telegram-session-mappings.json}"
TELEGRAM_MAPPING_DIR="/var/lib/sharpclaw"
mkdir -p "$TELEGRAM_MAPPING_DIR"
chown "$INSTALL_USER":"$INSTALL_USER" "$TELEGRAM_MAPPING_DIR"
info "Telegram mapping directory: $TELEGRAM_MAPPING_DIR"

# ── Write nginx site configuration ────────────────────────────────────────────
info "Writing nginx site configuration..."
# NOTE: This configuration serves HTTP on port 80 only.  For deployments
# exposed beyond a trusted local or LAN network, configure SSL/TLS certificates
# (e.g. via Let's Encrypt / Certbot) and restrict access with firewall rules.

NGINX_CONF_CONTENT='server {
    listen 80;
    # Bind to localhost only for purely local deployments; remove the line below
    # (or change to 0.0.0.0) to listen on all interfaces.
    # listen 80 default_server;

    root '"$WEB_INSTALL_DIR"';
    index index.html;

    # Proxy API requests (including SSE streams) to the SharpClaw backend.
    location /api/ {
        proxy_pass http://127.0.0.1:5000/api/;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;

        # SSE / streaming support — disable buffering.
        # Long timeout to keep agent streams open for the full response duration.
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 3600s;
    }

    # SPA fallback — serve index.html for any unmatched route.
    location / {
        try_files $uri $uri/ /index.html;
    }
}'

if [[ "$PKG_MANAGER" == "apt" ]]; then
    # Debian/Ubuntu layout: sites-available / sites-enabled
    NGINX_CONF_FILE="/etc/nginx/sites-available/sharpclaw"
    echo "$NGINX_CONF_CONTENT" > "$NGINX_CONF_FILE"
    ln -sf "$NGINX_CONF_FILE" /etc/nginx/sites-enabled/sharpclaw
    # Disable the default site if it exists and would conflict on port 80.
    if [[ -L /etc/nginx/sites-enabled/default ]]; then
        warn "Disabling default nginx site to avoid port 80 conflict."
        rm -f /etc/nginx/sites-enabled/default
    fi
else
    # RHEL/Fedora layout: conf.d
    NGINX_CONF_FILE="/etc/nginx/conf.d/sharpclaw.conf"
    echo "$NGINX_CONF_CONTENT" > "$NGINX_CONF_FILE"
fi

# Validate nginx config before applying.
if nginx -t 2>/dev/null; then
    info "nginx configuration valid."
else
    error "nginx configuration test failed. Check $NGINX_CONF_FILE"
    nginx -t
    exit 1
fi

# ── Write systemd unit file ───────────────────────────────────────────────────
info "Writing systemd unit file to $SERVICE_FILE ..."

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=SharpClaw API
Documentation=https://github.com/kjhughes097/sharpclaw
After=network.target ${PG_SERVICE}.service
Wants=${PG_SERVICE}.service

[Service]
Type=simple
User=${INSTALL_USER}
WorkingDirectory=${API_INSTALL_DIR}
ExecStart=${DOTNET_BIN} ${API_INSTALL_DIR}/SharpClaw.Api.dll
EnvironmentFile=${ENV_FILE}
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=sharpclaw-api

# Harden the service process.
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=full
ProtectHome=yes

[Install]
WantedBy=multi-user.target
EOF

if [[ "$ENABLE_TELEGRAM_SERVICE" == "true" ]]; then
    info "Writing Telegram systemd unit file to $TELEGRAM_SERVICE_FILE ..."
    cat > "$TELEGRAM_SERVICE_FILE" <<EOF
[Unit]
Description=SharpClaw Telegram Worker
Documentation=https://github.com/kjhughes097/sharpclaw
After=network.target ${SERVICE_NAME}.service
Wants=${SERVICE_NAME}.service

[Service]
Type=simple
User=${INSTALL_USER}
WorkingDirectory=${TELEGRAM_INSTALL_DIR}
ExecStart=${DOTNET_BIN} ${TELEGRAM_INSTALL_DIR}/SharpClaw.Telegram.dll
EnvironmentFile=${ENV_FILE}
Restart=on-failure
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=sharpclaw-telegram

NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=full
ProtectHome=yes

[Install]
WantedBy=multi-user.target
EOF
else
    if systemctl is-enabled --quiet "$TELEGRAM_SERVICE_NAME" 2>/dev/null; then
        info "Disabling previously enabled $TELEGRAM_SERVICE_NAME service..."
        systemctl disable "$TELEGRAM_SERVICE_NAME" || true
    fi

    if [[ -f "$TELEGRAM_SERVICE_FILE" ]]; then
        info "Removing stale Telegram unit file at $TELEGRAM_SERVICE_FILE ..."
        rm -f "$TELEGRAM_SERVICE_FILE"
    fi
fi

# ── Enable and start services ─────────────────────────────────────────────────
info "Reloading systemd daemon..."
systemctl daemon-reload

info "Enabling $SERVICE_NAME..."
systemctl enable "$SERVICE_NAME"

info "Starting $SERVICE_NAME..."
systemctl start "$SERVICE_NAME"

if [[ "$ENABLE_TELEGRAM_SERVICE" == "true" ]]; then
    info "Enabling $TELEGRAM_SERVICE_NAME..."
    systemctl enable "$TELEGRAM_SERVICE_NAME"

    info "Starting $TELEGRAM_SERVICE_NAME..."
    systemctl start "$TELEGRAM_SERVICE_NAME"
fi

info "Enabling and restarting nginx..."
systemctl enable nginx
systemctl restart nginx

# ── Status check ──────────────────────────────────────────────────────────────
sleep 2
echo ""
if systemctl is-active --quiet "$SERVICE_NAME"; then
    echo -e "${GREEN}[service-install]${NC} $SERVICE_NAME is running."
else
    warn "$SERVICE_NAME did not start. Check logs with: journalctl -u $SERVICE_NAME -n 50"
fi

if systemctl is-active --quiet nginx; then
    echo -e "${GREEN}[service-install]${NC} nginx is running."
else
    warn "nginx did not start. Check logs with: journalctl -u nginx -n 50"
fi

if [[ "$ENABLE_TELEGRAM_SERVICE" == "true" ]]; then
    if systemctl is-active --quiet "$TELEGRAM_SERVICE_NAME"; then
        echo -e "${GREEN}[service-install]${NC} $TELEGRAM_SERVICE_NAME is running."
    else
        warn "$TELEGRAM_SERVICE_NAME did not start. Check logs with: journalctl -u $TELEGRAM_SERVICE_NAME -n 50"
    fi
else
    warn "Telegram service not installed. Re-run without --no-telegram to install it."
fi

# ── Summary ───────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}══════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN} SharpClaw service installation complete${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════════════════${NC}"
echo ""
echo "Installed paths:"
echo "  API binary         : $API_INSTALL_DIR"
echo "  Telegram binary    : $TELEGRAM_INSTALL_DIR"
echo "  Frontend           : $WEB_INSTALL_DIR"
echo "  Environment file   : $ENV_FILE"
echo "  Systemd unit       : $SERVICE_FILE"
if [[ "$ENABLE_TELEGRAM_SERVICE" == "true" ]]; then
echo "  Telegram unit      : $TELEGRAM_SERVICE_FILE"
fi
echo "  nginx site config  : $NGINX_CONF_FILE"
echo "  Workspace          : $WORKSPACE_DIR"
echo ""
echo "Service management:"
echo "  sudo systemctl status  $SERVICE_NAME"
echo "  sudo systemctl restart $SERVICE_NAME"
echo "  sudo systemctl stop    $SERVICE_NAME"
echo "  sudo journalctl -u     $SERVICE_NAME -f"
echo ""
echo "  sudo systemctl status  $TELEGRAM_SERVICE_NAME"
echo "  sudo systemctl restart $TELEGRAM_SERVICE_NAME"
echo "  sudo systemctl stop    $TELEGRAM_SERVICE_NAME"
echo "  sudo journalctl -u     $TELEGRAM_SERVICE_NAME -f"
echo ""
echo "  sudo systemctl status  nginx"
echo "  sudo systemctl restart nginx"
echo "  sudo journalctl -u     nginx -f"
echo ""
echo "The app is available at: http://localhost"
echo ""
echo "To update after a code change, run this script again."
echo ""
