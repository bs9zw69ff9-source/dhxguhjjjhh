#!/usr/bin/env bash
#
# backup.sh — off-site backup of the bot's data to cloud storage via rclone.
#
# Designed to run from system cron (independent of the bot, so it keeps working
# even if the bot is crashed or stopped). It takes a CONSISTENT snapshot of the
# SQLite database — safe to run while the bot is writing (WAL mode) — using
# sqlite3's .backup. Also folds in the human-readable JSON exports.
#
# WHICH DATABASE. The bot keeps bot.db in DATA_DIR, which defaults to data/ under
# the directory it RUNS in (its pm2 cwd) - not the repo. The live bot runs in
# /root/pavlov-bot-fallout, so that is BOT_DIR there. This script used to back up
# <repo>/bot.db, a file the C# bot never writes: if a Node-era copy was still
# sitting there, every hourly backup "succeeded" while saving stale data.
#
# ── One-time setup ──────────────────────────────────────────────────────────
#   1. Install rclone:            curl https://rclone.org/install.sh | sudo bash
#   2. Configure a remote:        rclone config          # pick B2 / S3 / R2 / Drive…
#   3. Copy the config template:  cp scripts/backup.env.example backup.env
#      then edit backup.env and set RCLONE_REMOTE (e.g. "b2:my-bucket/pavlov-bot")
#   4. Add to root's crontab (hourly), naming the directory the bot RUNS in:
#        0 * * * * BOT_DIR=/root/pavlov-bot-fallout /root/pavlov-bot/scripts/backup.sh >> /var/log/pavlov-backup.log 2>&1
#
# ── Restore (see the bottom of this file) ───────────────────────────────────
#
set -euo pipefail

# BOT_DIR is the directory the bot RUNS in (where its .env and data/ live). It
# defaults to the parent of this script's dir, which is only right when the bot
# runs from the repo itself.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
BOT_DIR="${BOT_DIR:-$REPO_DIR}"

# Load config (gitignored) if present — lets you set RCLONE_REMOTE etc. out of git.
if [ -f "$BOT_DIR/backup.env" ]; then . "$BOT_DIR/backup.env"
elif [ -f "$REPO_DIR/backup.env" ]; then . "$REPO_DIR/backup.env"; fi

RCLONE_REMOTE="${RCLONE_REMOTE:-}"        # e.g. "b2:my-bucket/pavlov-bot-backups"
RETENTION_DAYS="${RETENTION_DAYS:-30}"    # prune remote backups older than this
WORK="${WORK:-/tmp/pavlov-bot-backup}"

log() { echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] backup: $*"; }
fail() { log "ERROR: $*"; exit 1; }

# DATA_DIR exactly as the bot resolves it: the environment, else its .env, else
# data/ - relative paths being relative to the directory the bot runs in.
if [ -z "${DATA_DIR:-}" ] && [ -f "$BOT_DIR/.env" ]; then
  DATA_DIR="$(sed -n -E 's/^[[:space:]]*DATA_DIR[[:space:]]*=[[:space:]]*//p' "$BOT_DIR/.env" | tail -n 1 \
              | sed -E 's/[[:space:]]+#.*$//; s/^["'\'']//; s/["'\'']$//')"
fi
DATA_DIR="${DATA_DIR:-data}"
case "$DATA_DIR" in /*) ;; *) DATA_DIR="$BOT_DIR/$DATA_DIR" ;; esac
DB="$DATA_DIR/bot.db"

[ -n "$RCLONE_REMOTE" ] || fail "RCLONE_REMOTE is not set (edit $BOT_DIR/backup.env)"
command -v rclone >/dev/null 2>&1 || fail "rclone is not installed (curl https://rclone.org/install.sh | sudo bash)"
[ -f "$DB" ] || fail "database not found at $DB - set BOT_DIR to the directory the bot runs in (its pm2 cwd), or DATA_DIR"

# A live bot writes its database every minute. One untouched for a day is almost
# certainly not the database the running bot uses - say so rather than back it up
# quietly forever.
if [ -n "$(find "$DB" -mmin +1440 2>/dev/null)" ]; then
  log "WARNING: $DB has not been written for over 24h - is BOT_DIR ($BOT_DIR) where the bot actually runs?"
fi
if [ -f "$BOT_DIR/bot.db" ] && [ "$BOT_DIR/bot.db" != "$DB" ]; then
  log "note: $BOT_DIR/bot.db is a leftover from the Node bot and is NOT backed up; the live database is $DB"
fi

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
if ls "$DATA_DIR"/*.json >/dev/null 2>&1; then
  ( cd "$DATA_DIR" && tar -czf "$JSON_TAR" ./*.json ) || true
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
#   pm2 stop pavlov-bot-fallout
#   rclone lsf "$RCLONE_REMOTE/"                         # list backups, pick one
#   rclone copy "$RCLONE_REMOTE/botdb-YYYYMMDD-HHMMSS.db.gz" /tmp/
#   gunzip -c /tmp/botdb-YYYYMMDD-HHMMSS.db.gz > /root/pavlov-bot-fallout/data/bot.db
#   rm -f /root/pavlov-bot-fallout/data/bot.db-wal /root/pavlov-bot-fallout/data/bot.db-shm   # drop stale WAL
#   pm2 start pavlov-bot-fallout
