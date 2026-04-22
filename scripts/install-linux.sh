#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# install-linux.sh — Build and install SharpClaw (API + Web) on a Linux host.
#
# Prerequisites (installed automatically if missing):
#   - .NET 10 SDK  (https://dot.net/download)
#   - Node.js 22+  (https://nodejs.org)
#   - nginx         (apt install nginx) — optional, for reverse proxy
#
# Usage:
#   sudo ./scripts/install-linux.sh [--install-dir /opt/sharpclaw]
# ---------------------------------------------------------------------------
set -euo pipefail

INSTALL_DIR="/opt/sharpclaw"
REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SHARPCLAW_USER="${SHARPCLAW_USER:-sharpclaw}"
SERVICE_NAME="sharpclaw"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

echo "==> SharpClaw installer"
echo "    Repository : $REPO_DIR"
echo "    Install to : $INSTALL_DIR"
echo ""

# ---------- Install .NET SDK if missing ----------
if ! command -v dotnet >/dev/null 2>&1; then
  echo "==> .NET SDK not found — installing .NET 10..."
  if command -v apt-get >/dev/null 2>&1; then
    apt-get update -qq
    apt-get install -y -qq dotnet-sdk-10.0
  elif command -v dnf >/dev/null 2>&1; then
    dnf install -y dotnet-sdk-10.0
  else
    echo "    Package manager not detected (apt/dnf). Installing via dotnet-install script..."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
    rm -f /tmp/dotnet-install.sh
  fi
  command -v dotnet >/dev/null 2>&1 || { echo "ERROR: .NET SDK installation failed."; exit 1; }
fi

# ---------- Install Node.js if missing ----------
if ! command -v node >/dev/null 2>&1; then
  echo "==> Node.js not found — installing Node.js 22 LTS..."
  if command -v apt-get >/dev/null 2>&1; then
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash -
    apt-get install -y -qq nodejs
  elif command -v dnf >/dev/null 2>&1; then
    curl -fsSL https://rpm.nodesource.com/setup_22.x | bash -
    dnf install -y nodejs
  else
    echo "ERROR: Cannot auto-install Node.js. Install Node.js 20+ manually."; exit 1
  fi
  command -v node >/dev/null 2>&1 || { echo "ERROR: Node.js installation failed."; exit 1; }
fi

echo "==> .NET $(dotnet --version)"
echo "==> Node $(node --version)"
echo ""

# ---------- Create service user ----------
if ! id "$SHARPCLAW_USER" &>/dev/null; then
  echo "==> Creating system user: $SHARPCLAW_USER"
  useradd --system --shell /usr/sbin/nologin --home-dir "$INSTALL_DIR" "$SHARPCLAW_USER"
fi

# ---------- Add sharpclaw to invoking user's group ----------
# This allows the service to read/write files in the user's home directory
# when WorkspaceRoot points there.
if [[ -n "${SUDO_USER:-}" ]] && id "$SUDO_USER" &>/dev/null; then
  OWNER_GROUP=$(id -gn "$SUDO_USER")
  if ! id -nG "$SHARPCLAW_USER" | grep -qw "$OWNER_GROUP"; then
    echo "==> Adding $SHARPCLAW_USER to group: $OWNER_GROUP"
    usermod -aG "$OWNER_GROUP" "$SHARPCLAW_USER"
  fi
fi

# ---------- Stop running services if they exist ----------
SERVICES_STOPPED=false
for svc in "${SERVICE_NAME}.service" "${SERVICE_NAME}-api.service" "${SERVICE_NAME}-telegram.service"; do
  if systemctl is-active --quiet "$svc" 2>/dev/null; then
    echo "==> Stopping $svc..."
    systemctl stop "$svc" 2>/dev/null || true
    # Wait for the unit to fully deactivate
    for i in $(seq 1 15); do
      systemctl is-active --quiet "$svc" 2>/dev/null || break
      sleep 1
    done
    SERVICES_STOPPED=true
  fi
done

# Kill any remaining SharpClaw.Api processes not managed by systemd
if pgrep -f "SharpClaw\.Api" >/dev/null 2>&1; then
  echo "==> Killing lingering SharpClaw.Api processes..."
  pkill -f "SharpClaw\.Api" 2>/dev/null || true
  sleep 2
  # Force kill if still alive
  pkill -9 -f "SharpClaw\.Api" 2>/dev/null || true
  sleep 1
fi

# ---------- Build .NET API ----------
echo "==> Building SharpClaw API (Release)..."
dotnet publish "$REPO_DIR/src/SharpClaw.Api/SharpClaw.Api.csproj" \
  -c Release \
  -o "$INSTALL_DIR/api" \
  --nologo

# ---------- Build Telegram worker ----------
echo "==> Building SharpClaw Telegram worker (Release)..."
dotnet publish "$REPO_DIR/src/SharpClaw.Telegram/SharpClaw.Telegram.csproj" \
  -c Release \
  -o "$INSTALL_DIR/telegram" \
  --nologo

# ---------- Build Web frontend ----------
echo "==> Building SharpClaw Web..."
pushd "$REPO_DIR/src/SharpClaw.Web" > /dev/null
npm ci --no-audit --no-fund
npm run build
popd > /dev/null

mkdir -p "$INSTALL_DIR/web"
cp -r "$REPO_DIR/src/SharpClaw.Web/build/." "$INSTALL_DIR/web/"

# ---------- Copy agents ----------
echo "==> Copying agent definitions..."
mkdir -p "$INSTALL_DIR/agents"
cp -r "$REPO_DIR/agents/." "$INSTALL_DIR/agents/"

# ---------- Data directory ----------
mkdir -p "$INSTALL_DIR/data"

# ---------- Environment file ----------
if [[ ! -f "$INSTALL_DIR/.env" ]]; then
  echo "==> Creating default .env from template..."
  cp "$REPO_DIR/.env.example" "$INSTALL_DIR/.env"
  chmod 600 "$INSTALL_DIR/.env"
  echo "    *** Edit $INSTALL_DIR/.env with your API keys ***"
fi

# ---------- appsettings override ----------
cat > "$INSTALL_DIR/api/appsettings.Production.json" <<EOF
{
  "SharpClaw": {
    "DataRoot": "$INSTALL_DIR/data",
    "AgentsDir": "$INSTALL_DIR/agents"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
EOF

# ---------- Ownership ----------
echo "==> Setting ownership to $SHARPCLAW_USER..."
chown -R "$SHARPCLAW_USER:$SHARPCLAW_USER" "$INSTALL_DIR"

# ---------- Restart enabled services ----------
echo "==> Restarting services..."
for svc in "${SERVICE_NAME}.service" "${SERVICE_NAME}-telegram.service"; do
  if systemctl is-enabled --quiet "$svc" 2>/dev/null; then
    systemctl start "$svc" 2>/dev/null || true
  fi
done
echo "    Services restarted."

echo ""
echo "==> Build complete."
echo "    API published to : $INSTALL_DIR/api"
echo "    Web built to     : $INSTALL_DIR/web"
echo "    Agents copied to : $INSTALL_DIR/agents"
echo "    Data directory   : $INSTALL_DIR/data"
echo ""
echo "Next steps:"
echo "  1. Edit $INSTALL_DIR/.env with your API keys"
echo "  2. Run: sudo ./scripts/install-service-linux.sh"
echo "  3. Configure nginx (see below) or run scripts/start-backend.sh for dev"
