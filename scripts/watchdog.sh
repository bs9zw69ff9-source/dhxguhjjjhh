#!/usr/bin/env bash
#
# watchdog.sh - tell a Discord channel when the bot itself goes down, and when it is back.
#
# The bot's own monitoring (the /monitor board, security DMs) runs INSIDE the bot, so it goes
# silent exactly when the bot does. This runs from cron, outside it, and asks the bot's
# health endpoint whether it is alive.
#
# It posts on a CHANGE of state only - down once, recovered once - never every minute.
#
# ── One-time setup ──────────────────────────────────────────────────────────
#   1. In the bot's .env, enable the endpoint:   METRICS_PORT=9464
#      (it binds 127.0.0.1 by default, which is all this needs)
#   2. Create a webhook in the channel that should hear about outages.
#   3. cp scripts/watchdog.env.example watchdog.env   and set WATCHDOG_WEBHOOK
#   4. Root's crontab, every minute:
#        * * * * * /root/pavlov-bot/scripts/watchdog.sh >> /var/log/pavlov-watchdog.log 2>&1
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(dirname "$SCRIPT_DIR")"
[ -f "$REPO_DIR/watchdog.env" ] && . "$REPO_DIR/watchdog.env"

WATCHDOG_WEBHOOK="${WATCHDOG_WEBHOOK:-}"                    # Discord webhook URL (a credential)
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:9464/healthz}"   # /healthz needs no token
BOT_NAME="${BOT_NAME:-Pavlov bot}"
FAILURES_BEFORE_ALERT="${FAILURES_BEFORE_ALERT:-3}"         # minutes; a restart takes ~30s
STATE="${STATE:-/tmp/pavlov-watchdog.state}"

log() { echo "[$(date -u '+%Y-%m-%dT%H:%M:%SZ')] watchdog: $*"; }

[ -n "$WATCHDOG_WEBHOOK" ] || { log "WATCHDOG_WEBHOOK is not set (edit $REPO_DIR/watchdog.env)"; exit 1; }

post() {
  # The message is built here, never from anything the bot returned, so it needs no escaping
  # beyond the plain text below.
  curl -fsS -m 10 -H 'Content-Type: application/json' \
    -d "{\"content\":\"$1\",\"allowed_mentions\":{\"parse\":[]}}" \
    "$WATCHDOG_WEBHOOK" >/dev/null || log "could not post to the webhook"
}

failures=0
alerted=0
if [ -f "$STATE" ]; then read -r failures alerted < "$STATE" || true; fi

if curl -fsS -m 5 "$HEALTH_URL" >/dev/null 2>&1; then
  if [ "$alerted" = 1 ]; then
    post ":white_check_mark: **$BOT_NAME is back** - its health endpoint is answering again."
    log "recovered"
  fi
  echo "0 0" > "$STATE"
  exit 0
fi

failures=$((failures + 1))
if [ "$failures" -ge "$FAILURES_BEFORE_ALERT" ] && [ "$alerted" != 1 ]; then
  post ":red_circle: **$BOT_NAME is down** - $HEALTH_URL has not answered for $failures minute(s). Check \`pm2 status\` and \`pm2 logs\` on the host."
  alerted=1
  log "DOWN - alerted after $failures failed check(s)"
else
  log "health check failed ($failures)"
fi
echo "$failures $alerted" > "$STATE"
