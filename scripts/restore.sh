#!/bin/bash
# Restore SharpClaw from backup

set -e

BACKUP_DIR="/home/khughes/backups/sharpclaw"
WORKSPACE_PATH="/home/khughes/sharpclaw-workspace"
DB_PATH="/home/khughes/sharpclaw-workspace/data/semantic-memory.db"

echo "=== SharpClaw Restore Script ==="
echo ""

# Find latest backup if not specified
BACKUP_FILE="${1:-}"
if [ -z "$BACKUP_FILE" ]; then
    echo "Finding latest backup..."
    BACKUP_FILE=$(ls -1t "$BACKUP_DIR"/backup-*.tar.gz 2>/dev/null | head -1)
    if [ -z "$BACKUP_FILE" ]; then
        echo "ERROR: No backup files found in $BACKUP_DIR"
        exit 1
    fi
fi

if [ ! -f "$BACKUP_FILE" ]; then
    echo "ERROR: Backup file not found: $BACKUP_FILE"
    exit 1
fi

echo "Backup file: $BACKUP_FILE"
echo ""

# Verify checksum
CHECKSUM_FILE="$BACKUP_FILE.sha256"
if [ -f "$CHECKSUM_FILE" ]; then
    echo "Verifying backup integrity..."
    cd "$(dirname "$BACKUP_FILE")"
    if ! sha256sum -c "$(basename $CHECKSUM_FILE)" > /dev/null 2>&1; then
        echo "ERROR: Checksum verification failed!"
        exit 1
    fi
    cd - > /dev/null
    echo "✓ Checksum verified"
else
    echo "WARNING: No checksum file found ($CHECKSUM_FILE)"
fi

echo ""
echo "This will restore from: $BACKUP_FILE"
echo "Current workspace will be backed up to: ${WORKSPACE_PATH}.backup-$(date +%s)"
echo ""
read -p "Continue with restore? (yes/no): " CONFIRM

if [ "$CONFIRM" != "yes" ]; then
    echo "Restore cancelled."
    exit 0
fi

echo ""
echo "Starting restore..."

# Create temporary extraction directory
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

# Extract backup
echo "Extracting backup..."
tar -xzf "$BACKUP_FILE" -C "$TEMP_DIR"

# Backup current workspace
if [ -d "$WORKSPACE_PATH" ]; then
    echo "Backing up current workspace..."
    mv "$WORKSPACE_PATH" "${WORKSPACE_PATH}.backup-$(date +%s)"
fi

# Restore workspace
echo "Restoring workspace..."
mv "$TEMP_DIR/workspace" "$WORKSPACE_PATH"

# Restore database
if [ -f "$TEMP_DIR/semantic-memory.db" ]; then
    echo "Restoring semantic-memory.db..."
    mkdir -p "$(dirname "$DB_PATH")"
    cp "$TEMP_DIR/semantic-memory.db" "$DB_PATH"
fi

echo ""
echo "✓ Restore completed successfully!"
echo ""
echo "Next steps:"
echo "1. Start SharpClaw: cd /home/khughes/projects/sharpclaw/SharpClaw && dotnet run"
echo "2. Verify agent memory loads correctly"
echo "3. Check scheduled tasks are present"
echo "4. Confirm no data corruption in logs"
