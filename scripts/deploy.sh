#!/usr/bin/env bash
# One-command deploy for the VPS: make the working tree exactly match the branch
# (immune to stray local edits - the recurring "local changes would be
# overwritten" pull failures), then build, verify and RESTART the bot.
#
# .env, bot.db and all other git-ignored state are untouched by the reset.
#
#   bash scripts/deploy.sh                 # pull + build + verify + restart
#   bash scripts/deploy.sh --no-start      # ... everything except the restart
#   bash scripts/deploy.sh --install-sdk   # ... fetching the .NET SDK if absent
#   bash scripts/deploy.sh --node          # deploy the Node ROLLBACK target instead
#
# RESTARTING IS THE DEFAULT, and it did not used to be. The old default pulled,
# built, verified and then deliberately changed nothing, on the reasoning that
# swapping a running bot should be a second conscious step. In practice the
# second step is the one that gets forgotten: `bash scripts/deploy.sh` looks
# like it worked, prints the commit it pulled, and leaves the old binary
# serving - so a fix appears not to have worked and the next hour goes on
# debugging code that was never running.
#
# The safety that actually mattered is not this default. It is the SELFTEST GATE
# in deploy-csharp.sh, which builds the whole object graph and refuses to swap
# the process when anything fails to resolve. That gate is untouched and has
# already stopped one broken build from reaching the server. Use --no-start when
# you genuinely want to build without deploying.
set -euo pipefail
cd "$(dirname "$0")/.."

BRANCH="claude/debug-optimize-code-dva08i"
MODE="csharp"
START=true
CS_ARGS=()

for arg in "$@"; do
  case "$arg" in
    --no-start)    START=false ;;
    # Accepted and ignored: it is what the default does now, and muscle memory
    # and any existing cron entry should keep working rather than erroring.
    --start)       START=true ;;
    --node)        MODE="node" ;;
    --install-sdk) CS_ARGS+=("$arg") ;;
    --keep-node)   CS_ARGS+=("$arg") ;;
    --*)           echo "unknown option: $arg" >&2; exit 2 ;;
    *)             BRANCH="$arg" ;;
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
    echo "Pulled and built. NOT restarted, because --no-start was given."
  fi
  echo "Deployed $(git log --oneline -1)"
  exit 0
fi

# The C# bot. deploy-csharp.sh owns the build, the selftest gate and the
# refuse-to-double-run check, so none of that is duplicated here.
# ${CS_ARGS[@]+...} because `set -u` treats an unset empty array as an error on
# older bash, and this runs on whatever bash the game host happens to ship.
if [ "$START" = true ]; then
  bash scripts/deploy-csharp.sh --start ${CS_ARGS[@]+"${CS_ARGS[@]}"}
else
  bash scripts/deploy-csharp.sh ${CS_ARGS[@]+"${CS_ARGS[@]}"}
  echo "Built and verified. NOT restarted, because --no-start was given."
fi

echo "Deployed $(git log --oneline -1)"

# ---- proof it is the new binary that is running -----------------------------
# "Did the deploy take?" has come up on nearly every fix in this branch, and the
# answer lived only in a log line at startup. pm2 restarting is not the same as
# the new build serving, so print what is actually up and where to confirm it.
if [ "$START" = true ]; then
  if command -v pm2 >/dev/null; then
    pm2 describe "$([ "$MODE" = node ] && echo pavlov-bot || echo pavlov-bot-cs)" 2>/dev/null \
      | grep -E "status|uptime|restarts" | head -3 || true
  fi
  echo
  echo "Confirm in Discord: /health shows 'build $(git rev-parse HEAD)' in its footer."
  echo "If that stamp is older than the commit above, the process was not replaced."
fi
