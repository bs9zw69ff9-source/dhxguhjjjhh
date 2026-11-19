#!/usr/bin/env bash
# One-command deploy for the VPS: make the working tree exactly match the branch
# (immune to stray local edits - the recurring "local changes would be
# overwritten" pull failures), then build, verify and RESTART the bot.
#
# .env, bot.db and all other git-ignored state are untouched by the reset.
#
#   bash scripts/deploy.sh                  # pull + build + selftest + restart
#   bash scripts/deploy.sh --no-start       # ... everything except the restart
#   bash scripts/deploy.sh --install-rust   # ... fetching a Rust toolchain if absent
#
# THE BOT IS THE RUST BOT. The C# bot was removed from the repo on 2026-11-19, so
# this builds rust/ through scripts/deploy-rust.sh, which owns the build, the
# selftest gate, the binary swap, the restart and the build-stamp check. Nothing
# of that is duplicated here.
#
# Rolling back means deploying an earlier commit. A sha or tag is checked out
# DETACHED; a branch name is checked out as that branch:
#   bash scripts/deploy.sh 1760d55        # roll back to a commit
#   bash scripts/deploy.sh main           # and back to the trunk afterwards
# Do not roll back past 1760d55: earlier commits predate the ServerRoles and JSON-save
# server, and their bot does not match it.
set -euo pipefail
cd "$(dirname "$0")/.."

# THE WHOLE SCRIPT IS ONE FUNCTION, CALLED ON THE LAST LINE. Bash reads a script as it runs
# it, and the checkout below replaces this very file. Before this was a function, bash went
# on executing the OLD copy from its open file after the checkout, so a deploy that changed
# deploy.sh ran half of each (2026-11-19: it called the deleted deploy-csharp.sh). A function
# is parsed whole before it starts, and a changed copy is re-executed after the checkout.
main() {

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
RUST_ARGS=()

CHECKED_OUT=false
for arg in "$@"; do
  case "$arg" in
    # Internal: set when this script re-executes itself after the checkout changed it.
    --checked-out) CHECKED_OUT=true ;;
    --no-start)    START=false ;;
    # Accepted and ignored: it is what the default does now, and muscle memory
    # and any existing cron entry should keep working rather than erroring.
    --start)       START=true ;;
    --install-rust) RUST_ARGS+=("$arg") ;;
    --install-sdk) echo "--install-sdk was for the C# bot, which is gone. Use --install-rust." >&2; exit 2 ;;
    --*)           echo "unknown option: $arg" >&2; exit 2 ;;
    *)             BRANCH="$arg" ;;
  esac
done

# A BRANCH OR A COMMIT, because rolling back is the case you are in when you are
# least able to debug the deploy script. This documented `deploy.sh <older-sha>`
# from the day it was written and never supported it: `git fetch origin <sha>`
# fails with "couldn't find remote ref", because a sha is not a remote ref. The
# one command you reach for in an outage was the one that did not work.
if [ "$CHECKED_OUT" != true ]; then
check_out "$@"
fi
build_and_restart
}

# Fetch, check out and clean, then re-execute the checked-out deploy.sh if it differs from
# the one running. Re-executing only a copy that knows --checked-out keeps a rollback to an
# older commit working: that copy cannot skip the fetch, so the running one carries on.
check_out() {
local before
before="$(git hash-object scripts/deploy.sh)"

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

# "Exactly match the branch" has to include files the branch does not have: a source file
# deleted upstream is still on disk after a checkout and still gets built. ONLY THE SOURCE
# TREES ARE CLEANED. A bare `git clean -fd` once deleted 16 JSON exports from data/, because
# .gitignore named datasets one by one and newer ones were missing (2026-11-19; bot.db, the
# real store, was ignored and survived). Runtime state lives outside these paths, and no -x,
# so ignored files inside them (rust/target, .env) are untouched.
git clean -fd -- rust scripts .github

if [ "$(git hash-object scripts/deploy.sh)" != "$before" ] \
   && grep -q -- '--checked-out)' scripts/deploy.sh; then
  echo "deploy.sh changed in this checkout; running the new copy ..."
  exec bash scripts/deploy.sh --checked-out "$@"
fi
}

build_and_restart() {

# ---- the bot: the systemd unit pavlov-bot -----------------------------------
# deploy-rust.sh builds, runs the selftest against this .env, and swaps the binary
# only if that passes. With --start it restarts the unit, but only after checking
# that the unit's ExecStart IS this checkout's rust-out/pavlov-bot, and then waits
# for the journal to report this commit's build stamp. A unit that runs some other
# binary is refused loudly instead of being restarted for nothing.
# ${RUST_ARGS[@]+...} because `set -u` treats an unset empty array as an error on
# older bash, and this runs on whatever bash the game host happens to ship.
BOT_UNIT="${BOT_UNIT:-pavlov-bot}"
if [ "$START" = true ]; then
  bash scripts/deploy-rust.sh --start --unit "$BOT_UNIT" ${RUST_ARGS[@]+"${RUST_ARGS[@]}"}
else
  bash scripts/deploy-rust.sh ${RUST_ARGS[@]+"${RUST_ARGS[@]}"}
  echo "Built and verified. NOT restarted, because --no-start was given."
fi

echo "Deployed $(git log --oneline -1)"
if [ "$START" = true ]; then
  echo "Confirm in Discord: /health shows 'build $(git rev-parse --short HEAD)' in its footer."
fi
}

main "$@"
exit $?
