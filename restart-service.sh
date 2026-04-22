#!/bin/bash
# Simple script to restart SharpClaw service
# This needs to be run with appropriate privileges

echo "Stopping SharpClaw service..."
systemctl stop sharpclaw

echo "Starting SharpClaw service..."
systemctl start sharpclaw

echo "Checking service status..."
systemctl status sharpclaw --no-pager -l