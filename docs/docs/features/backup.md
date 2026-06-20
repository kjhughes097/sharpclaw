---
sidebar_position: 18
---

# Backup & Disaster Recovery

SharpClaw includes an automated backup feature that produces a daily compressed snapshot of the workspace, plus a disaster-recovery procedure for restoring from one of those snapshots.

## What gets backed up

The backup contains everything under `SharpClaw:WorkspacePath` **except** any path listed in `Backup:ExcludeRelativePaths`. The default exclude is `coding/` (third-party project clones — recoverable from their own remotes).

SQLite databases listed in `Backup:SqliteRelativePaths` are not copied raw (which would risk capturing an inconsistent state mid-write). Instead, each one is copied via SQLite's online backup API (`SqliteConnection.BackupDatabase`) into a temp file and the temp file is added to the archive at the original relative path. WAL/SHM/journal sidecars are skipped.

Defaults:

```jsonc
{
  "Backup": {
    "Enabled": false,                              // off in the shipped appsettings.json
    "RootPath": "/srv/backups/sharpclaw",          // archives live here
    "CronExpression": "0 3 * * *",                 // 03:00 UTC daily
    "RetentionDays": 7,                            // older archives pruned
    "NotifyTelegramChatId": null,                  // chat to ping on failure
    "ExcludeRelativePaths": [ "coding" ],
    "SqliteRelativePaths": [ "token-usage.db", "data/semantic-memory.db" ]
  }
}
```

## Output

Each successful run drops two files into `RootPath`:

```
backup-YYYY-MM-DD-HHmm.tar.gz
backup-YYYY-MM-DD-HHmm.tar.gz.sha256
```

…and appends a JSON line to `RootPath/backup-log.jsonl` with the timestamp, archive size, sha256, file count, and duration. Retention runs append a line of `kind=retention` after each backup.

Failures are logged at error level **and** sent as a Telegram message to `NotifyTelegramChatId` if configured. The bot must already be a member of that chat/channel.

## Triggers

There are two ways to run a backup:

1. **Scheduled** — `BackupWorker` (a `BackgroundService`) wakes on the configured cron and runs backup → retention back-to-back. Enabled implicitly when `Backup:Enabled=true`.
2. **On demand** — the `run_backup` tool, registered globally:
   - `mode=backup` (default) — produce a real archive.
   - `mode=verify` — dry-run that builds the full tarball into a temp directory, hashes it, then deletes it. Use this to sanity-check that the workspace is backup-able without filling up `RootPath`.
   - `mode=retention` — prune old archives only.

Any agent with the tool available (any agent with `tools: null` or with `run_backup` listed) can invoke it.

## Filesystem permissions

`RootPath` must be writeable by the SharpClaw process. The default `/srv/backups/sharpclaw` is system-owned, so the one-time setup is:

```bash
sudo mkdir -p /srv/backups/sharpclaw
sudo chown $USER:$USER /srv/backups/sharpclaw
sudo chmod 750 /srv/backups/sharpclaw
```

For a personal install, `~/sharpclaw-backups` works just as well — point `Backup:RootPath` at it.

## Off-site copy

The shipped backup writes to local disk. To get an off-site copy, configure your remote machine to `scp`/`rsync` pull the archives out of `RootPath` on its own schedule (separate from SharpClaw). The sha256 sidecars let the remote verify integrity without trusting transport. This keeps SharpClaw out of the remote-credential business.

## Disaster recovery

To restore the workspace from a backup:

1. **Stop SharpClaw**:
   ```bash
   systemctl --user stop sharpclaw    # or however the service is run
   ```
2. **Verify the archive**:
   ```bash
   cd /srv/backups/sharpclaw
   sha256sum -c backup-2026-06-18-0300.tar.gz.sha256
   ```
3. **Move aside the existing workspace** (don't delete it until restore is verified):
   ```bash
   mv /home/khughes/sharpclaw-workspace /home/khughes/sharpclaw-workspace.broken
   mkdir /home/khughes/sharpclaw-workspace
   ```
4. **Extract** into the empty workspace path:
   ```bash
   tar -xzf backup-2026-06-18-0300.tar.gz -C /home/khughes/sharpclaw-workspace
   ```
5. **Re-attach `coding/`**: if any project clones from `coding/` are needed, re-clone them from their git remotes. They are intentionally excluded from backups.
6. **Start SharpClaw** and smoke-test:
   ```bash
   systemctl --user start sharpclaw
   curl http://localhost:5100/health
   ```
   Then send a message to one agent and confirm:
   - prior transcript history is visible in the web UI
   - `semantic_memory_count` returns a non-zero count
   - any scheduled tasks listed via `/api/tasks` still appear

If the smoke test passes, delete `sharpclaw-workspace.broken`. If anything is wrong, swap directories back and try an older archive.

## Periodic restore drill

Once a month, run a non-destructive verification:

```bash
# from any agent with the tool
run_backup mode=verify
```

This produces and hashes a full archive in a temp directory, then deletes it. A passing verify means the workspace is internally consistent (notably: the SQLite snapshots succeed) and the cron path is exercising the same code as a live backup.

For a true end-to-end restore drill, copy the latest archive to a scratch directory and extract it there:

```bash
mkdir /tmp/sc-restore-test
tar -xzf /srv/backups/sharpclaw/backup-*.tar.gz -C /tmp/sc-restore-test
ls /tmp/sc-restore-test    # should look like the workspace minus coding/
sqlite3 /tmp/sc-restore-test/data/semantic-memory.db "SELECT COUNT(*) FROM memories;"
rm -rf /tmp/sc-restore-test
```

## Troubleshooting

- **No archives appearing**: confirm `Backup:Enabled=true`, the cron expression is valid (5-field standard cron, UTC), and `BackupWorker started` appears in the logs at startup.
- **`UnauthorizedAccessException`**: `RootPath` isn't writeable by the SharpClaw user. Fix permissions.
- **`Workspace path does not exist`**: `SharpClaw:WorkspacePath` is unset or wrong. Backups have nothing to capture without it.
- **Telegram failure messages not arriving**: the bot must be a member of the chat referenced by `NotifyTelegramChatId`. Note this is checked separately from `Telegram:AllowedChatIds`, which only governs the `send_telegram` tool.
