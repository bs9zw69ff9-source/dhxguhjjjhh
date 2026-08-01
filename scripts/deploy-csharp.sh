#!/usr/bin/env bash
# Build, verify and start the C# bot under pm2.
#
#   bash scripts/deploy-csharp.sh              # build + selftest only, do not start
#   bash scripts/deploy-csharp.sh --start      # ... and start it (stops the Node bot first)
#   bash scripts/deploy-csharp.sh --start --keep-node   # override the safety check
#   bash scripts/deploy-csharp.sh --install-sdk         # fetch the .NET SDK if absent
#
# The default is deliberately "build and check, change nothing". Deploying a bot
# that has never run against a live gateway should take a second, conscious step.
set -euo pipefail
cd "$(dirname "$0")/.."

START=false
KEEP_NODE=false
INSTALL_SDK=false
for arg in "$@"; do
  case "$arg" in
    --start)        START=true ;;
    --keep-node)    KEEP_NODE=true ;;
    --install-sdk)  INSTALL_SDK=true ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

OUT="./dotnet-out"
APP="pavlov-bot-cs"
NODE_APP="pavlov-bot"
SDK_MAJOR=9        # dotnet/Directory.Build.props targets net9.0

# ---- 0. find the SDK ---------------------------------------------------------
# `dotnet: command not found` when the SDK is sitting right there in $HOME/.dotnet
# is the normal outcome of installing it via dotnet-install.sh, which does not
# touch any shell profile. A non-login `bash scripts/deploy.sh` then gets a PATH
# that never had it. So look for it rather than trusting PATH.

# An SDK, not just a runtime: `dotnet publish` needs the former, and a
# runtime-only install answers `--version` perfectly happily. --list-sdks prints
# nothing on a runtime-only install, which is exactly the distinction we want.
sdk_is_new_enough() {
  local major
  major="$("$1" --list-sdks 2>/dev/null | cut -d. -f1 | sort -rn | head -1)"
  [ -n "$major" ] && [ "$major" -ge "$SDK_MAJOR" ] 2>/dev/null
}

use_sdk_at() {
  export DOTNET_ROOT="$1"
  export PATH="$1:$PATH"
  # Telemetry on a headless deploy is noise, and the first-run banner writes to
  # stdout in the middle of the build output.
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_NOLOGO=1
}

resolve_dotnet() {
  if command -v dotnet >/dev/null 2>&1 && sdk_is_new_enough "$(command -v dotnet)"; then
    return 0
  fi

  local dir
  for dir in "$HOME/.dotnet" /usr/share/dotnet /usr/lib/dotnet /usr/local/share/dotnet /opt/dotnet; do
    if [ -x "$dir/dotnet" ] && sdk_is_new_enough "$dir/dotnet"; then
      use_sdk_at "$dir"
      echo "==> .NET SDK found at $dir (it was not on PATH; added for this run)"
      return 0
    fi
  done
  return 1
}

install_sdk() {
  local root="$HOME/.dotnet" script
  echo "==> Installing .NET SDK $SDK_MAJOR into $root"
  script="$(mktemp)"
  # dot.net/v1/dotnet-install.sh is Microsoft's own installer. It unpacks into a
  # directory of our choosing and needs no root, so it cannot collide with a
  # distro-packaged dotnet or with anything else on the game host.
  if ! curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$script"; then
    rm -f "$script"
    echo "Could not download the installer. Is this host allowed out to dot.net?" >&2
    return 1
  fi
  bash "$script" --channel "$SDK_MAJOR.0" --install-dir "$root" --no-path
  rm -f "$script"
  use_sdk_at "$root"
  sdk_is_new_enough "$root/dotnet"
}

if ! resolve_dotnet; then
  if [ "$INSTALL_SDK" = true ]; then
    install_sdk || exit 1
  else
    cat >&2 <<EOF

No .NET SDK $SDK_MAJOR+ on this host, and none in the usual install locations
(\$HOME/.dotnet, /usr/share/dotnet, /usr/lib/dotnet, /usr/local/share/dotnet,
/opt/dotnet). Nothing was built and nothing was changed — the Node bot is
untouched and still running.

Three ways forward:

  1. Let this script install it (into \$HOME/.dotnet, no root needed). Budget
     ~1 GB: ~600 MB for the SDK, ~400 MB for the NuGet cache it fills on the
     first build.
       bash scripts/deploy.sh --install-sdk --start

  2. Install it yourself, then re-run the deploy normally:
       curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel $SDK_MAJOR.0

  3. Keep the SDK off the game host entirely: build ./dotnet-out elsewhere,
     copy it over, and start it with
       pm2 start ecosystem.csharp.config.js
     The published output is self-contained, so it needs no .NET installed.

EOF
    exit 1
  fi
fi

# ---- 1. build ----------------------------------------------------------------
# Self-contained: no .NET runtime to install on the game host, and no chance of a
# runtime upgrade underneath the bot changing its behaviour.
#
# NOT trimmed. Discord.Net's Rest and WebSocket assemblies produce IL2104 trim
# warnings, and a reflection failure under trimming does not show up at startup —
# it shows up the first time a particular gateway payload arrives, which is the
# worst possible time to find out.
echo "==> Building (self-contained, ReadyToRun)"
# The commit is stamped INTO the binary so the running process can say which build it
# is. That is the only way to tell "the fix does not work" apart from "the fix was
# never actually started".
REVISION="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
dotnet publish dotnet/src/PavlovBot.Host \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishReadyToRun=true -p:PublishTrimmed=false \
  -p:SourceRevisionId="$REVISION" \
  -o "$OUT"

# ---- 2. verify ---------------------------------------------------------------
# --selftest builds the whole object graph and exits without connecting to
# anything. It proves the configuration parses and every dependency resolves —
# the two things that fail at 2am — before pm2 is involved at all.
echo "==> Verifying configuration"
SELFTEST_LOG="$(mktemp)"
trap 'rm -f "$SELFTEST_LOG"' EXIT

# pipefail (set at the top) is what makes this test the bot's exit status rather
# than tee's.
if ! "$OUT/PavlovBot" --selftest 2>&1 | tee "$SELFTEST_LOG"; then
  echo >&2
  echo "SELFTEST FAILED — nothing was started, nothing was changed." >&2

  # One failure mode does not explain itself. The bot keeps timestamps in
  # America/New_York, so it is built with globalization enabled and needs ICU at
  # runtime; a minimal server image often has none, and .NET then dies before
  # Main with "Couldn't find a valid ICU package installed on the system".
  #
  # Keyed off what the run actually printed, NOT off probing the system for the
  # library: `ldconfig` lives in /sbin and is missing from a non-login PATH, so
  # the probe reports "no ICU" on hosts that have it and misdirects every
  # unrelated failure.
  if grep -qiE 'icu|globalization' "$SELFTEST_LOG"; then
    echo >&2
    echo "That is a missing ICU library, not a configuration problem:" >&2
    echo "  apt-get install -y libicu-dev" >&2
  fi
  exit 1
fi

if [ "$START" != true ]; then
  echo
  echo "Built and verified. Nothing started."
  echo "  start it with:  bash scripts/deploy-csharp.sh --start"
  exit 0
fi

# ---- 3. the safety check -----------------------------------------------------
# Two bots on one Discord application answer every command twice and issue every
# ban twice. There is no way to undo that from inside the bot, so this is a hard
# stop rather than a warning.
# Parsed, not pattern-matched. `pm2 jlist` emits every app as ONE line of JSON, so
# `grep "name":"X".*"status":"online"` spans app boundaries: it matched a STOPPED
# pavlov-bot purely because pavlov-bot-cs appeared later in the same line and was
# online. Exit codes: 0 online, 1 not online, 2 could not tell.
node_status() {
  pm2 jlist 2>/dev/null | node -e '
    let raw = "";
    process.stdin.on("data", d => raw += d).on("end", () => {
      let apps;
      try { apps = JSON.parse(raw); } catch { process.exit(2); }
      if (!Array.isArray(apps)) process.exit(2);
      const app = apps.find(a => a && a.name === process.argv[1]);
      process.exit(app && app.pm2_env && app.pm2_env.status === "online" ? 0 : 1);
    });
  ' "$NODE_APP"
}

node_status
case $? in
  1) NODE_IS_UP=false ;;
  0) NODE_IS_UP=true ;;
  # Cannot tell - pm2 missing, or output this script does not understand. Treat that
  # as "it might be running", because the failure being guarded against is double
  # bans, and there is no undo for those.
  *) NODE_IS_UP=true
     echo "WARNING: could not read pm2's app list; assuming '$NODE_APP' may be running." >&2 ;;
esac

if [ "$NODE_IS_UP" = true ]; then
  if [ "$KEEP_NODE" != true ]; then
    echo
    echo "REFUSING TO START: '$NODE_APP' is online." >&2
    echo >&2
    echo "Both bots would answer every slash command, issue every ban twice, and" >&2
    echo "post every feed line twice." >&2
    echo >&2
    echo "  stop it first:   pm2 stop $NODE_APP" >&2
    echo "  or, if this C# bot uses a DIFFERENT Discord token and different" >&2
    echo "  channels, override with:  --keep-node" >&2
    exit 1
  fi
  echo "WARNING: '$NODE_APP' left running at your request (--keep-node)."
  echo "         Only safe if the two use different Discord applications."
fi

# ---- 4. start ----------------------------------------------------------------
mkdir -p logs

# `pm2 start` ON AN APP THAT IS ALREADY RUNNING DOES NOT RESTART IT. pm2 reports
# "Script already launched" and leaves the existing process alone, so a deploy would
# publish a new binary, pass the selftest against that new binary, print "Started",
# and leave the OLD process serving - for hours, with every fix apparently having no
# effect and nothing anywhere saying why.
#
# Both branches name the ECOSYSTEM FILE rather than the app. `pm2 restart <name>`
# restarts the process but does NOT re-read the config, so a change to kill_timeout,
# max_memory_restart or the env block would be deployed and never applied - the same
# bug again, one level down.
if pm2 describe "$APP" >/dev/null 2>&1; then
  echo "==> Restarting $APP (it was already running)"
  pm2 restart ecosystem.csharp.config.js --update-env
else
  echo "==> Starting $APP"
  pm2 start ecosystem.csharp.config.js --update-env
fi
pm2 save

# ---- 5. prove the restart took ------------------------------------------------
# Not a formality. The whole reason this section exists is that a deploy which
# quietly leaves the old process running is indistinguishable from a fix that does
# not work, and that cost real time.
DEPLOYED="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
RUNNING=""
for _ in $(seq 1 15); do
  sleep 1
  RUNNING="$(pm2 logs "$APP" --nostream --lines 400 2>/dev/null \
    | grep -oE 'build [0-9a-f]{7,}' | tail -1 | awk '{print $2}')"
  [ -n "$RUNNING" ] && [ "$RUNNING" = "$DEPLOYED" ] && break
done

echo
if [ "$RUNNING" = "$DEPLOYED" ]; then
  echo "==> Running build $RUNNING - matches what was just deployed."
elif [ -n "$RUNNING" ]; then
  echo "MISMATCH: the running process reports build $RUNNING, but $DEPLOYED was deployed." >&2
  echo "The restart did not take. Force it with:" >&2
  echo "  pm2 delete $APP && pm2 start ecosystem.csharp.config.js && pm2 save" >&2
  exit 1
else
  echo "Could not read a build stamp from the log within 15s." >&2
  echo "Check it came up at all:  pm2 logs $APP --lines 50" >&2
  exit 1
fi

echo
echo "Started. Watch it come up:"
echo "  pm2 logs $APP"
echo
echo "Roll back at any time — the C# bot writes the same format the Node bot reads:"
echo "  pm2 stop $APP && pm2 start $NODE_APP"
