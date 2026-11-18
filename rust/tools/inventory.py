#!/usr/bin/env python3
"""Regenerate the generated sections of rust/MIGRATION.md from the C# source.

The C# implementation is the reference, so the inventory is READ from it rather than
typed by hand: a hand-written list of 58 commands or 30 services drifts the first time
somebody adds one. Run from anywhere:  python3 rust/tools/inventory.py

Only the text between the BEGIN/END GENERATED markers is replaced. Everything else in
MIGRATION.md (decisions, risks, notes) is hand-maintained and left alone.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DOTNET = ROOT / "dotnet"
SRC = DOTNET / "src"
TESTS = DOTNET / "tests" / "PavlovBot.Tests"
MIGRATION = ROOT / "rust" / "MIGRATION.md"

sys.path.insert(0, str(Path(__file__).resolve().parent))
from parity import csharp_tests, dropped_reason, load_status, rust_tests, snake  # noqa: E402

BEGIN = "<!-- BEGIN GENERATED -->"
END = "<!-- END GENERATED -->"


def cs_files(base: Path) -> list[Path]:
    return sorted(p for p in base.rglob("*.cs") if "/obj/" not in p.as_posix() and "/bin/" not in p.as_posix())


def lines(p: Path) -> int:
    return sum(1 for _ in p.open(encoding="utf-8", errors="replace"))


def target_crate(rel: str) -> str:
    """Where a C# source file lands in the Rust workspace. See MIGRATION.md 'Crate map'."""
    parts = rel.split("/")
    project, rest = parts[0], parts[1:]
    if project == "PavlovBot.Core":
        return "pavlov-core"
    if project == "PavlovBot.Rcon":
        return "pavlov-rcon"
    top = rest[0] if len(rest) > 1 else ""
    if rel.endswith("RconRegistry.cs"):
        return "pavlov-host"   # beside the metrics and the Pavlov.log confirmations it uses
    if rel.endswith("Program.cs"):
        return "pavlov-bot"
    return {
        "Configuration": "pavlov-config",
        "Storage": "pavlov-storage",
        "Servers": "pavlov-host",
        "Discord": "pavlov-discord",
        "Rcon": "pavlov-rcon",
        "Plugins": "DROPPED (plugins)",
    }.get(top, "pavlov-host") if not rel.endswith(("SqliteEventStore.cs", "LedgerFileStore.cs")) else "pavlov-storage"


def table(rows: list[list[str]], header: list[str]) -> str:
    out = ["| " + " | ".join(header) + " |", "|" + "---|" * len(header)]
    out += ["| " + " | ".join(r) + " |" for r in rows]
    return "\n".join(out)


# Commands whose name is not a `Name => "..."` literal in the C#: set through a constructor
# (RankChangeCommand) or read from the skin (MoneyCommand). Listed so the count is the real one.
CONSTRUCTOR_NAMED = {
    "promotion": "RankChangeCommand.cs",
    "demotion": "RankChangeCommand.cs",
    "givemoney": "EconomyCommands.cs",
    "adjustmoney": "EconomyCommands.cs",
}


def rust_command_sources() -> str:
    """Every Rust source a command name can be declared in."""
    text = ""
    for root in (ROOT / "rust" / "crates" / "pavlov-discord" / "src",
                 ROOT / "rust" / "crates" / "pavlov-core" / "src"):
        for f in sorted(root.rglob("*.rs")):
            text += f.read_text(encoding="utf-8")
    return text


def commands() -> list[list[str]]:
    rust = rust_command_sources()

    def status(name: str) -> str:
        if name == "plugins":
            return "DROPPED (plugins)"
        return "ported" if f'"{name}"' in rust else "pending"

    rows = []
    for f in cs_files(SRC / "PavlovBot.Host" / "Discord" / "Commands"):
        text = f.read_text(encoding="utf-8")
        for m in re.finditer(r'string Name => "([a-z0-9-]+)"', text):
            # The command's own description is the first WithDescription after its Build().
            build = text.find("Build()", m.end())
            window = text[build: build + 2500] if build >= 0 else ""
            d = re.search(r'WithDescription\("((?:[^"\\]|\\.)*)"', window)
            desc = d.group(1) if d else ""
            rows.append([f"`/{m.group(1)}`", desc.replace("|", "\\|"), f.name, status(m.group(1))])
    for name, file in CONSTRUCTOR_NAMED.items():
        rows.append([f"`/{name}`", "(named by its constructor)", file, status(name)])
    return sorted(rows)


def env_vars() -> list[list[str]]:
    example = (ROOT / ".env.example").read_text(encoding="utf-8")
    documented = set(re.findall(r"^#?\s*([A-Z][A-Z0-9_]{2,})=", example, re.M))
    used: dict[str, set[str]] = {}
    for f in cs_files(SRC / "PavlovBot.Host"):
        for name in re.findall(r'"([A-Z][A-Z0-9_]{2,}(?:_\{?[a-z]*\}?)?)"', f.read_text(encoding="utf-8")):
            if "_" in name or name in {"THEME", "USER_APP"}:
                used.setdefault(name, set()).add(f.relative_to(SRC).as_posix())
    names = sorted((documented | set(used)) - {"UTF_8"})
    config_text = ""
    for crate in ("pavlov-config", "pavlov-bot", "pavlov-host", "pavlov-storage"):
        for f in sorted((ROOT / "rust" / "crates" / crate / "src").rglob("*.rs")):
            config_text += f.read_text(encoding="utf-8")
    rows = []
    for n in names:
        where = ", ".join(sorted(used.get(n, set())))[:120] or "(.env.example only)"
        # Numbered settings are read as RCON_HOST_{n}: check the stem.
        stem = re.sub(r"_?(\d+|\{[a-z]*\})$", "", n)
        if n not in used:
            state = "unused (the C# never read it either)"
        elif f'"{n}"' in config_text or (stem != n and f'"{stem}_' in config_text):
            state = "read"
        else:
            state = "NOT READ"
        rows.append([f"`{n}`", "yes" if n in documented else "no", where, state])
    return rows


def datasets() -> list[list[str]]:
    text = (SRC / "PavlovBot.Host" / "Storage" / "Datasets.cs").read_text(encoding="utf-8")
    rust = (ROOT / "rust" / "crates" / "pavlov-storage" / "src" / "datasets.rs").read_text(encoding="utf-8")
    return [[f"`{c}`", f"`{v}`", "ported" if f'"{v}"' in rust else "pending"]
            for c, v in re.findall(r'public const string (\w+) = "([^"]+)"', text)]


def services() -> list[list[str]]:
    text = (SRC / "PavlovBot.Host" / "Services" / "BackgroundServiceHost.cs").read_text(encoding="utf-8")
    rust = (ROOT / "rust" / "crates" / "pavlov-bot" / "src" / "services.rs").read_text(encoding="utf-8")
    return [[f"`{n}`", "ported" if f'"{n}"' in rust else "pending"]
            for n in re.findall(r'Name = "([a-z0-9-]+)"', text)]


def source_map() -> list[list[str]]:
    rows = []
    for f in cs_files(SRC):
        rel = f.relative_to(SRC).as_posix()
        rows.append([f"`{rel}`", str(lines(f)), target_crate(rel)])
    return rows


def test_map(status: dict) -> list[list[str]]:
    """Each C# test file with how many of its tests exist in Rust, by parity.py's rule."""
    rs = rust_tests()
    rows = []
    for cs_file, methods in csharp_tests().items():
        have = rs.get(snake(cs_file), set())
        dropped = sum(1 for m in methods if dropped_reason(status["dropped"], cs_file, m))
        ported = sum(1 for m in methods if snake(m) in have and not dropped_reason(status["dropped"], cs_file, m))
        state = status["files"].get(cs_file, "")
        note = "ported" if state == "ported" else f"{ported}/{len(methods) - dropped}"
        if dropped:
            note += f" ({dropped} dropped)"
        rows.append([f"`{cs_file}.cs`", str(len(methods)), note])
    return rows


def main() -> int:
    status = load_status()
    cmd, env, ds, svc = commands(), env_vars(), datasets(), services()
    src, tst = source_map(), test_map(status)
    body = "\n\n".join([
        "_Generated by `rust/tools/inventory.py` from the C# source. Do not edit by hand; "
        "edit `rust/tools/status.toml` and re-run._",
        f"### Slash commands ({len(cmd)})\n\n" + table(cmd, ["Command", "Description (C#)", "C# file", "Rust"]),
        f"### Background services ({len(svc)})\n\n" + table(svc, ["Service", "Rust"]),
        f"### Datasets ({len(ds)})\n\n" + table(ds, ["C# constant", "Stored name", "Rust"]),
        f"### Environment variables ({len(env)})\n\n"
        "\"Read in\" is a scan for the literal name. `(.env.example only)` means no literal read was "
        "found: either the name is built at run time (`RCON_HOST_{n}`) or the C# never reads it "
        "(`APPEAL_LINK`). Each one is checked by hand when `pavlov-config` is ported.\n\n" + table(env, ["Variable", "In .env.example", "Read in", "Rust"]),
        f"### C# source files ({len(src)}, {sum(int(r[1]) for r in src):,} lines)\n\n" + table(src, ["File", "Lines", "Rust crate"]),
        f"### C# test files ({len(tst)}, {sum(int(r[1]) for r in tst):,} test methods)\n\n" + table(tst, ["File", "Tests", "Rust"]),
    ])
    current = MIGRATION.read_text(encoding="utf-8")
    if BEGIN not in current or END not in current:
        print(f"{MIGRATION} has no generated markers", file=sys.stderr)
        return 1
    head, rest = current.split(BEGIN, 1)
    _, tail = rest.split(END, 1)
    MIGRATION.write_text(f"{head}{BEGIN}\n\n{body}\n\n{END}{tail}", encoding="utf-8")
    print(f"commands={len(cmd)} services={len(svc)} datasets={len(ds)} env={len(env)} "
          f"sources={len(src)} test_files={len(tst)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
