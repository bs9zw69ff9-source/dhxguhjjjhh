# Deploying the Rust bot

The Rust bot replaces the C# bot **in place**: same `.env`, same `bot.db`, same game files,
same `pavlov-bot` systemd unit. Getting there takes three steps, in this order:

1. **Staging.** The Rust bot runs beside the live C# bot. It uses a second Discord application,
   a test guild, one test Pavlov server and a *copy* of the data. Work through the checklist
   below.
2. **Cutover.** Repoint `pavlov-bot`'s `ExecStart` at the Rust binary.
3. **Rollback, if needed.** Repoint it back. The C# bot keeps working until M11 deletes it,
   and M11 only happens after the owner confirms the Rust bot works live. **Once the game
   server runs the ServerRoles and JSON-save mod, rollback is no longer data-safe**: see
   [Rollback](#rollback).

## The ServerRoles and JSON-save server (owner change, 2026-11-18)

The Rust bot, and only the Rust bot, matches the server as it now is:

- No RCON+. Menu access is the tier files in `<install>/Pavlov/Saved/ModSave/ServerRoles/`:
  `owner.txt`, `manager.txt`, `srstaff.txt`, `staff.txt` and `jrstaff.txt`, one in-game name
  per line.
- The GWPD ranks are in the same folder: `sheriff.txt`, `undersheriff.txt`, `captain.txt`,
  `lieutenant.txt`, `sergeant.txt`, `corporal.txt` and `deputyfirstclass.txt`. There are no
  sub-classes and no spawn file.
- Player saves are `<install>/Pavlov/Saved/ModSave/<playername>.json`. The bot changes only
  `money`, `drivers_license`, `firearm_license` and `updated`.
- The mod syncs in-game state to the save live, so the bot edits a save while the player is
  online.

Set both paths in `.env`. They are not derived from the install:

```bash
FACTION_ROLES_PATH=<install>/Pavlov/Saved/ModSave/ServerRoles
MODSAVE_PATH=<install>/Pavlov/Saved/ModSave
```

`MENU_PANEL_CHANNEL` and `MENU_ROLE_*` do nothing now. Remove them, or leave them and accept
one startup warning.

## What was and was not verified before this point

Verified in CI and in the development container:

- every one of the 1,792 C# tests, ported and passing (`python3 rust/tools/parity.py --strict`)
- clippy with warnings as errors
- the release build
- `pavlov-bot --selftest` against a minimal `.env`

RCON is tested against a fake server that records the exact bytes on the wire. Discord is
tested against recorded interaction payloads and the builders' JSON.

**Not verified yet:** a live Discord gateway, or RCON against a real Pavlov server. Nothing in
the container could reach either. The staging checklist below is where both are checked for
the first time. Treat it as a first run, not a formality.

## Build and the selftest gate

```bash
bash scripts/deploy-rust.sh                # build, selftest, swap rust-out/pavlov-bot
bash scripts/deploy-rust.sh --install-rust # first time: installs a Rust toolchain in ~/.cargo
```

What the script does:

1. Builds in release with `PAVLOV_BUILD_STAMP=<commit>`, into `rust-out/pavlov-bot.new`.
2. Runs that binary with `--selftest` from the checkout's directory, against its `.env`.
   The selftest builds the whole object graph and registers every background service. It does
   not contact Discord or start any work.
3. Only if the selftest passes, swaps the binary into `rust-out/pavlov-bot`.
4. With `--start`, restarts the unit. It first checks that the unit's `ExecStart` really is
   this binary, then waits for the journal to report `build=<commit>`. A mismatch is a
   failure, not a deploy.

A failed selftest leaves everything as it was. Exit code 78 means a configuration problem,
and the lines above it say what to change.

The build needs about 1.5 GB of disk for the toolchain and the build cache, and a few minutes
of CPU. Building on another machine and copying the single binary also works; it links only
glibc.

## Staging checklist

Everything here happens in `/root/pavlov-bot-staging`, which is a separate checkout. The live
bot in `/root/pavlov-bot` is not touched at any point.

### 1. A copy of the data

```bash
git clone <this repository> /root/pavlov-bot-staging
mkdir -p /root/pavlov-bot-staging/data
# A consistent copy of a live SQLite database: .backup, not cp (cp can catch a half-written WAL).
sqlite3 /root/pavlov-bot/data/bot.db ".backup /root/pavlov-bot-staging/data/bot.db"
```

### 2. Scan the copy for keys the Rust models would not read

The C# reads stored JSON keys case-insensitively. The Rust models read exactly the camelCase
both bots write. A `PascalCase` key that something wrote by hand would read fine in C# and
silently as empty in Rust.

```bash
python3 /root/pavlov-bot-staging/rust/tools/scan_keys.py /root/pavlov-bot-staging/data/bot.db
```

Exit 0 means nothing was found. Otherwise, each finding prints its dataset, its path and a
sample. A finding under `{*}` (a map key) is usually data, such as a player called `Holosight`,
and not a property. A real property name needs fixing in the copy, and in the live data before
cutover. Report it rather than guessing.

### 3. The staging `.env`

Start from a copy of the live `.env`, then change these lines. Every one of them keeps staging
away from something live.

| Setting | Staging value | Why |
|---|---|---|
| `DISCORD_TOKEN` | a **second** Discord application's token (the application id comes from the token) | the live application's commands and interactions stay with the live bot |
| `GUILD_ID`, `HOME_GUILD_ID` | the test guild | commands register there, and appear immediately |
| `RCON_HOST_n` / `RCON_PORT_n` / `RCON_PASSWORD_n` | the test server only | staging must never kick, ban or message on a live server |
| `PAVLOV_BASES`, `PAVLOV_UNITS`, `PAVLOV_LOGS`, `STATS_LOGS` | the test server's install, unit and logs | ban file, whitelist, rosters, ledgers and restarts all follow these |
| `FACTION_ROLES_PATH`, `MODSAVE_PATH`, `BLACKLIST_PATH` | the test server's files, or unset | the same game files the live bot writes |
| `DATA_DIR` | `/root/pavlov-bot-staging/data` | the copy, never the live `bot.db` |
| every `*_WEBHOOK` | unset, or a test channel's webhook | the live feeds stay single-sourced |
| every `*_CHANNEL` and role id | test guild channels and roles, or unset | boards and logs post into the test guild |
| `METRICS_PORT` | a different port, or unset | the live bot holds the live one |
| `FACTION_BOT_TOKEN`, `FACTION_CLIENT_ID` | unset, or a second test application | as for the main token |
| `FIREWALL_BANS`, `FIREWALL_BLACKLIST` | `0` | ufw rules are machine-wide, so staging must not add or lift any |
| `CRASH_RECOVERY` | `0` unless the test unit is the only one listed | it restarts failed units |

`chmod 600 .env` afterwards. The bot warns at startup if other users can read it.

### 4. Build and start

```bash
cd /root/pavlov-bot-staging
bash scripts/deploy-rust.sh
cp rust/deploy/pavlov-bot-rust.service /etc/systemd/system/ && systemctl daemon-reload
systemctl enable pavlov-bot-rust
bash scripts/deploy-rust.sh --start            # restarts pavlov-bot-rust, checks the stamp
journalctl -fu pavlov-bot-rust
```

In the journal, expect:

- `build build=<commit>`
- the startup summary (servers, features, factions, theme, rosters, feeds)
- `logged in`
- `registered commands in guild`

There should be no `MALFORMED` line.

### 5. Check it, against the test guild and the test server

Check each item, and note any difference from the C# bot.

**Gateway**

- [ ] All 61 commands appear in the picker within a minute. None appears twice.
- [ ] `/help` lists them.
- [ ] `/health` shows the build and every component.

**Replies**

- [ ] A public command answers publicly.
- [ ] `/alts`, `/iplookup` and `/inspect` answer privately.
- [ ] No reply pings anybody.

**RCON**

- [ ] `/players`, `/serverinfo` and `/server lookup` work, and the lookup browser pages.
- [ ] `/kick` and `/announce` reach the test server. The game shows them.

**Bans**

- [ ] `/tempban` a test account: it is kicked, and the test server's ban file lists it.
- [ ] `/checkban` shows the ban.
- [ ] `/unban` clears both the record and the file.
- [ ] `/banlist` lists it while active.

**Moderation**

- [ ] `/warn` three times escalates to a ban.
- [ ] The staff log posts in the test channel.

**Factions**

- [ ] `/whitelist add` and `remove` write the test server's roster file.
- [ ] `/promotion` and `/demotion` move the name between the ServerRoles rank files, and the
  game shows the new rank.
- [ ] `/subclass` and `/subclasses` are not registered (the ladder has no sub-classes).
- [ ] The rank field autocompletes ranks. Player fields autocomplete names.

**Economy**

- [ ] `/givemoney` and `/adjustmoney` write `<playername>.json`. Only `money` and `updated`
  change. Every other field, `look` and `skin` included, is the same as before.
- [ ] **With the player in game**, `/adjustmoney` lands, and the new balance shows in game
  without a rejoin. This is the one thing the tests cannot prove: it depends on the mod
  picking up the live edit. If it does not show, stop and report it. Do not cut over.
- [ ] `/license give` and `/license remove` change `drivers_license` / `firearm_license`, and
  the game honours it.
- [ ] `/wages` shows payroll.

**Police**

- [ ] `/arrest` opens its booking picker and records the arrest.
- [ ] `/warrant`, `/bail` and `/backgroundcheck` work.

**Owner**

- [ ] `/configure` opens the panel, an action that needs a value opens its form, and a
  confirmed action lands.
- [ ] `/botstats` shows memory.
- [ ] `/errors` lists an id after a failure.

**Boards**

- [ ] Each configured board posts once.
- [ ] On the next tick it is **edited**, not reposted.
- [ ] After `systemctl restart pavlov-bot-rust`, it is still edited in place.

**Logs**

- [ ] Join the test server: the join feed and `/stats` show it, and the player-count channel
  renames within five minutes.

**Monitor**

- [ ] `/monitor status` matches the server.
- [ ] `/monitor test` posts to the monitor channel and pings the role.

**Verification** (if `VERIFY_CHANNEL` is set)

- [ ] The panel posts.
- [ ] Verify opens the form, and the request reaches the staff channel.
- [ ] Accept grants the role.

**Menus**

- [ ] `/givemenu` with a tier puts the name on that tier file and takes it off the other four.
  The player has that menu in game.
- [ ] As an admin, `/givemenu` refuses `owner` and `manager`.
- [ ] `/stripmenu` takes the name off every tier file, and the menu is gone in game.
- [ ] A master name is on `owner.txt` after startup.

**Access**

- [ ] A user on `BLACKLIST_IDS` is refused privately: commands, buttons and autocomplete.
- [ ] A `COMMANDS_DISABLED` entry removes the command.

**Shutdown**

- [ ] `systemctl stop pavlov-bot-rust` exits within a few seconds.
- [ ] `journalctl` ends with `stopped`.

### 6. The data still reads in C#

Rollback depends on the C# bot reading whatever the Rust bot wrote. Check it on the staging
copy:

```bash
systemctl stop pavlov-bot-rust
cd /root/pavlov-bot-staging && bash scripts/deploy-csharp.sh   # builds and runs the C# selftest here
```

The selftest must pass against the staging data the Rust bot has been writing.

## Cutover

Only after the checklist passes.

```bash
cd /root/pavlov-bot
git pull && bash scripts/deploy-rust.sh            # build and selftest against the LIVE .env
mkdir -p /etc/systemd/system/pavlov-bot.service.d
cat > /etc/systemd/system/pavlov-bot.service.d/rust.conf <<'EOF'
[Service]
ExecStart=
ExecStart=/root/pavlov-bot/rust-out/pavlov-bot
EOF
systemctl daemon-reload
bash scripts/deploy-rust.sh --start --unit pavlov-bot   # restart + build stamp check
journalctl -fu pavlov-bot
```

If `pavlov-bot`'s existing unit sets `WorkingDirectory` to something other than
`/root/pavlov-bot`, adjust both paths. Check with `systemctl cat pavlov-bot`.

After the cutover, deploy with `bash scripts/deploy-rust.sh --start --unit pavlov-bot`.
`scripts/deploy.sh` still builds the C# bot. It will refuse to restart a unit that runs a
different binary, so it cannot silently swap the Rust bot out. M11 makes it build Rust.

## Rollback

```bash
rm /etc/systemd/system/pavlov-bot.service.d/rust.conf
systemctl daemon-reload && systemctl restart pavlov-bot
journalctl -u pavlov-bot -n 30      # the C# bot's own build line
```

**After the server switch to ServerRoles and JSON saves, the C# bot is not a working
rollback.** It reads `<name>.txt` saves that no longer exist, so money and licence commands
fail. It sends RCON+ menu commands to a server without RCON+, and its police ladder names
rank files that are gone. Roll back only to a server still on the old mod, or accept those
features being down until you roll forward again.

`bot.db` is still shared safely. Both bots read and write it in the same shape, which
`NodeCompatibilityTests` pins. There is one difference, and it is harmless: the Rust bot
writes non-ASCII text as UTF-8 where the C# escaped it. Both readers accept either.

## What is different on purpose

Every intentional behaviour difference from the C# is listed, with its reason, in
MIGRATION.md under "Behaviour differences". The ones an operator is most likely to notice:

- Plugins are not supported. `PLUGIN_*` settings are ignored, with a warning.
- The fallout theme and faction set were removed. `THEME=fallout` and `FACTION_SET=fallout`
  stop the bot with a message saying so.
- A channel rename that hits Discord's limit says "rate limited" after 10 seconds, instead of
  waiting up to ten minutes.
- A board is re-posted only when Discord says its message is gone. A missing channel leaves
  it alone.
