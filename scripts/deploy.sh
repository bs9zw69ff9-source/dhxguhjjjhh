#!/usr/bin/env bash
# One-command deploy for the VPS: make the working tree exactly match the branch
# (immune to stray local edits - the recurring "local changes would be
# overwritten" pull failures), then build and restart the C# bot.
#
# .env, bot.db and all other git-ignored state are untouched by the reset.
#
#   bash scripts/deploy.sh                 # pull + build + verify, start nothing
#   bash scripts/deploy.sh --start         # ... and restart the bot
#   bash scripts/deploy.sh --node --start  # deploy the Node ROLLBACK target instead
set -euo pipefail
cd "$(dirname "$0")/.."

BRANCH="claude/debug-optimize-code-dva08i"
MODE="csharp"
START=false

for arg in "$@"; do
  case "$arg" in
    --start) START=true ;;
    --node)  MODE="node" ;;
    --*)     echo "unknown option: $arg" >&2; exit 2 ;;
    *)       BRANCH="$arg" ;;
  esac
done

echo "Deploying origin/$BRANCH ($MODE) ..."
git fetch origin "$BRANCH"
git reset --hard "origin/$BRANCH"

if [ "$MODE" = "node" ]; then
  # The rollback target. Kept working on purpose - a rollback nobody exercises
  # is not a rollback.
  if command -v npm >/dev/null && ! diff -q <(git show "HEAD:package.json") package.json.deployed 2>/dev/null >/dev/null; then
    npm install --omit=dev --no-audit --no-fund
    cp package.json package.json.deployed
  fi
  if [ "$START" = true ]; then
    pm2 stop pavlov-bot-cs 2>/dev/null || true   # never both at once
    pm2 restart pavlov-bot
  else
    echo "Pulled. Nothing restarted - add --start."
  fi
  echo "Deployed $(git log --oneline -1)"
  exit 0
fi

# The C# bot. deploy-csharp.sh owns the build, the selftest gate and the
# refuse-to-double-run check, so none of that is duplicated here.
if [ "$START" = true ]; then
  bash scripts/deploy-csharp.sh --start
else
  bash scripts/deploy-csharp.sh
fi

echo "Deployed $(git log --oneline -1)"
