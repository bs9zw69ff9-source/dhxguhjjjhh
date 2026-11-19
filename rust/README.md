# PavlovBot

The Pavlov VR police bot, in Rust. It is the only implementation: the C#/.NET bot it was
ported from was removed on 2026-11-19. The port's
decisions and every intentional behaviour difference from the C# are in
[MIGRATION.md](MIGRATION.md); the C# itself is in git history before that date.

## Build and test

Needs stable Rust 1.88 or newer (`rustup` from https://rustup.rs).

```bash
cd rust
bash tools/gate.sh        # fmt check, clippy -D warnings, tests, release build
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
| `crates/pavlov-bot` | The binary: the startup checks, the object graph, the background services, the run and `--selftest` |

## Run and deploy

```bash
bash scripts/deploy.sh                 # from the repository root: pull main, build, selftest, restart
bash scripts/deploy-rust.sh            # build + selftest gate only, from the current checkout
rust-out/pavlov-bot --selftest         # build the whole graph without contacting Discord
rust-out/pavlov-bot                    # run it (reads ./.env)
```

The unit, the ServerRoles paths and the checks after a deploy are in [DEPLOY.md](DEPLOY.md).
Plugins are not supported, and the fallout theme was removed.
