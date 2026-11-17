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
