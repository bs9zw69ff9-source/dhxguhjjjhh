#!/usr/bin/env bash
# One-command deploy for the VPS: make the working tree exactly match the branch
# (immune to stray local edits - the recurring "local changes would be
# overwritten" pull failures), then restart the bot. .env, bot.db and all other
# git-ignored state are untouched by the reset.
#   usage:  bash scripts/deploy.sh
set -euo pipefail
BRANCH="${1:-claude/debug-optimize-code-dva08i}"
cd "$(dirname "$0")/.."
echo "Deploying origin/$BRANCH ..."
git fetch origin "$BRANCH"
git reset --hard "origin/$BRANCH"
if command -v npm >/dev/null && ! diff -q <(git show "HEAD:package.json") package.json.deployed 2>/dev/null >/dev/null; then
  npm install --omit=dev --no-audit --no-fund
  cp package.json package.json.deployed
fi
pm2 restart pavlov-bot
echo "Deployed $(git log --oneline -1)"
