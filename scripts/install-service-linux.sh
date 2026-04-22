#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# install-service-linux.sh — Install SharpClaw as a systemd service + nginx
#                             reverse proxy.
#
# Run AFTER install-linux.sh.
#
# Usage:
#   sudo ./scripts/install-service-linux.sh [--install-dir /opt/sharpclaw]
#                                            [--domain sharpclaw.local]
#                                            [--api-port 5100]
#                                            [--web-port 8097]
# ---------------------------------------------------------------------------
set -euo pipefail

INSTALL_DIR="/opt/sharpclaw"
DOMAIN="_"
API_PORT="5100"
WEB_PORT="8097"
SHARPCLAW_USER="${SHARPCLAW_USER:-sharpclaw}"
SERVICE_NAME="sharpclaw"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --domain)      DOMAIN="$2"; shift 2 ;;
    --api-port)    API_PORT="$2"; shift 2 ;;
    --web-port)    WEB_PORT="$2"; shift 2 ;;
    *) echo "Unknown option: $1"; exit 1 ;;
  esac
done

echo "==> Installing SharpClaw systemd service"
echo "    Install dir : $INSTALL_DIR"
echo "    API port    : $API_PORT"
echo "    Web port    : $WEB_PORT"
echo "    Domain      : $DOMAIN"
echo ""

# ---------- Verify install directory ----------
if [[ ! -d "$INSTALL_DIR/api" ]]; then
  echo "ERROR: $INSTALL_DIR/api not found. Run install-linux.sh first."
  exit 1
fi

# ---------- Detect workspace root from .env ----------
WORKSPACE_ROOT=""
if [[ -f "$INSTALL_DIR/.env" ]]; then
  WORKSPACE_ROOT=$(grep -E '^SharpClaw__WorkspaceRoot=' "$INSTALL_DIR/.env" 2>/dev/null | cut -d= -f2- | xargs)
fi

# Build ReadWritePaths and ProtectHome based on whether workspace is under /home
RW_PATHS="${INSTALL_DIR}/data ${INSTALL_DIR}/cache"
PROTECT_HOME="true"
if [[ -n "$WORKSPACE_ROOT" ]]; then
  RW_PATHS="${RW_PATHS} ${WORKSPACE_ROOT}"
  if [[ "$WORKSPACE_ROOT" == /home/* ]]; then
    PROTECT_HOME="false"
    echo "    Workspace under /home — ProtectHome disabled"
  fi
fi

# ---------- systemd unit: sharpclaw-api ----------
echo "==> Creating systemd service: ${SERVICE_NAME}.service"
cat > "/etc/systemd/system/${SERVICE_NAME}.service" <<EOF
[Unit]
Description=SharpClaw API
After=network.target

[Service]
Type=exec
User=${SHARPCLAW_USER}
Group=${SHARPCLAW_USER}
WorkingDirectory=${INSTALL_DIR}/api
ExecStart=${INSTALL_DIR}/api/SharpClaw.Api --urls "http://127.0.0.1:${API_PORT}"
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=sharpclaw

# Environment from .env file
EnvironmentFile=-${INSTALL_DIR}/.env

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=${PROTECT_HOME}
ReadWritePaths=${RW_PATHS}
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true

# Copilot SDK needs HOME and writable cache for its CLI subprocess
Environment=HOME=${INSTALL_DIR}/cache
Environment=XDG_CACHE_HOME=${INSTALL_DIR}/cache
Environment=TMPDIR=${INSTALL_DIR}/cache/tmp

# Resource limits
LimitNOFILE=65536
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF

# ---------- systemd unit: sharpclaw-telegram ----------
echo "==> Creating systemd service: ${SERVICE_NAME}-telegram.service"
cat > "/etc/systemd/system/${SERVICE_NAME}-telegram.service" <<EOF
[Unit]
Description=SharpClaw Telegram Bot
After=network.target ${SERVICE_NAME}.service
Requires=${SERVICE_NAME}.service

[Service]
Type=exec
User=${SHARPCLAW_USER}
Group=${SHARPCLAW_USER}
WorkingDirectory=${INSTALL_DIR}/telegram
ExecStart=${INSTALL_DIR}/telegram/SharpClaw.Telegram
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=sharpclaw-telegram

# Environment from .env file
EnvironmentFile=-${INSTALL_DIR}/.env

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true

# Resource limits
LimitNOFILE=65536
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF

# ---------- nginx site ----------
echo "==> Configuring nginx reverse proxy..."

# Check if nginx is installed
if command -v nginx >/dev/null 2>&1; then
  cat > "/etc/nginx/sites-available/${SERVICE_NAME}" <<EOF
server {
    listen ${WEB_PORT};
    server_name ${DOMAIN};

    # Frontend static files
    root ${INSTALL_DIR}/web;
    index index.html;

    # API reverse proxy
    location /api/ {
        proxy_pass http://127.0.0.1:${API_PORT};
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;

        # SSE support — disable buffering for streaming
        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 300s;
        proxy_set_header Connection '';
        chunked_transfer_encoding off;
    }

    # SPA fallback — serve index.html for client-side routes
    location / {
        try_files \$uri \$uri/ /index.html;
    }

    # Cache static assets
    location ~* \.(js|css|png|jpg|jpeg|gif|ico|svg|woff2?)$ {
        expires 30d;
        add_header Cache-Control "public, immutable";
    }

    # Security headers
    add_header X-Frame-Options "SAMEORIGIN" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    access_log /var/log/nginx/sharpclaw-access.log;
    error_log  /var/log/nginx/sharpclaw-error.log;
}
EOF

  # Enable the site
  ln -sf "/etc/nginx/sites-available/${SERVICE_NAME}" "/etc/nginx/sites-enabled/${SERVICE_NAME}"

  # Test nginx config
  if nginx -t 2>/dev/null; then
    echo "    nginx config OK"
  else
    echo "    WARNING: nginx config test failed — check /etc/nginx/sites-available/${SERVICE_NAME}"
  fi
else
  echo "    nginx not installed — skipping reverse proxy config"
  echo "    Install with: sudo apt install nginx"
fi

# ---------- Enable and start ----------
echo "==> Reloading systemd..."
systemctl daemon-reload
systemctl enable "${SERVICE_NAME}.service"
systemctl enable "${SERVICE_NAME}-telegram.service"

echo ""
echo "==> Services installed: ${SERVICE_NAME}.service, ${SERVICE_NAME}-telegram.service"
echo ""
echo "Commands:"
echo "  sudo systemctl start ${SERVICE_NAME}              # Start the API"
echo "  sudo systemctl start ${SERVICE_NAME}-telegram     # Start the Telegram bot"
echo "  sudo systemctl status ${SERVICE_NAME}             # Check API status"
echo "  sudo systemctl status ${SERVICE_NAME}-telegram    # Check Telegram status"
echo "  sudo journalctl -u ${SERVICE_NAME} -f             # Tail API logs"
echo "  sudo journalctl -u ${SERVICE_NAME}-telegram -f    # Tail Telegram logs"
echo ""

if command -v nginx >/dev/null 2>&1; then
  echo "  sudo systemctl restart nginx             # Apply nginx config"
  echo ""
  if [[ "$DOMAIN" == "_" ]]; then
    echo "  Open http://<server-ip> in your browser"
  else
    echo "  Open http://${DOMAIN} in your browser"
  fi
fi

echo ""
echo "Don't forget to edit $INSTALL_DIR/.env with your API keys before starting!"
