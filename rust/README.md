# PavlovBot, Rust port

A port of the police bot from C#/.NET 9 (`../dotnet`) to Rust. **Complete, and not deployed
yet.** All 1,792 C# tests are ported and pass, the whole bot builds as one binary, and
`pavlov-bot --selftest` passes. A live Discord gateway and live RCON have not been exercised
yet: nothing in the development container could reach either. The owner's staging run in
[DEPLOY.md](DEPLOY.md) is where both are checked for the first time. Until the cutover,
`scripts/deploy.sh` builds and runs the C# bot.

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
| `crates/pavlov-bot` | The binary: the startup checks, the object graph in the C# start order, the background services, the run and `--selftest` |

## Run and deploy

```bash
bash scripts/deploy-rust.sh            # from the repository root: release build + selftest gate
rust-out/pavlov-bot --selftest         # build the whole graph without contacting Discord
rust-out/pavlov-bot                    # run it (reads ./.env, like the C# bot)
```

Staging, the cutover and the rollback are in [DEPLOY.md](DEPLOY.md). Plugins are not
supported, and the fallout theme was removed. The behaviour differences from the C#, each
with its reason, are in MIGRATION.md.

## Test parity

Every C# test is ported under the same name in snake_case, in a Rust file named after the C#
test file. `python3 tools/parity.py` counts what is done; `--list` names what is not.
