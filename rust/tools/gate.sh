#!/usr/bin/env bash
# The Rust port's merge gate. Every milestone runs this, and nothing merges unless it
# passes in full. Run from anywhere:  bash rust/tools/gate.sh
#
# Warnings are errors (clippy -D warnings), the same bar the C# holds with
# TreatWarningsAsErrors. parity.py fails if a C# test file marked "ported" in
# tools/status.toml is missing any of its tests in Rust.
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

step "test parity with the C# suite"
python3 tools/parity.py

printf '\nGate passed.\n'
