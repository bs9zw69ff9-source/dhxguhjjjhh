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

# Overridable without editing the script, so a branch rename does not mean editing
# a file on the box: DEPLOY_BRANCH=main bash scripts/deploy.sh
#
# BACK ON THE TRUNK. This briefly pointed at a working branch, because the fixes that
# mattered were sitting on one and unmerged - deploying the trunk then would have
# built and shipped a tree containing none of them, successfully and silently. They
# merged in #4, so there is one answer again to "what does production run".
#
# Keep it that way. Two refs that both claim to be what production runs is the
# confusion this repo already carries between `main`, `Main` and the claude/*
# branches, and a deploy script is the worst place to leave it unresolved. If a
# working branch needs deploying, pass it for that one run rather than editing this:
#   bash scripts/deploy.sh some/branch      (or DEPLOY_BRANCH=some/branch)
BRANCH="${DEPLOY_BRANCH:-claude/debug-optimize-code-dva08i}"
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

# Flags that only mean something to the C# path. Silently accepting them in --node
# mode makes `--node --install-sdk` look like it did something.
if [ "$MODE" = "node" ] && [ ${#CS_ARGS[@]} -gt 0 ]; then
  echo "note: ${CS_ARGS[*]} only applies to the C# deploy and is ignored with --node" >&2
fi

echo "Deploying origin/$BRANCH ($MODE) ..."
git fetch origin "$BRANCH"

# CHECKOUT, NOT JUST RESET. `git reset --hard origin/$BRANCH` moves whatever branch
# happens to be checked out to point at $BRANCH's commit, without saying so. Deploy
# a branch while the box sits on another one and the local ref is silently rewritten,
# so the next `git status` describes a branch that no longer contains what its name
# says. -f discards local edits, which is the documented intent of this script.
git checkout -f -B "$BRANCH" "origin/$BRANCH"

# "Exactly match the branch" has to include files the branch does not have. A source
# file deleted upstream is still on disk after a reset and is still what `require`
# resolves, so the bot keeps running code that is no longer in the repo. Ignored
# state (.env, bot.db, package.json.deployed) is untouched: no -x.
git clean -fd

if [ "$MODE" = "node" ]; then
  # The rollback target. Kept working on purpose - a rollback nobody exercises
  # is not a rollback.
  if command -v npm >/dev/null && ! diff -q <(git show "HEAD:package.json") package.json.deployed 2>/dev/null >/dev/null; then
    npm install --omit=dev --no-audit --no-fund
    cp package.json package.json.deployed
  fi
  if [ "$START" = true ]; then
    pm2 stop pavlov-bot-cs 2>/dev/null || true   # never both at once

    # `pm2 restart <app>` FAILS WHEN THE APP IS NOT IN THE PROCESS LIST, and under
    # `set -e` that aborts the deploy - after the working tree has already been
    # reset. That is not an edge case here: the C# deploy stops `pavlov-bot`, so
    # the app is stopped in exactly the situation this path exists for, and every
    # rollback attempt died on this line. deploy-csharp.sh already handles the
    # same case; this is the half that never learned it.
    #
    # --update-env so a changed .env is actually picked up rather than the process
    # coming back with the environment it was first started with.
    if pm2 describe pavlov-bot >/dev/null 2>&1; then
      echo "==> Restarting pavlov-bot (it was already running)"
      pm2 restart pavlov-bot --update-env
    else
      echo "==> Starting pavlov-bot (it was not running)"
      pm2 start index.js --name pavlov-bot --update-env
    fi

    # Persist the list, or a reboot resurrects the pm2 snapshot from before this
    # deploy - which is the C# bot, i.e. the thing being rolled back FROM.
    pm2 save
  else
    echo "Pulled and built. NOT restarted, because --no-start was given."
  fi
else

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
