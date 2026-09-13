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

line "which binary each process is running, and from where"
# TWO PATHS, TWO JOBS, and confusing them is invisible: the repo holds the build
# and this config, the bot's own directory holds its .env, its data and its logs.
# A process whose exec path is NOT this checkout's dotnet-out is running a binary
# no deploy here ever replaces - the one failure mode that survives every other
# check in this script.
if command -v pm2 >/dev/null; then
  pm2 jlist 2>/dev/null | python3 -c '
import json, os, sys, datetime
try:
    apps = json.load(sys.stdin)
except Exception:
    print("  (could not read pm2 jlist)"); raise SystemExit
for a in apps:
    env = a.get("pm2_env", {})
    exe = env.get("pm_exec_path", "?")
    cwd = env.get("pm_cwd", "?")
    when = "missing"
    if os.path.exists(exe):
        when = datetime.datetime.fromtimestamp(os.path.getmtime(exe)).strftime("%Y-%m-%d %H:%M:%S")
    print(f"  {a.get(\"name\",\"?\")}")
    print(f"    runs:   {exe}   (built {when})")
    print(f"    cwd:    {cwd}   <- its .env and data")
' || echo "  (python3 not available to read pm2 jlist)"
else
  echo "pm2 is not installed on this host."
fi

line "what each bot loaded at startup"
if command -v pm2 >/dev/null; then
  for app in $(pm2 jlist 2>/dev/null | grep -o '"name":"[^"]*"' | cut -d'"' -f4 | sort -u); do
    echo "--- $app"
    pm2 logs "$app" --nostream --lines 400 2>/dev/null \
      | grep -E "factions:|rank\(s\), sub-classes:|Registered .* command|Whitelist bot|FACTIONS_PATH|FACTION_SET|roster files:|Pavlov log|Tailing .* Pavlov log|CANNOT READ|Kill stats: recorded|staff roles" \
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

line "the kill log, and whether anything has happened since the bot started"
# THE BENIGN EXPLANATION, CHECKED FIRST. The tailer positions at the END of the file on its
# first pass - deliberately, so a restart does not replay thousands of old joins - so kills
# written BEFORE the process started are not counted and never will be. "Nothing recorded"
# and "nobody has died since the restart" are the same picture, and only this tells them
# apart.
for log in /home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log \
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

if command -v pm2 >/dev/null; then
  echo
  echo "  Compare that against when each bot started:"
  pm2 list 2>/dev/null | grep -E "pavlov|uptime" | sed 's/^/    /' | head -6
  echo "    A kill written BEFORE the uptime above was never seen: the tailer starts at the"
  echo "    end of the file, on purpose. Play a round, then look again."
fi

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
  "staff roles: mod unset, ..."           -> no role grants that tier, whatever /setroles
                                             showed; only owners (matched by user id) pass
EOF
