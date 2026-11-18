# PavlovBot, Rust port (in progress)

A port of the police bot from C#/.NET 9 (`../dotnet`) to Rust. **Nothing here is deployed.**
`scripts/deploy.sh` builds and runs the C# bot until the cutover, which the owner runs after
the port reaches parity and passes a live staging check.

The C# code is the reference. Progress, decisions and the full inventory of what has to be
ported (commands, services, datasets, environment variables, source and test files) are in
[MIGRATION.md](MIGRATION.md).

## Build and test

Needs stable Rust 1.85 or newer (`rustup` from https://rustup.rs).

```bash
cd rust
bash tools/gate.sh        # fmt check, clippy -D warnings, tests, release build, test parity
cargo test                # tests only
```

## Layout

| Crate | What it is |
|---|---|
| `crates/pavlov-core` | Pure domain rules: no I/O, no Discord, no sockets |
| `crates/pavlov-config` | The `.env` grammar, the environment over it, and the typed options |
| `crates/pavlov-rcon` | Pavlov's RCON protocol: one persistent, paced, self-healing session per server, and a byte-recording fake server (`testing` feature) |

The remaining crates (`pavlov-rcon`, `pavlov-storage`, `pavlov-host`,
`pavlov-services`, `pavlov-discord`, `pavlov-bot`) are added as their milestones land; see
MIGRATION.md for the crate map.

## Test parity

Every C# test is ported under the same name in snake_case, in a Rust file named after the C#
test file. `python3 tools/parity.py` counts what is done; `--list` names what is not.
