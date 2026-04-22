#!/usr/bin/env bash
# restart-sharpclaw.sh - Restart SharpClaw services
# This script should be run with sudo privileges

set -euo pipefail

SERVICE_NAME="sharpclaw"

echo "==> Restarting SharpClaw services..."

# Stop services
echo "Stopping services..."
sudo systemctl stop "${SERVICE_NAME}.service" 2>/dev/null || true
sudo systemctl stop "${SERVICE_NAME}-telegram.service" 2>/dev/null || true

# Kill any lingering processes
if pgrep -f "SharpClaw\.Api" >/dev/null 2>&1; then
    echo "Killing lingering SharpClaw processes..."
    sudo pkill -f "SharpClaw\.Api" 2>/dev/null || true
    sleep 2
    sudo pkill -9 -f "SharpClaw\.Api" 2>/dev/null || true
    sleep 1
fi

# Start services if they're enabled
echo "Starting enabled services..."
if systemctl is-enabled --quiet "${SERVICE_NAME}.service" 2>/dev/null; then
    sudo systemctl start "${SERVICE_NAME}.service"
    echo "✅ Started ${SERVICE_NAME}.service"
fi

if systemctl is-enabled --quiet "${SERVICE_NAME}-telegram.service" 2>/dev/null; then
    sudo systemctl start "${SERVICE_NAME}-telegram.service"  
    echo "✅ Started ${SERVICE_NAME}-telegram.service"
fi

echo "==> SharpClaw services restarted!"
echo
echo "Check status with:"
echo "  sudo systemctl status ${SERVICE_NAME}"
echo "  sudo journalctl -u ${SERVICE_NAME} -f"