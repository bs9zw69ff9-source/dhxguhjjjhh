# Pavlov RP Moderation Bot

A Discord bot for **Pavlov VR** RP servers, written in Rust. It drives one to three game servers
over RCON and adds bans (written into the player's save) and kicks with a reason and duration
picker, ban-evasion and VPN/proxy detection with optional ufw blocking, the GWPD whitelist ladder, in-game menu tiers,
a dollar economy, police RP (warrants, arrests, bail, licences), boards and feeds, and server
monitoring, all behind slash commands.

It is the GWPD's bot: dollars, warrants and the GWPD ladder. `BOT_NAME` overrides the name
stamped on every embed. The fallout theme was removed, and `THEME=fallout` or
`FACTION_SET=fallout` stops the bot at startup with a message saying so.

The source is in [`rust/`](rust/). Earlier versions of this bot were written in Node.js and
then C#/.NET; both were removed, and git history holds them.

## Requirements

- Linux (Ubuntu or Debian, x86-64). The bot shells out to `systemctl` and `ufw` by design.
- Stable Rust 1.88+ to build. `scripts/deploy.sh --install-rust` installs it into `~/.cargo`.
- One to three Pavlov servers with RCON enabled.
- A Discord application and bot token.

## Setup

**A fresh VPS: see [INSTALL.md](INSTALL.md).** On a box that is already prepared:

```bash
cp .env.example .env              # then fill in the values
bash scripts/deploy.sh            # pull main, build, selftest, restart pavlov-bot
```

The deploy gates on `--selftest`, which builds the entire object graph and exits without
connecting to anything. If the configuration is wrong it prints every problem at once and
changes nothing. Slash commands are registered on startup.

Rolling back means deploying an earlier commit: `bash scripts/deploy.sh <older-sha>`. The unit,
the ServerRoles paths and the checks after a deploy are in [rust/DEPLOY.md](rust/DEPLOY.md).

## Configuration

Everything is in `.env`; [`.env.example`](.env.example) documents every setting.

- **Required:** `DISCORD_TOKEN` and the `RCON_*_1` trio. Set `OWNER_IDS` too: without it
  nobody but the built-in super owner can run an owner command.
- **The game files:** `FACTION_ROLES_PATH` (the ServerRoles folder: rank files and menu tiers)
  and `MODSAVE_PATH` (the `<playername>.json` saves).
- **Worth setting:** `GUILD_ID` (commands appear in seconds instead of up to an hour) and
  `MASTER_NAMES` (your in-game names, never auto-banned).

Owner Discord user IDs come from `OWNER_IDS` / `SUPER_OWNER_IDS`. Owners pass every permission
check and can never be blacklisted. A staff ladder (**Super Owner, Owner, Admin, Mod**) decides
who may lift whose moderation actions: a lower tier never overrides a higher tier's ban.

## Development

```bash
cd rust
bash tools/gate.sh                # fmt, clippy -D warnings, the whole suite, release build
```

CI runs the same script on every push (`.github/workflows/test.yml`), then the selftest. No
Discord token or network is needed: RCON is tested against a fake server that records the
exact bytes on the wire. The crate layout is in [rust/README.md](rust/README.md).

## Backups (off-site, via rclone)

`scripts/backup.sh` takes a **consistent** snapshot of `bot.db` (WAL-safe via
`sqlite3 .backup`, so it's fine to run while the bot is writing — install the
`sqlite3` package), gzips it alongside the JSON exports, uploads both to a cloud
remote with [`rclone`](https://rclone.org), and prunes old backups. Run it from
system cron so it keeps working even when the bot is down.

The database lives in `DATA_DIR` (default `data/`) under the directory the bot
**runs** in. The script reads that from the systemd unit `pavlov-bot`
(its `WorkingDirectory`); set `BOT_DIR` only if the bot runs somewhere systemd
cannot tell it. It warns if the database it finds has not been written for a
day, which means it is looking at the wrong one.

```bash
# 1. install rclone and configure a remote (Backblaze B2 / S3 / R2 / Drive / …)
curl https://rclone.org/install.sh | sudo bash
rclone config                                    # creates a remote, e.g. "b2"

# 2. point the script at your remote (backup.env is git-ignored)
cp scripts/backup.env.example backup.env
# edit backup.env -> RCLONE_REMOTE=b2:my-bucket/pavlov-bot-backups

# 3. schedule it (hourly)
( crontab -l 2>/dev/null; echo "0 * * * * $(pwd)/scripts/backup.sh >> /var/log/pavlov-backup.log 2>&1" ) | crontab -
```

**Restore:**

```bash
systemctl stop pavlov-bot
rclone lsf b2:my-bucket/pavlov-bot-backups                    # list, pick a file
rclone copy b2:my-bucket/pavlov-bot-backups/botdb-YYYYMMDD-HHMMSS.db.gz /tmp/
cd "$(systemctl show -p WorkingDirectory --value pavlov-bot)/data"
gunzip -c /tmp/botdb-YYYYMMDD-HHMMSS.db.gz > bot.db
rm -f bot.db-wal bot.db-shm                                   # drop stale WAL
systemctl start pavlov-bot
```

Retention defaults to 30 days (`RETENTION_DAYS` in `backup.env`).
