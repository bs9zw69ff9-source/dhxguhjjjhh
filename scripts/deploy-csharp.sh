#!/usr/bin/env bash
# Build, verify and start the C# bot under pm2.
#
#   bash scripts/deploy-csharp.sh              # build + selftest only, do not start
#   bash scripts/deploy-csharp.sh --start      # ... and start it (stops the Node bot first)
#   bash scripts/deploy-csharp.sh --start --keep-node   # override the safety check
#
# The default is deliberately "build and check, change nothing". Deploying a bot
# that has never run against a live gateway should take a second, conscious step.
set -euo pipefail
cd "$(dirname "$0")/.."

START=false
KEEP_NODE=false
for arg in "$@"; do
  case "$arg" in
    --start)      START=true ;;
    --keep-node)  KEEP_NODE=true ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

OUT="./dotnet-out"
APP="pavlov-bot-cs"
NODE_APP="pavlov-bot"

# ---- 1. build ----------------------------------------------------------------
# Self-contained: no .NET runtime to install on the game host, and no chance of a
# runtime upgrade underneath the bot changing its behaviour.
#
# NOT trimmed. Discord.Net's Rest and WebSocket assemblies produce IL2104 trim
# warnings, and a reflection failure under trimming does not show up at startup —
# it shows up the first time a particular gateway payload arrives, which is the
# worst possible time to find out.
echo "==> Building (self-contained, ReadyToRun)"
dotnet publish dotnet/src/PavlovBot.Host \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishReadyToRun=true -p:PublishTrimmed=false \
  -o "$OUT"

# ---- 2. verify ---------------------------------------------------------------
# --selftest builds the whole object graph and exits without connecting to
# anything. It proves the configuration parses and every dependency resolves —
# the two things that fail at 2am — before pm2 is involved at all.
echo "==> Verifying configuration"
if ! "$OUT/PavlovBot" --selftest; then
  echo "SELFTEST FAILED — nothing was started, nothing was changed." >&2
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
if pm2 jlist 2>/dev/null | grep -q "\"name\":\"$NODE_APP\".*\"status\":\"online\""; then
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
echo "==> Starting $APP"
pm2 start ecosystem.csharp.config.js --update-env
pm2 save

echo
echo "Started. Watch it come up:"
echo "  pm2 logs $APP"
echo
echo "Roll back at any time — the C# bot writes the same format the Node bot reads:"
echo "  pm2 stop $APP && pm2 start $NODE_APP"
