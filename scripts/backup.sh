#!/bin/bash
# Daily backup of SharpClaw workspace and semantic memory database

set -e

WORKSPACE_PATH="/home/khughes/sharpclaw-workspace"
DB_PATH="/home/khughes/projects/sharpclaw/SharpClaw/bin/Debug/net10.0/data/semantic-memory.db"
BACKUP_DIR="/home/khughes/backups/sharpclaw"

echo "[$(date +'%Y-%m-%d %H:%M:%S')] Starting SharpClaw backup..."

# Verify backup directory exists
if [ ! -d "$BACKUP_DIR" ]; then
    mkdir -p "$BACKUP_DIR"
fi

# Create timestamp
TIMESTAMP=$(date -u +"%Y-%m-%d-%H%M")
BACKUP_FILE="$BACKUP_DIR/backup-$TIMESTAMP.tar.gz"
STAGE_DIR=$(mktemp -d)

trap "rm -rf $STAGE_DIR" EXIT

echo "[$(date +'%Y-%m-%d %H:%M:%S')] Preparing backup..."

# Copy workspace (excluding coding)
mkdir -p "$STAGE_DIR/workspace"
rsync -a "$WORKSPACE_PATH/" "$STAGE_DIR/workspace/" --exclude='coding/' > /dev/null 2>&1

# Copy database if it exists
if [ -f "$DB_PATH" ]; then
    cp "$DB_PATH" "$STAGE_DIR/semantic-memory.db"
fi

# Create tarball
echo "[$(date +'%Y-%m-%d %H:%M:%S')] Creating backup archive..."
cd "$STAGE_DIR"
tar -czf "$BACKUP_FILE" . > /dev/null 2>&1
cd - > /dev/null

# Generate checksum
CHECKSUM=$(sha256sum "$BACKUP_FILE" | cut -d' ' -f1)
echo "$CHECKSUM  $(basename $BACKUP_FILE)" > "$BACKUP_FILE.sha256"

# Clean up old backups (older than 7 days)
echo "[$(date +'%Y-%m-%d %H:%M:%S')] Cleaning up old backups..."
find "$BACKUP_DIR" -name "backup-*.tar.gz" -mtime +7 -delete 2>/dev/null || true
find "$BACKUP_DIR" -name "backup-*.sha256" -mtime +7 -delete 2>/dev/null || true

# Report results
SIZE=$(du -h "$BACKUP_FILE" | cut -f1)
echo "[$(date +'%Y-%m-%d %H:%M:%S')] Backup completed: $BACKUP_FILE ($SIZE)"
echo "[$(date +'%Y-%m-%d %H:%M:%S')] Checksum: $CHECKSUM"
echo "[$(date +'%Y-%m-%d %H:%M:%S')] Backup successful"
