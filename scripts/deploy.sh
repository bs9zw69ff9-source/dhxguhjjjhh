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

# The C# bot. deploy-csharp.sh owns the build and the selftest gate, so none of
# that is duplicated here. BUILD AND VERIFY ONLY: it never starts anything. The
# systemd restart below is the one thing that puts the new binary live.
# ${CS_ARGS[@]+...} because `set -u` treats an unset empty array as an error on
# older bash, and this runs on whatever bash the game host happens to ship.
bash scripts/deploy-csharp.sh ${CS_ARGS[@]+"${CS_ARGS[@]}"}
if [ "$START" != true ]; then
  echo "Built and verified. NOT restarted, because --no-start was given."
fi

# ---- the bot: the systemd unit pavlov-bot -----------------------------------
# IT KEEPS SERVING THE OLD BINARY until its process is replaced. Forgetting that
# step is exactly what "the fix is deployed and the bot is not doing it" looks
# like, and it has cost days.
#
# ONLY IF THE UNIT RUNS THIS BUILD. Restarting a unit whose ExecStart points
# somewhere else takes the bot down and brings the OLD binary back - downtime for
# nothing, reported as a deploy. So the unit's ExecStart is read first and the
# restart is refused, loudly, unless it is this checkout's published binary.
#
# (This used to restart the pm2 app pavlov-bot-fallout. The fallout bot was
# dropped on 2026-11-17; the bot is the systemd unit.)
BOT_UNIT="${BOT_UNIT:-pavlov-bot}"
BUILT="$(pwd)/dotnet-out/PavlovBot"
if [ "$START" = true ]; then
  if ! command -v systemctl >/dev/null 2>&1 || ! systemctl cat "$BOT_UNIT" >/dev/null 2>&1; then
    echo "WARNING: there is no systemd unit $BOT_UNIT on this host, so nothing was restarted." >&2
    echo "  The new build is at $BUILT. Start the bot from it yourself." >&2
  else
    EXEC_START="$(systemctl show -p ExecStart --value "$BOT_UNIT" 2>/dev/null || true)"
    case "$EXEC_START" in
      *"$BUILT"*)
        echo "==> Restarting $BOT_UNIT"
        # Not fatal: a restart that fails is worth saying loudly and not worth
        # undoing a good deploy over.
        if ! systemctl restart "$BOT_UNIT"; then
          echo "WARNING: $BOT_UNIT did not restart. It is still serving the OLD binary." >&2
          echo "  Retry with:  systemctl restart $BOT_UNIT   then  journalctl -u $BOT_UNIT -n 50" >&2
        fi
        ;;
      *)
        echo "WARNING: $BOT_UNIT does not run this build, so it was NOT restarted." >&2
        echo "  This build:          $BUILT" >&2
        echo "  The unit's ExecStart: ${EXEC_START:-(unreadable)}" >&2
        echo "  See it with:  systemctl cat $BOT_UNIT" >&2
        ;;
    esac
  fi
fi

echo "Deployed $(git log --oneline -1)"


# ---- proof it is the new binary that is running -----------------------------
# "Did the deploy take?" has come up on nearly every fix, and the answer lived
# only in a log line at startup. A restart is not the same as the new build
# serving, so print what is actually up and where to confirm it.
if [ "$START" = true ]; then
  if command -v systemctl >/dev/null 2>&1 && systemctl cat "$BOT_UNIT" >/dev/null 2>&1; then
    echo "--- $BOT_UNIT"
    systemctl show -p ActiveState -p SubState -p ActiveEnterTimestamp -p NRestarts "$BOT_UNIT" 2>/dev/null || true
  fi
  echo
  echo "Confirm in Discord: /health shows 'build $(git rev-parse HEAD)' in its footer."
  echo "If that stamp is older than the commit above, the process was not replaced."
fi
