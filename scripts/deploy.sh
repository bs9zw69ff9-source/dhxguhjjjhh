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
#
# THE NODE ROLLBACK TARGET IS GONE. It was removed once the C# bot had taken
# over, so there is no --node mode and no second app to keep out of the way.
# Rolling back now means deploying an earlier commit. A sha or tag is checked out
# DETACHED; a branch name is checked out as that branch:
#   bash scripts/deploy.sh 4577811        # roll back to a commit
#   bash scripts/deploy.sh main           # and back to the trunk afterwards
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
# MAIN IS THE TRUNK AGAIN. The work lived on claude/* branches for a while and
# this pointed at whichever of them actually held the fixes, because deploying a
# branch that did not would have built and shipped the wrong tree, successfully
# and silently. Those branches have been folded into main, so there is one answer
# to "what does production run" and it has the obvious name.
#
# Keep it that way. If a working branch needs deploying, pass it for that one run
# rather than editing this:
#   bash scripts/deploy.sh some/branch      (or DEPLOY_BRANCH=some/branch)
BRANCH="${DEPLOY_BRANCH:-main}"
START=true
CS_ARGS=()

for arg in "$@"; do
  case "$arg" in
    --no-start)    START=false ;;
    # Accepted and ignored: it is what the default does now, and muscle memory
    # and any existing cron entry should keep working rather than erroring.
    --start)       START=true ;;
    --install-sdk) CS_ARGS+=("$arg") ;;
    --*)           echo "unknown option: $arg" >&2; exit 2 ;;
    *)             BRANCH="$arg" ;;
  esac
done

# A BRANCH OR A COMMIT, because rolling back is the case you are in when you are
# least able to debug the deploy script. This documented `deploy.sh <older-sha>`
# from the day it was written and never supported it: `git fetch origin <sha>`
# fails with "couldn't find remote ref", because a sha is not a remote ref. The
# one command you reach for in an outage was the one that did not work.
echo "Fetching ..."
if git fetch origin "$BRANCH" 2>/dev/null; then
  :
else
  # Not a branch name. Fetch everything and resolve it locally - a sha, a tag, or
  # a typo, and the three are told apart below rather than all failing the same way.
  git fetch origin
fi

if git rev-parse --verify --quiet "refs/remotes/origin/$BRANCH" >/dev/null; then
  echo "Deploying branch origin/$BRANCH ..."

  # CHECKOUT, NOT JUST RESET. `git reset --hard origin/$BRANCH` moves whatever branch
  # happens to be checked out to point at $BRANCH's commit, without saying so. Deploy
  # a branch while the box sits on another one and the local ref is silently rewritten,
  # so the next `git status` describes a branch that no longer contains what its name
  # says. -f discards local edits, which is the documented intent of this script.
  git checkout -f -B "$BRANCH" "origin/$BRANCH"
elif git rev-parse --verify --quiet "${BRANCH}^{commit}" >/dev/null; then
  # DETACHED ON PURPOSE. A rollback is a deliberate visit to an old commit, not a
  # new branch, and creating one here would leave a local ref that the next deploy
  # of the same name silently reuses.
  echo "Deploying commit $BRANCH (detached - this is a rollback) ..."
  git checkout -f --detach "$BRANCH"
  echo "NOTE: the working tree is DETACHED. Return to the trunk with:  bash scripts/deploy.sh main"
else
  echo "'$BRANCH' is neither a branch on origin nor a commit in this repository." >&2
  echo "Branches:  git branch -r" >&2
  echo "Recent commits:  git log --oneline -20" >&2
  exit 2
fi

# "Exactly match the branch" has to include files the branch does not have. A source
# file deleted upstream is still on disk after a reset and is still what `require`
# resolves, so the bot keeps running code that is no longer in the repo. Ignored
# state (.env, bot.db and the rest) is untouched: no -x.
git clean -fd

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

# ---- the second bot ---------------------------------------------------------
# BOTH BOTS RUN THE SAME PUBLISHED OUTPUT, so the build above is already the
# clone's build too - but it keeps serving the OLD binary until its own process
# is replaced. That step lived only in SECOND-BOT.md as a thing to remember, and
# "the fix works on one bot and not the other" is exactly what forgetting it
# looks like.
#
# THE ECOSYSTEM FILE, NOT THE APP NAME. `pm2 restart pavlov-bot-fallout`
# restarts the process without re-reading the config, so a change to
# kill_timeout or the env block would deploy and never apply - the same trap
# deploy-csharp.sh documents for the main bot.
#
# Skipped without complaint on a box that does not run a clone: most do not, and
# a missing second bot is a deployment choice rather than a fault.
if [ "$START" = true ] && command -v pm2 >/dev/null; then
  if pm2 describe pavlov-bot-fallout >/dev/null 2>&1; then
    echo "==> Restarting pavlov-bot-fallout (it runs the same build)"

    # Not fatal. The main bot is already up and verified by this point, so a
    # clone that will not restart is worth saying loudly and not worth undoing
    # a good deploy over.
    if pm2 restart ecosystem.fallout.config.js --update-env; then
      pm2 save >/dev/null 2>&1 || true
    else
      echo "WARNING: pavlov-bot-fallout did not restart. It is still serving the OLD binary." >&2
      echo "  Retry with:  pm2 restart ecosystem.fallout.config.js --update-env" >&2
    fi
  else
    echo "No pavlov-bot-fallout under pm2 on this host - nothing else to restart."
  fi
fi

# ---- proof it is the new binary that is running -----------------------------
# "Did the deploy take?" has come up on nearly every fix in this branch, and the
# answer lived only in a log line at startup. pm2 restarting is not the same as
# the new build serving, so print what is actually up and where to confirm it.
if [ "$START" = true ]; then
  if command -v pm2 >/dev/null; then
    for app in pavlov-bot-cs pavlov-bot-fallout; do
      if pm2 describe "$app" >/dev/null 2>&1; then
        echo "--- $app"
        pm2 describe "$app" 2>/dev/null \
          | grep -E "status|uptime|restarts" | head -3 || true
      fi
    done
  fi
  echo
  echo "Confirm in Discord: /health shows 'build $(git rev-parse HEAD)' in its footer."
  echo "If that stamp is older than the commit above, the process was not replaced."
fi
