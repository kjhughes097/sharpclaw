# SharpClaw Disaster Recovery Guide

## Overview

SharpClaw stores critical data across multiple locations. This guide describes how to recover from data loss events using automated backups.

## What Is Backed Up

**Daily backup includes:**
- Workspace directory: `/home/khughes/sharpclaw-workspace/` (excluding `coding/`)
  - Agent memory (ade, cody, fin, myles, deb)
  - Knowledge files and patterns
  - Projects and metadata
  - Session transcripts and conversation history
  - Scheduled task definitions

- Semantic memory database: `SharpClaw/bin/Debug/net10.0/data/semantic-memory.db`
  - Embeddings and extracted memories
  - Agent context state

**NOT backed up (already in git):**
- Agent definitions, skills, MCP server configs
- Source code (tracked in git repository)

## Backup Storage Location

Backups are stored in: `/home/khughes/backups/sharpclaw/`

Format: `backup-YYYY-MM-DD-HHmm.tar.gz`

Example: `backup-2026-06-11-0300.tar.gz`

Each backup includes a checksum file: `backup-YYYY-MM-DD-HHmm.tar.gz.sha256`

## Backup Status & Management

### List Available Backups

```bash
ls -lh /home/khughes/backups/sharpclaw/
```

### Verify Backup Integrity

```bash
cd /home/khughes/backups/sharpclaw/
sha256sum -c backup-YYYY-MM-DD-HHmm.tar.gz.sha256
```

### Cleanup Old Backups Manually

```bash
# Keep only last N days (e.g., 7 days)
find /home/khughes/backups/sharpclaw/ -name "backup-*.tar.gz" -mtime +7 -delete
```

## Recovery Procedure

### Recovery Time Estimate: 30–45 minutes

### Step 1: Stop SharpClaw

```bash
# Stop the running application
pkill -f "SharpClaw"
# Or if running as a service:
sudo systemctl stop sharpclaw
```

### Step 2: Locate Latest Backup

```bash
# Find the most recent backup
ls -1t /home/khughes/backups/sharpclaw/backup-*.tar.gz | head -1
```

### Step 3: Verify Backup Integrity (Recommended)

```bash
BACKUP_FILE="/home/khughes/backups/sharpclaw/backup-YYYY-MM-DD-HHmm.tar.gz"
CHECKSUM_FILE="${BACKUP_FILE}.sha256"

# Verify checksum
sha256sum -c "$CHECKSUM_FILE"
```

If the checksum verification fails, try the next most recent backup.

### Step 4: Extract Backup to Temporary Location

```bash
# Create temporary extraction directory
TEMP_DIR=$(mktemp -d)
BACKUP_FILE="/home/khughes/backups/sharpclaw/backup-YYYY-MM-DD-HHmm.tar.gz"

# Extract
tar -xzf "$BACKUP_FILE" -C "$TEMP_DIR"

# List contents to verify
ls -la "$TEMP_DIR"
```

### Step 5: Restore Workspace

```bash
# Backup current workspace (if it exists)
if [ -d "/home/khughes/sharpclaw-workspace" ]; then
  mv /home/khughes/sharpclaw-workspace /home/khughes/sharpclaw-workspace.backup-$(date +%s)
fi

# Restore from backup
cp -r "$TEMP_DIR/workspace" /home/khughes/sharpclaw-workspace
```

### Step 6: Restore Semantic Memory Database

```bash
# Ensure target directory exists
mkdir -p "SharpClaw/bin/Debug/net10.0/data"

# Restore database
cp "$TEMP_DIR/semantic-memory.db" "SharpClaw/bin/Debug/net10.0/data/semantic-memory.db"
```

### Step 7: Start SharpClaw

```bash
# Start the application
cd /home/khughes/projects/sharpclaw/SharpClaw
dotnet run

# Or if running as a service:
sudo systemctl start sharpclaw
```

### Step 8: Verify Recovery

```bash
# Check that SharpClaw started successfully
curl http://localhost:5100/health

# Verify agent memory is accessible
# Log into the application and check:
# - Agent memory loads correctly
# - Scheduled tasks appear in the task list
# - Recent conversations are visible
# - Projects and tickets are present
```

### Step 9: Cleanup Temporary Files

```bash
# Remove temporary extraction directory
rm -rf "$TEMP_DIR"
```

## Verification Checklist

After a recovery, verify the following:

- [ ] SharpClaw starts without errors
- [ ] Web UI loads and is responsive
- [ ] Agent memory files are accessible (check agent workspace)
- [ ] Scheduled tasks are present and configured correctly
- [ ] Projects and tickets are visible in the task system
- [ ] Session transcripts are accessible
- [ ] Recent conversation history is intact
- [ ] No data corruption detected in logs

## Disaster Prevention

### Regular Backup Verification

Backups are only effective if they can be restored. Test restores periodically:

```bash
# Monthly restore test (automated in CI pipeline if configured)
./scripts/test-backup-restore.sh
```

### Monitoring

Check backup logs and status:

```bash
# View recent backup events
journalctl -u sharpclaw -n 50 | grep -i backup

# Check backup directory size and file count
du -sh /home/khughes/backups/sharpclaw/
ls /home/khughes/backups/sharpclaw/ | wc -l
```

### Retention Policy

- **Default**: Keep 7 days of backups
- **Storage**: ~46 MB per backup × 7 = ~320 MB required
- **Automated cleanup**: Old backups are automatically deleted

## Remote Backup Pulls (Optional)

For off-site redundancy, a remote machine can pull backups via SCP:

```bash
# On remote machine (configure with cron for automated pulls):
BACKUP_HOST="khughes@localhost"
BACKUP_DIR="/home/khughes/backups/sharpclaw"
LOCAL_BACKUP_DIR="/backups/sharpclaw-offsite"

# Pull latest 7 backups
scp "$BACKUP_HOST:$BACKUP_DIR/backup-*.tar.gz" "$LOCAL_BACKUP_DIR/"
scp "$BACKUP_HOST:$BACKUP_DIR/backup-*.sha256" "$LOCAL_BACKUP_DIR/"
```

**Note:** Ensure SSH key-based authentication is configured for unattended pulls.

## Troubleshooting

### Backup File Corruption

If a backup is corrupted (checksum mismatch):
1. Try the previous backup
2. If multiple backups are corrupted, check disk health: `sudo smartctl -a /dev/sda`
3. Consider restoring from off-site copy if available

### Database Lock During Backup

If the semantic-memory.db is locked during backup:
- SharpClaw temporarily holds locks during normal operation
- Backup tool will still capture a consistent snapshot
- If issues persist, shut down SharpClaw before backup

### Incomplete Recovery

If restored data seems incomplete:
1. Check that extraction completed without errors
2. Verify file permissions are correct: `ls -la /home/khughes/sharpclaw-workspace/`
3. Check SharpClaw logs for any initialization errors: `journalctl -u sharpclaw -n 100`

### Out of Disk Space

If backup directory runs out of space:
- Increase retention cleanup aggressiveness (reduce `days_to_keep`)
- Move older backups to off-site storage
- Expand disk if necessary

## Recovery SLA

| Scenario | RTO | RPO |
|----------|-----|-----|
| Recent backup available (< 24h old) | 30–45 min | 0–24 hours |
| All backups lost, recovery from git | 1–2 hours | Code only, no memory |
| Multiple backups corrupted | Manual intervention | Depends on off-site copies |

## Contacts & Escalation

For disaster recovery escalation:
- **Primary**: khughes@example.com
- **Backup**: Team Slack #sharpclaw-oncall

## See Also

- [Backup Solution Design](../ticket-009-backup-solution.md)
- [SharpClaw Architecture](../intro.md)
- [Workspace Configuration](../features/scaffolding.md)
