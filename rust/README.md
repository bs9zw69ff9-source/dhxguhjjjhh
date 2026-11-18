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
| `crates/pavlov-host` | Everything on the box but Discord: processes, systemd, the RCON registry, observability, the services |
| `crates/pavlov-discord` | The Discord side: access, the commands, boards and feeds, over a Discord-agnostic caller; the gateway adapter is the only part that knows Discord's types |
| `crates/pavlov-gateway` | The serenity adapter, the only crate that knows Discord's wire types: converts interactions into `pavlov-discord`'s plain values and implements its seams (responders, channel posts, renames, invites, DMs, staff-guild members, command registration) over Discord's REST API |
| `crates/pavlov-storage` | Where the data lives: SQLite and file backends, atomic writes that keep the game's ownership, the datasets |

The remaining crate (`pavlov-bot`) are added as their milestones land; see
MIGRATION.md for the crate map.

## Test parity

Every C# test is ported under the same name in snake_case, in a Rust file named after the C#
test file. `python3 tools/parity.py` counts what is done; `--list` names what is not.
