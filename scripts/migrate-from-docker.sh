#!/usr/bin/env bash
# MOVE the game servers off Docker, keeping their data, ready for /provisionserver (systemd).
#
#   sudo bash scripts/migrate-from-docker.sh           # shows the plan and changes NOTHING
#   sudo bash scripts/migrate-from-docker.sh --yes     # does it
#   sudo bash scripts/migrate-from-docker.sh --yes pavlovserver pavlovserver1   # only these
#
# For each container the bot made (label pavlovbot.managed=true), in this order:
#   1. backs up its whole Saved folder to /root/docker-migration-<time>/<name>-Saved.tgz
#      (Game.ini and RconSettings.txt are in it)
#   2. moves the player data (ModSave, mods.txt, whitelist.txt, blacklist.txt) to
#      <parent>/.pavlov-preserved/<name>, which is where /provisionserver puts it back
#   3. stops and removes the container
#   4. moves the rest of the install aside to <parent>/.pavlov-docker-old/<name>, so
#      /provisionserver finds an empty slot (it refuses to install over a non-empty folder)
#
# Nothing is deleted. The container held only the game files, which SteamCMD downloads again;
# everything the bot reads or writes was in Saved, and it is backed up and moved, not removed.
# A step that fails stops the whole run before the next container is touched.
set -euo pipefail

YES=0
NAMES=()
for arg in "$@"; do
  case "$arg" in
    --yes) YES=1 ;;
    -*) echo "Unknown option $arg"; exit 1 ;;
    *) NAMES+=("$arg") ;;
  esac
done

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash $0"; exit 1; }
command -v docker >/dev/null 2>&1 || { echo "docker is not installed: nothing to migrate."; exit 0; }

STAMP="$(date -u +%Y%m%d-%H%M%S)"
BACKUP="/root/docker-migration-$STAMP"
CONTAINER_SAVED=/home/steam/pavlovserver/Pavlov/Saved   # where the image keeps Saved
ITEMS=(ModSave mods.txt whitelist.txt blacklist.txt)    # what the bot preserves (PreservedPlayerData)

say() { printf '%s\n' "$*"; }
run() { if [ "$YES" = 1 ]; then "$@"; else printf '  would run: %s\n' "$*"; fi; }

if [ "${#NAMES[@]}" -eq 0 ]; then
  mapfile -t NAMES < <(docker ps -a --filter label=pavlovbot.managed=true --format '{{.Names}}')
fi
[ "${#NAMES[@]}" -gt 0 ] || { say "No containers made by the bot were found: nothing to migrate."; exit 0; }

# Check everything for every container BEFORE changing any of them.
declare -A SAVED INSTALL
for name in "${NAMES[@]}"; do
  [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$ ]] || { say "'$name' is not a plausible container name. Stopping."; exit 1; }
  src="$(docker inspect -f "{{range .Mounts}}{{if eq .Destination \"$CONTAINER_SAVED\"}}{{.Source}}{{end}}{{end}}" "$name" 2>/dev/null || true)"
  [ -n "$src" ] || { say "$name has no mount at $CONTAINER_SAVED: not a Pavlov server container. Stopping."; exit 1; }
  [[ "$src" == /*/Pavlov/Saved ]] || { say "$name: unexpected Saved path '$src'. Stopping."; exit 1; }
  [ -d "$src" ] || { say "$name: $src does not exist. Stopping."; exit 1; }
  SAVED[$name]="$src"
  INSTALL[$name]="${src%/Pavlov/Saved}"
done

if [ "$YES" = 1 ]; then install -d -m 0700 "$BACKUP"; fi
say "Plan (backups go to $BACKUP):"
for name in "${NAMES[@]}"; do
  saved="${SAVED[$name]}"; install="${INSTALL[$name]}"; parent="$(dirname "$install")"
  need_kb="$(du -sk "$saved" | cut -f1)"
  have_kb="$(df -k --output=avail "$(dirname "$BACKUP")" | tail -1 | tr -d ' ')"
  [ "$have_kb" -gt $(( need_kb * 2 + 102400 )) ] || { say "Not enough free space on / to back up $saved (${need_kb} KB). Stopping."; exit 1; }

  say
  say "== $name  ($install)"
  run tar czf "$BACKUP/$name-Saved.tgz" -C "$install" Pavlov/Saved

  preserved="$parent/.pavlov-preserved/$name"
  [ "$YES" = 1 ] && [ -d "$preserved" ] && [ -n "$(ls -A "$preserved" 2>/dev/null)" ] && mv "$preserved" "$preserved-$STAMP"
  run mkdir -p "$preserved"
  for item in "${ITEMS[@]}"; do
    if [ -e "$saved/Config/$item" ]; then run mv "$saved/Config/$item" "$preserved/$item"; fi
  done

  run docker stop -t 30 "$name"
  run docker rm "$name"

  old="$parent/.pavlov-docker-old/$name"
  [ "$YES" = 1 ] && [ -e "$old" ] && old="$old-$STAMP"
  run mkdir -p "$parent/.pavlov-docker-old"
  run mv "$install" "$old"
done

say
if [ "$YES" = 1 ]; then
  say "Done. Backups: $BACKUP"
else
  say "Nothing was changed. Run again with --yes to do the above."
  exit 0
fi
cat <<'NEXT'

Next:
  1. Make room, if the disk is tight (a game install is several GB per server). The Docker
     image and the containers' game files are what filled it. After checking nothing else
     uses Docker here:   docker image ls    then    docker image rm <the pavlov image>
  2. In Discord run /provisionserver once per server, in order. It installs the game with SteamCMD
     and puts the preserved data back.
  3. When each finishes:  chown -R steam:steam /home/steam/pavlovserver*/Pavlov/Saved
  4. PAVLOV_UNITS in .env may still say docker:<name>; the prefix is ignored, so it can stay or be
     cleaned up.
NEXT
