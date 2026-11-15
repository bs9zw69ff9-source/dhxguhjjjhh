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
#   2. stops and removes the container
#   3. moves anything in the install folder that is NOT Pavlov/Saved aside to
#      <parent>/.pavlov-docker-old/<name>
#
# Saved stays exactly where it is. /provisionserver accepts a folder that holds only Pavlov/Saved,
# hands it to the steam user, keeps the Game.ini that is there (adding only ServerKey if you give
# one and the file has none), and installs the game around it with SteamCMD. So the server's
# ModSave, whitelist and blacklist never move.
#
# Nothing is deleted. The container held only the game files, which SteamCMD downloads again.
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

  run docker stop -t 30 "$name"
  run docker rm "$name"

  # Anything beside Pavlov/Saved would make /provisionserver refuse the folder: park it.
  old="$parent/.pavlov-docker-old/$name"
  [ "$YES" = 1 ] && [ -e "$old" ] && old="$old-$STAMP"
  run mkdir -p "$old"
  while IFS= read -r -d '' entry; do
    [ "$(basename "$entry")" = Pavlov ] && continue
    run mv "$entry" "$old/"
  done < <(find "$install" -mindepth 1 -maxdepth 1 -print0 2>/dev/null)
  if [ -d "$install/Pavlov" ]; then
    while IFS= read -r -d '' entry; do
      [ "$(basename "$entry")" = Saved ] && continue
      run mv "$entry" "$old/"
    done < <(find "$install/Pavlov" -mindepth 1 -maxdepth 1 -print0 2>/dev/null)
  fi
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
  1. Make room, if the disk is tight (a game install is several GB per server). After checking
     nothing else uses Docker here:   docker image ls    then    docker image rm <the pavlov image>
  2. In Discord run /provisionserver once per server, in order. It installs the dependencies and
     the game, keeps each server's existing Game.ini and saved data, and starts the systemd unit.
  3. PAVLOV_UNITS in .env may still say docker:<name>; the prefix is ignored, so it can stay or be
     cleaned up.
NEXT
