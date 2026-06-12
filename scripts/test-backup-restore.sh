#!/bin/bash
# End-to-end backup and restore test

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WORKSPACE_PATH="/home/khughes/sharpclaw-workspace"
DB_PATH="/home/khughes/sharpclaw-workspace/data/semantic-memory.db"
BACKUP_DIR="/home/khughes/backups/sharpclaw"
TEST_DIR=$(mktemp -d)
RESTORE_TEST_DIR=$(mktemp -d)

echo "=== SharpClaw Backup & Restore Test ==="
echo "Test directory: $TEST_DIR"
echo "Restore test directory: $RESTORE_TEST_DIR"

cleanup() {
    echo "Cleaning up temporary directories..."
    rm -rf "$TEST_DIR" "$RESTORE_TEST_DIR"
}

trap cleanup EXIT

# Verify prerequisites
if [ ! -d "$WORKSPACE_PATH" ]; then
    echo "ERROR: Workspace not found at $WORKSPACE_PATH"
    exit 1
fi

if [ ! -f "$DB_PATH" ]; then
    echo "WARNING: Database not found at $DB_PATH (continuing without it)"
    DB_PATH=""
fi

if [ ! -d "$BACKUP_DIR" ]; then
    echo "ERROR: Backup directory not found at $BACKUP_DIR"
    exit 1
fi

# Step 1: Create test backup
echo ""
echo "Step 1: Creating test backup..."
TIMESTAMP=$(date -u +"%Y-%m-%d-%H%M")
TEST_BACKUP="$TEST_DIR/backup-$TIMESTAMP.tar.gz"

# Create temporary staging directory
STAGE_DIR="$TEST_DIR/stage"
mkdir -p "$STAGE_DIR/workspace"

# Copy workspace (excluding coding)
echo "  - Copying workspace (excluding coding/)..."
rsync -a "$WORKSPACE_PATH/" "$STAGE_DIR/workspace/" --exclude='coding/'

# Copy database if it exists
if [ -n "$DB_PATH" ] && [ -f "$DB_PATH" ]; then
    echo "  - Copying semantic-memory.db..."
    cp "$DB_PATH" "$STAGE_DIR/semantic-memory.db"
fi

# Create tarball
echo "  - Creating tarball..."
cd "$STAGE_DIR"
tar -czf "$TEST_BACKUP" .
cd - > /dev/null

BACKUP_SIZE=$(du -h "$TEST_BACKUP" | cut -f1)
echo "  ✓ Backup created: $TEST_BACKUP ($BACKUP_SIZE)"

# Step 2: Verify checksum
echo ""
echo "Step 2: Verifying backup integrity..."
CHECKSUM=$(sha256sum "$TEST_BACKUP" | cut -d' ' -f1)
CHECKSUM_FILE="$TEST_BACKUP.sha256"
echo "$CHECKSUM  $(basename $TEST_BACKUP)" > "$CHECKSUM_FILE"

cd "$TEST_DIR"
sha256sum -c "$CHECKSUM_FILE"
cd - > /dev/null
echo "  ✓ Checksum verified"

# Step 3: Extract to restore test directory
echo ""
echo "Step 3: Extracting backup to test location..."
tar -xzf "$TEST_BACKUP" -C "$RESTORE_TEST_DIR"
echo "  ✓ Backup extracted"

# Step 4: Verify extracted contents
echo ""
echo "Step 4: Verifying restored content..."
if [ ! -d "$RESTORE_TEST_DIR/workspace" ]; then
    echo "  ERROR: Workspace directory not found in restored backup"
    exit 1
fi

WORKSPACE_FILES=$(find "$RESTORE_TEST_DIR/workspace" -type f | wc -l)
if [ "$WORKSPACE_FILES" -eq 0 ]; then
    echo "  ERROR: No files found in restored workspace"
    exit 1
fi

echo "  ✓ Workspace restored with $WORKSPACE_FILES files"

if [ -f "$RESTORE_TEST_DIR/semantic-memory.db" ]; then
    DB_SIZE=$(du -h "$RESTORE_TEST_DIR/semantic-memory.db" | cut -f1)
    echo "  ✓ Database restored ($DB_SIZE)"
else
    echo "  ! Database not present (skipped)"
fi

# Step 5: Compare key files
echo ""
echo "Step 5: Spot-checking restored content..."

# Check if agent memory directories exist
for agent in ade cody fin myles deb; do
    if [ -d "$RESTORE_TEST_DIR/workspace/$agent" ]; then
        FILES=$(find "$RESTORE_TEST_DIR/workspace/$agent" -type f | wc -l)
        echo "  ✓ Agent '$agent': $FILES files"
    fi
done

# Check for projects and metadata
if [ -d "$RESTORE_TEST_DIR/workspace/projects" ]; then
    echo "  ✓ Projects directory present"
fi

echo ""
echo "=== Test Results ==="
echo "✓ Backup creation successful"
echo "✓ Checksum verification passed"
echo "✓ Restore extraction successful"
echo "✓ Content verification passed"
echo ""
echo "=== Summary ==="
echo "Backup size:        $BACKUP_SIZE"
echo "Workspace files:    $WORKSPACE_FILES"
echo "Restore location:   $RESTORE_TEST_DIR"
echo ""
echo "To manually inspect restored content:"
echo "  ls -la '$RESTORE_TEST_DIR'"
echo ""
echo "✓ All tests passed!"
