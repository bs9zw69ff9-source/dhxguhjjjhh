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
# WHY IT EXISTS. Every one of those lives somewhere different - git, systemd, a
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

# The bot is the systemd unit pavlov-bot (the pm2 fallout bot was dropped on
# 2026-11-17). BOT_UNIT overrides the name.
BOT_UNIT="${BOT_UNIT:-pavlov-bot}"
have_unit() { command -v systemctl >/dev/null 2>&1 && systemctl cat "$BOT_UNIT" >/dev/null 2>&1; }
BOT_DIR=""
if have_unit; then
  BOT_DIR="$(systemctl show -p WorkingDirectory --value "$BOT_UNIT" 2>/dev/null || true)"
fi

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

line "the bot process"
if have_unit; then
  systemctl status --no-pager -n 0 "$BOT_UNIT" 2>/dev/null | sed -n '1,6p'
  echo
  echo "failed or inactive is the answer: a bot that is not running registers no"
  echo "commands, so Discord keeps serving the list from last time."
else
  echo "There is no systemd unit $BOT_UNIT on this host."
fi

line "which binary it is running, and from where"
# TWO PATHS, TWO JOBS, and confusing them is invisible: the repo holds the build,
# the bot's working directory holds its .env, its data and its logs. A unit whose
# ExecStart is NOT this checkout's dotnet-out is running a binary no deploy here
# ever replaces - the one failure mode that survives every other check here.
if have_unit; then
  exec_start="$(systemctl show -p ExecStart --value "$BOT_UNIT" 2>/dev/null || true)"
  exe="$(printf '%s' "$exec_start" | sed -n -E 's/.*path=([^ ;]+).*/\1/p')"
  when="missing"
  [ -n "$exe" ] && [ -f "$exe" ] && when="$(date -r "$exe" '+%Y-%m-%d %H:%M:%S')"
  echo "  runs:   ${exe:-?}   (built $when)"
  echo "  cwd:    ${BOT_DIR:-?}   <- its .env and data"
  case "$exe" in
    "$(pwd)/$BIN") ;;
    *) echo "  !! NOT THIS CHECKOUT'S BUILD ($(pwd)/$BIN) - a deploy here does not reach it." ;;
  esac
fi

line "what the bot loaded at startup"
if have_unit && command -v journalctl >/dev/null 2>&1; then
  journalctl -u "$BOT_UNIT" -n 400 --no-pager 2>/dev/null \
    | grep -E "factions:|rank\(s\), sub-classes:|Registered .* command|Whitelist bot|FACTIONS_PATH|FACTION_SET|roster files:|Pavlov log|Tailing .* Pavlov log|CANNOT READ|Kill stats: recorded|staff roles" \
    | tail -15
else
  echo "(no journal for $BOT_UNIT - check the bot's own log output for the 'factions:' line)"
fi

line "configuration that decides all of the above"
ENV_FILES=".env"
[ -n "$BOT_DIR" ] && [ "$BOT_DIR" != "$(pwd)" ] && ENV_FILES="$ENV_FILES $BOT_DIR/.env"
for env in $ENV_FILES; do
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

line "the kill log, and whether anything has happened since the bot started"
# THE BENIGN EXPLANATION, CHECKED FIRST. The tailer positions at the END of the file on its
# first pass - deliberately, so a restart does not replay thousands of old joins - so kills
# written BEFORE the process started are not counted and never will be. "Nothing recorded"
# and "nobody has died since the restart" are the same picture, and only this tells them
# apart.
for log in /home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log \
           /home/steam/pavlovserver1/Pavlov/Saved/Logs/Pavlov.log \
           /home/steam/pavlovserver2/Pavlov/Saved/Logs/Pavlov.log; do
  [ -f "$log" ] || continue
  echo "--- $log"
  echo "    last written:  $(date -r "$log" '+%Y-%m-%d %H:%M:%S')"
  echo "    KillData blocks in the file: $(grep -c '"KillData"' "$log" 2>/dev/null || echo '?')"

  # The last one, with the line the game wrote just before it - which carries the timestamp.
  last=$(grep -n '"KillData"' "$log" 2>/dev/null | tail -1 | cut -d: -f1)
  if [ -n "${last:-}" ]; then
    echo "    last block at line $last:"
    sed -n "$((last)),$((last + 5))p" "$log" | sed 's/^/      /'
  fi
done

if have_unit; then
  echo
  echo "  Compare that against when the bot started:"
  echo "    $(systemctl show -p ActiveEnterTimestamp --value "$BOT_UNIT" 2>/dev/null)"
  echo "    A kill written BEFORE that time was never seen: the tailer starts at the"
  echo "    end of the file, on purpose. Play a round, then look again."
fi

line "roster files on disk"
ROSTERS=$(grep -hE "^[[:space:]]*FACTION_ROLES_PATH=" $ENV_FILES 2>/dev/null \
  | tail -1 | cut -d= -f2- | xargs || true)
if [ -n "${ROSTERS:-}" ] && [ -d "$ROSTERS" ]; then
  echo "$ROSTERS"
  ls -la "$ROSTERS" | grep -iE "police" | head -20
else
  echo "FACTION_ROLES_PATH is unset or not a directory - the bot writes no rosters at all."
fi

line "what to do with this"
cat <<'EOF'
Paste the whole output. The three things it settles:

  the binary is older than the checkout   -> the build or the restart did not happen
  the unit is failed or inactive          -> journalctl -u pavlov-bot -n 100; it is failing to start
  "sub-classes: ... " does not list yours -> the running bot loaded different ladders,
                                             and the "factions:" line above says from where
  "staff roles: mod unset, ..."           -> no role grants that tier, whatever /setroles
                                             showed; only owners (matched by user id) pass
EOF
