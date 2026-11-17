#!/usr/bin/env python3
"""Test parity between the C# suite (the reference) and the Rust port.

THE RULE. Every C# test method has a Rust test of the same name in snake_case, in a
Rust file named after the C# file in snake_case:

    dotnet/tests/PavlovBot.Tests/ProvisioningTests.cs :: GameIniIsTheConfigThatActuallyRuns
    rust/**/provisioning_tests.rs                     :: fn game_ini_is_the_config_that_actually_runs

A [Theory] is one Rust test that runs every [InlineData] case. A C# test that is
deliberately not ported is listed under [dropped] in rust/tools/status.toml with the
reason, so "not ported" is always a decision somebody wrote down, never an omission.

GATES
  default   every C# test FILE marked "ported" in status.toml must have all its tests
            present in Rust. Use it on every milestone: a file is either done or not.
  --strict  every C# test must be ported or dropped. The completion gate (M10).

Exit status 0 when the gate passes, 1 otherwise.
"""
from __future__ import annotations

import argparse
import fnmatch
import re
import sys
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CS_TESTS = ROOT / "dotnet" / "tests" / "PavlovBot.Tests"
RUST = ROOT / "rust"
STATUS = RUST / "tools" / "status.toml"

CS_TEST = re.compile(
    r"\[(?:Fact|Theory)[^\]]*\][\s\S]*?(?:public|internal)\s+(?:async\s+)?\w+(?:<[^>]+>)?\s+(\w+)\s*\(")
RUST_TEST = re.compile(r"#\[(?:tokio::)?test[^\]]*\]\s*(?:#\[[^\]]*\]\s*)*(?:pub\s+)?(?:async\s+)?fn\s+(\w+)")


def snake(name: str) -> str:
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name)
    s = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1_\2", s)
    return re.sub(r"_+", "_", s).lower().strip("_")


def load_status() -> dict:
    if not STATUS.exists():
        return {"files": {}, "dropped": {}}
    data = tomllib.loads(STATUS.read_text(encoding="utf-8"))
    return {"files": data.get("files", {}), "dropped": data.get("dropped", {})}


def csharp_tests() -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for f in sorted(CS_TESTS.glob("*.cs")):
        names = CS_TEST.findall(f.read_text(encoding="utf-8"))
        if names:
            out[f.stem] = names
    return out


def rust_tests() -> dict[str, set[str]]:
    out: dict[str, set[str]] = {}
    for f in RUST.rglob("*.rs"):
        if "/target/" in f.as_posix():
            continue
        out.setdefault(f.stem, set()).update(RUST_TEST.findall(f.read_text(encoding="utf-8")))
    return out


def dropped_reason(dropped: dict, cs_file: str, method: str) -> str | None:
    key = f"{cs_file}::{method}"
    for pattern, reason in dropped.items():
        if fnmatch.fnmatchcase(key, pattern):
            return reason
    return None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict", action="store_true", help="every C# test must be ported or dropped")
    ap.add_argument("--list", action="store_true", help="list every missing test")
    args = ap.parse_args()

    status = load_status()
    cs, rs = csharp_tests(), rust_tests()
    total = ported = dropped = 0
    failures: list[str] = []
    missing_all: list[str] = []

    for cs_file, methods in cs.items():
        have = rs.get(snake(cs_file), set())
        marked = status["files"].get(cs_file) == "ported"
        for m in methods:
            total += 1
            if dropped_reason(status["dropped"], cs_file, m):
                dropped += 1
            elif snake(m) in have:
                ported += 1
            else:
                missing_all.append(f"{cs_file}::{m} -> {snake(cs_file)}.rs::{snake(m)}")
                if marked:
                    failures.append(f"{cs_file} is marked ported but {snake(m)} is missing")

    for cs_file in status["files"]:
        if cs_file not in cs:
            failures.append(f"status.toml names {cs_file}, which is not a C# test file")

    left = total - ported - dropped
    print(f"C# tests: {total}  ported: {ported}  dropped: {dropped}  not yet ported: {left}")
    if args.list:
        print("\n".join(missing_all))
    if args.strict and left:
        failures.append(f"{left} C# tests are neither ported nor dropped")
    for f in failures:
        print(f"FAIL: {f}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
