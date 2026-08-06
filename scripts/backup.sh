#!/usr/bin/env bash
#
# backup.sh — off-site backup of the bot's data to cloud storage via rclone.
#
# Designed to run from system cron (independent of the bot, so it keeps working
# even if the bot is crashed or stopped). It takes a CONSISTENT snapshot of the
# SQLite database — safe to run while the bot is writing (WAL mode) — using the
# bot's own better-sqlite3 online-backup, so no extra system packages are needed
# beyond rclone. Also folds in the human-readable JSON exports.
#
# ── One-time setup ──────────────────────────────────────────────────────────
#   1. Install rclone:            curl https://rclone.org/install.sh | sudo bash
#   2. Configure a remote:        rclone config          # pick B2 / S3 / R2 / Drive…
#   3. Copy the config template:  cp scripts/backup.env.example backup.env
#      then edit backup.env and set RCLONE_REMOTE (e.g. "b2:my-bucket/pavlov-bot")
#   4. Add to root's crontab (hourly):
#        0 * * * * /root/pavlov-bot/scripts/backup.sh >> /var/log/pavlov-backup.log 2>&1
#
# ── Restore (see the bottom of this file) ───────────────────────────────────
#
set -euo pipefail

# Resolve the bot directory as the parent of this script's dir, so it works
# regardless of where cron invokes it from.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BOT_DIR="${BOT_DIR:-$(dirname "$SCRIPT_DIR")}"

# Load config (gitignored) if present — lets you set RCLONE_REMOTE etc. out of git.
[ -f "$BOT_DIR/backup.env" ] && . "$BOT_DIR/backup.env"

RCLONE_REMOTE="${RCLONE_REMOTE:-}"        # e.g. "b2:my-bucket/pavlov-bot-backups"
RETENTION_DAYS="${RETENTION_DAYS:-30}"    # prune remote backups older than this
WORK="${WORK:-/tmp/pavlov-bot-backup}"
DB="$BOT_DIR/bot.db"

log() { echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] backup: $*"; }
fail() { log "ERROR: $*"; exit 1; }

[ -n "$RCLONE_REMOTE" ] || fail "RCLONE_REMOTE is not set (edit $BOT_DIR/backup.env)"
command -v rclone >/dev/null 2>&1 || fail "rclone is not installed (curl https://rclone.org/install.sh | sudo bash)"
[ -f "$DB" ] || fail "database not found at $DB"

# Single-instance lock so overlapping cron runs can't collide.
LOCK="$WORK/.lock"
mkdir -p "$WORK"
exec 9>"$LOCK"
flock -n 9 || { log "another backup is already running — skipping"; exit 0; }

STAMP="$(date -u +%Y%m%d-%H%M%S)"
SNAP="$WORK/botdb-$STAMP.db"

# ── Consistent SQLite snapshot ──────────────────────────────────────────────
# The first choice used to be the Node bot's better-sqlite3 online backup. That bot
# and its node_modules are gone, so the branch could only ever fail - silently, into
# a plain cp, which is the one option that is NOT WAL-safe.
#
# sqlite3's .backup is the WAL-safe route now. The plain copy stays as a last resort
# because a slightly risky backup beats no backup, and it says so in the log rather
# than pretending it was clean.
if command -v sqlite3 >/dev/null 2>&1; then
  sqlite3 "$DB" ".backup '$SNAP'" && log "snapshot via sqlite3 .backup"
else
  cp "$DB" "$SNAP" && log "snapshot via plain cp - NOT WAL-safe, install sqlite3 for a consistent copy"
fi
[ -s "$SNAP" ] || fail "snapshot is empty"

gzip -f "$SNAP"                      # -> $SNAP.gz
ARCHIVE="$SNAP.gz"

# ── Bonus: the human-readable JSON exports (small, text) ────────────────────
JSON_TAR="$WORK/botjson-$STAMP.tar.gz"
if ls "$BOT_DIR"/*.json >/dev/null 2>&1; then
  ( cd "$BOT_DIR" && tar -czf "$JSON_TAR" ./*.json ) || true
fi

# ── Upload ──────────────────────────────────────────────────────────────────
rclone copy "$ARCHIVE" "$RCLONE_REMOTE/" --no-traverse || fail "rclone upload failed"
[ -s "$JSON_TAR" ] && rclone copy "$JSON_TAR" "$RCLONE_REMOTE/" --no-traverse || true
log "uploaded $(basename "$ARCHIVE")$( [ -s "$JSON_TAR" ] && echo " + $(basename "$JSON_TAR")" ) to $RCLONE_REMOTE"

# ── Retention: prune old remote backups ─────────────────────────────────────
rclone delete --min-age "${RETENTION_DAYS}d" "$RCLONE_REMOTE/" 2>/dev/null \
  && log "pruned remote backups older than ${RETENTION_DAYS}d" || true

# ── Clean local temp ────────────────────────────────────────────────────────
rm -f "$ARCHIVE" "$JSON_TAR"
log "done"

# ── Restore (manual) ────────────────────────────────────────────────────────
#   pm2 stop pavlov-bot
#   rclone lsf "$RCLONE_REMOTE/"                         # list backups, pick one
#   rclone copy "$RCLONE_REMOTE/botdb-YYYYMMDD-HHMMSS.db.gz" /tmp/
#   gunzip -c /tmp/botdb-YYYYMMDD-HHMMSS.db.gz > /root/pavlov-bot/bot.db
#   rm -f /root/pavlov-bot/bot.db-wal /root/pavlov-bot/bot.db-shm   # drop stale WAL
#   pm2 start pavlov-bot
