#!/usr/bin/env bash
# WHY IS THE BOT NOT DOING THE THING I JUST DEPLOYED?
#
#   bash scripts/diagnose.sh
#
# Prints, in one pass, every fact that has actually been the answer to that
# question on this repo: which commit the repo is on, which binary is running,
# whether the process was replaced, which faction ladders got loaded, which
# sub-classes are in them, where the commands were registered, and what is on
# disk in the roster directory.
#
# WHY IT EXISTS. Every one of those lives somewhere different - git, pm2, a
# startup log line, a .env, the game's config directory - and chasing them one
# at a time across a chat window has cost days. The failures it has caught so
# far, none of which were visible from the code:
#
#   a published binary older than the commit (deploy built, never restarted)
#   a bot crash-looping on a config error, so its commands never re-register
#   ladders loaded from a JSON copy on the box that no deploy updates
#   commands registered globally, an hour behind
#
# PRINTS NO SECRETS. Tokens, passwords and webhook URLs are reported as SET or
# unset and never echoed, so the output is safe to paste into a chat.
set -uo pipefail
cd "$(dirname "$0")/.."

line() { printf '\n=== %s ===\n' "$1"; }

line "repo"
git log --oneline -1 2>/dev/null || echo "not a git checkout"
echo "branch:    $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"
echo "dirty:     $(test -n "$(git status --porcelain 2>/dev/null)" && echo yes || echo no)"

line "published binary"
BIN="dotnet-out/PavlovBot"
if [ -f "$BIN" ]; then
  echo "built:     $(date -r "$BIN" '+%Y-%m-%d %H:%M:%S %Z')"
  echo "commit:    $(git log -1 --format=%cd --date=format:'%Y-%m-%d %H:%M:%S %Z' 2>/dev/null)"
  # The one that catches "deployed but never restarted".
  if [ "$BIN" -ot .git/HEAD ]; then
    echo "  !! THE BINARY IS OLDER THAN THE CHECKOUT. The build did not run, or it failed."
  fi
else
  echo "$BIN is missing - nothing has been published on this box."
fi

line "processes"
if command -v pm2 >/dev/null; then
  pm2 list 2>/dev/null | sed -n '1,20p'
  echo
  echo "A status of errored or stopped is the answer: a bot that is not running"
  echo "registers no commands, so Discord keeps serving the list from last time."
else
  echo "pm2 is not installed on this host."
fi

line "what each bot loaded at startup"
if command -v pm2 >/dev/null; then
  for app in $(pm2 jlist 2>/dev/null | grep -o '"name":"[^"]*"' | cut -d'"' -f4 | sort -u); do
    echo "--- $app"
    pm2 logs "$app" --nostream --lines 400 2>/dev/null \
      | grep -E "factions:|rank\(s\), sub-classes:|Registered .* command|Whitelist bot|FACTIONS_PATH|FACTION_SET|roster files:" \
      | tail -15
    echo
  done
else
  echo "(no pm2 - check the bot's own log output for the 'factions:' line)"
fi

line "configuration that decides all of the above"
for env in .env /root/pavlov-bot-fallout/.env; do
  [ -f "$env" ] || continue
  echo "--- $env"
  for key in FACTION_SET FACTIONS_PATH GUILD_ID USER_APP FACTION_ROLES_PATH COMMANDS_DISABLED; do
    value=$(grep -E "^[[:space:]]*$key=" "$env" | tail -1 | cut -d= -f2- | xargs || true)
    printf '  %-20s %s\n' "$key" "${value:-<unset>}"
  done
  # NAMED, NEVER ECHOED. These decide which bot is which, and none of them can
  # appear in output somebody is about to paste into a chat.
  for key in DISCORD_TOKEN FACTION_BOT_TOKEN; do
    grep -qE "^[[:space:]]*$key=." "$env" && state=SET || state="<unset>"
    printf '  %-20s %s\n' "$key" "$state"
  done
  echo
done

line "roster files on disk"
ROSTERS=$(grep -hE "^[[:space:]]*FACTION_ROLES_PATH=" .env /root/pavlov-bot-fallout/.env 2>/dev/null \
  | tail -1 | cut -d= -f2- | xargs || true)
if [ -n "${ROSTERS:-}" ] && [ -d "$ROSTERS" ]; then
  echo "$ROSTERS"
  ls -la "$ROSTERS" | grep -iE "enclave|ncr|legion|bos" | head -20
else
  echo "FACTION_ROLES_PATH is unset or not a directory - the bot writes no rosters at all."
fi

line "what to do with this"
cat <<'EOF'
Paste the whole output. The three things it settles:

  the binary is older than the checkout   -> the build or the restart did not happen
  a process is errored or stopped         -> read its log; it is failing to start
  "sub-classes: ... " does not list yours -> the running bot loaded different ladders,
                                             and the "factions:" line above says from where
EOF
