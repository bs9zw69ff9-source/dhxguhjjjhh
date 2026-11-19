#!/usr/bin/env bash
# The merge gate. Nothing merges unless it passes in full, and CI runs the same script.
# Run from anywhere:  bash rust/tools/gate.sh
#
# Warnings are errors (clippy -D warnings). The test-parity step against the C# suite ended
# when the C# bot was removed (2026-11-19): every C# test had been ported or dropped with a
# reason, and the record of that is rust/MIGRATION.md and git history.
set -euo pipefail
cd "$(dirname "$0")/.."

step() { printf '\n==> %s\n' "$1"; }

step "no source file is hidden by .gitignore"
# The gate builds the working tree, so a file git ignores passes here and is missing on main.
# A bare `logs/` rule once hid two whole modules that way. Outside a git checkout (an export)
# there is nothing to check.
if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  hidden=$(git ls-files --others --ignored --exclude-standard -- crates tools fixtures Cargo.toml Cargo.lock \
    | grep -vE '(^|/)(__pycache__|target)/' || true)
  if [ -n "$hidden" ]; then
    printf 'ignored by git, so it would never reach main:\n%s\n' "$hidden" >&2
    exit 1
  fi
fi

step "cargo fmt --check"
cargo fmt --all -- --check

step "cargo clippy (warnings are errors)"
cargo clippy --workspace --all-targets --all-features -- -D warnings

step "cargo test"
cargo test --workspace --all-features

step "cargo build --release"
cargo build --release --workspace

printf '\nGate passed.\n'
