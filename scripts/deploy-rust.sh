#!/usr/bin/env bash
# Build, verify and (optionally) restart the RUST bot. The C# bot and scripts/deploy.sh are
# untouched: until the cutover in rust/DEPLOY.md this only ever replaces rust-out/pavlov-bot.
#
#   bash scripts/deploy-rust.sh                        # build + selftest, change nothing running
#   bash scripts/deploy-rust.sh --start                # ... then restart the unit (default
#                                                      #     pavlov-bot-rust, the staging unit)
#   bash scripts/deploy-rust.sh --start --unit pavlov-bot   # after the cutover
#   bash scripts/deploy-rust.sh --install-rust         # ... fetching a Rust toolchain if absent
#
# THE SELFTEST GATE IS THE POINT. The new binary is built to rust-out/pavlov-bot.new and runs
# `--selftest` from this directory, against THIS .env and data directory, before it replaces
# anything. The selftest builds the whole object graph and registers every background service
# without contacting Discord or starting work; any failure stops here with the running bot
# untouched.
#
# THE BUILD STAMP. PAVLOV_BUILD_STAMP is set to the commit at compile time, the bot logs
# "build=<commit>" at startup, and after a restart this script waits for that line. A stamp
# that does not match means the unit is running something else, and that is reported as a
# failure rather than as a deploy.
set -euo pipefail
cd "$(dirname "$0")/.."

START=false
INSTALL_RUST=false
UNIT="pavlov-bot-rust"
while [ $# -gt 0 ]; do
  case "$1" in
    --start)        START=true ;;
    --install-rust) INSTALL_RUST=true ;;
    --unit)         shift; UNIT="${1:?--unit needs a unit name}" ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

OUT="./rust-out"
BIN="$OUT/pavlov-bot"
NEW="$BIN.new"
MIN_RUST_MINOR=85   # rust/Cargo.toml rust-version = "1.85"

rust_is_new_enough() {
  local minor
  minor="$(rustc --version 2>/dev/null | awk '{print $2}' | cut -d. -f2)"
  [ -n "$minor" ] && [ "$minor" -ge "$MIN_RUST_MINOR" ] 2>/dev/null
}

if ! command -v cargo >/dev/null 2>&1 && [ -x "$HOME/.cargo/bin/cargo" ]; then
  export PATH="$HOME/.cargo/bin:$PATH"
  echo "==> Rust found at \$HOME/.cargo/bin (it was not on PATH; added for this run)"
fi
if ! command -v cargo >/dev/null 2>&1 || ! rust_is_new_enough; then
  if [ "$INSTALL_RUST" = true ]; then
    echo "==> Installing a minimal stable Rust toolchain into \$HOME/.cargo"
    curl -fsSL https://sh.rustup.rs | sh -s -- -y --profile minimal --default-toolchain stable
    export PATH="$HOME/.cargo/bin:$PATH"
  else
    cat >&2 <<EOF
No Rust 1.$MIN_RUST_MINOR+ toolchain on this host. Nothing was built and nothing was changed.

  1. Let this script install it (into \$HOME/.cargo, no root needed; ~1.5 GB with the
     build cache):   bash scripts/deploy-rust.sh --install-rust
  2. Or build elsewhere and copy the single binary to $BIN (it links only glibc).
EOF
    exit 1
  fi
fi

REVISION="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
echo "==> Building the Rust bot (release, build $REVISION)"
mkdir -p "$OUT"
# Incremental caches are pointless for a release build and fill a small disk.
(cd rust && PAVLOV_BUILD_STAMP="$REVISION" CARGO_INCREMENTAL=0 cargo build --release --locked -p pavlov-bot)
cp rust/target/release/pavlov-bot "$NEW"
chmod 755 "$NEW"

echo "==> Verifying configuration (selftest)"
SELFTEST_LOG="$(mktemp)"
trap 'rm -f "$SELFTEST_LOG"' EXIT
SELFTEST_SECONDS=60
SELFTEST_RC=0
timeout --kill-after=10 "$SELFTEST_SECONDS" "$NEW" --selftest 2>&1 | tee "$SELFTEST_LOG" || SELFTEST_RC=$?
if [ "$SELFTEST_RC" -ne 0 ] || ! grep -q "selftest: graph built" "$SELFTEST_LOG"; then
  if [ "$SELFTEST_RC" = 124 ] || [ "$SELFTEST_RC" = 137 ]; then
    echo >&2
    echo "The selftest did not finish within ${SELFTEST_SECONDS}s - the bot's startup hangs." >&2
    echo "That is a bug in the bot, not in your configuration: report it with the log above." >&2
  elif [ "$SELFTEST_RC" = 78 ]; then
    echo >&2
    echo "The configuration is not usable - the lines above say what to change in .env." >&2
  fi
  rm -f "$NEW"
  echo >&2
  echo "SELFTEST FAILED - nothing was replaced, nothing was restarted." >&2
  exit 1
fi

# Atomic on one filesystem: a running process keeps its old inode, the next start gets this.
mv -f "$NEW" "$BIN"
echo "==> $BIN is build $REVISION"

if [ "$START" != true ]; then
  echo
  echo "Built and verified. Nothing restarted."
  echo "  restart it with:  bash scripts/deploy-rust.sh --start --unit $UNIT"
  exit 0
fi

if ! command -v systemctl >/dev/null 2>&1 || ! systemctl cat "$UNIT" >/dev/null 2>&1; then
  echo "There is no systemd unit called $UNIT. Install the example first:" >&2
  echo "  see rust/DEPLOY.md, \"The staging unit\"" >&2
  exit 1
fi
# Restarting a unit whose ExecStart is some other binary would change nothing and be
# reported as a deploy, so the unit is checked first.
EXEC_START="$(systemctl show -p ExecStart --value "$UNIT" 2>/dev/null || true)"
case "$EXEC_START" in
  *"$(pwd)/rust-out/pavlov-bot"*) ;;
  *)
    echo "$UNIT does not run $(pwd)/rust-out/pavlov-bot, so restarting it would not deploy this build." >&2
    echo "  Its ExecStart: ${EXEC_START:-(unreadable)}" >&2
    echo "  See it with:   systemctl cat $UNIT" >&2
    exit 1
    ;;
esac

SINCE="$(date '+%Y-%m-%d %H:%M:%S')"
echo "==> Restarting $UNIT"
systemctl restart "$UNIT"

RUNNING=""
for _ in $(seq 1 20); do
  sleep 1
  RUNNING="$(journalctl -u "$UNIT" --since "$SINCE" --no-pager -o cat 2>/dev/null \
    | grep -oE 'build=[0-9a-f]{7,}|build=local-[0-9.]+' | tail -1 | cut -d= -f2)"
  [ -n "$RUNNING" ] && break
done
echo
if [ "$RUNNING" = "$REVISION" ]; then
  echo "==> $UNIT is running build $RUNNING - matches what was just deployed."
elif [ -n "$RUNNING" ]; then
  echo "MISMATCH: $UNIT reports build $RUNNING, but $REVISION was deployed." >&2
  exit 1
else
  echo "Could not read a build stamp from the journal within 20s." >&2
  echo "Check it came up at all:  journalctl -u $UNIT -n 50" >&2
  exit 1
fi
echo
echo "Watch it:  journalctl -fu $UNIT"
echo "Roll back: rust/DEPLOY.md, \"Rollback\""
