# Pavlov RP Moderation Bot

A theme-neutral Discord moderation bot for **Pavlov VR** RP servers. It drives
the game servers over RCON and adds native bans/kicks/mutes with punishment
presets, ban-evasion + VPN/proxy detection with optional OS-firewall blocking,
faction whitelists and ranks, a credits economy with a casino, weekly wages and
leaderboards, donator management, self-service RCON menus, and a layered
owner/admin/mod/faction permission model — all behind slash commands.

It ships **brand-neutral** and skins to any RP: set `BOT_NAME` in `.env` (e.g.
`Mojave Authority`, `LSPD Command`) and every embed is stamped with it.

## Requirements

- **Node.js 18+** (uses `structuredClone`, `fetch`, `Intl.DisplayNames`)
- One to three Pavlov servers with RCON enabled
- A Discord application + bot token
- `better-sqlite3` compiles a native binding on install — a C toolchain is
  needed if a prebuilt binary isn't available for your platform

## Setup

```bash
npm install
cp .env.example .env      # then fill in the values
npm start
```

Slash commands are registered automatically on startup.

## Architecture

`index.js` is the composition root: it validates config, opens the database,
constructs the shared services, and wires the feature modules together by
dependency injection (a plain `ctx` object). Each module is a factory that
destructures what it needs from `ctx`, so there are no circular `require`s and
no hidden globals.

```
index.js              entry point + wiring (the ctx is built here)
ipBans.js             Pavlov.log tailer: EOS-id ↔ IP ↔ name tracking, auto-ban
commands/             slash-command dispatcher (index.js) + domain handlers
                      (moderation, admin, factions, economy, casino, info)
                      and definitions.js (every SlashCommandBuilder)
events/               clientReady handler + ipBans join/leave/kill/auto-ban hooks
moderation/           bans.js (RCON enforce/reconcile/unban), vpn.js (IPHub/IPQS
                      + geolocation), firewall.js (ufw block/unblock/status)
factions/             ranks.js (rank registry), files.js, whitelist.js
casino/               games.js (pure math), ledger.js (atomic caps ledger)
leaderboards/         caps + playtime boards, live player list, server dashboard
database/             SQLite (bot.db) storage layer + JSON export
rcon/                 Pavlov RCON transport (TCP + md5 auth)
discord/              theme.js (palette, quotes, brand, embed builders)
utils/                pure string/time/crypto helpers
test/                 node:test unit + wiring suites
```

Modules communicate only through the injected `ctx`. `test/wiring.test.js`
statically verifies that every dependency a module destructures (or spreads)
from `ctx` is actually provided — so a mis-wire fails the test suite, not the
live bot.

## Configuration

All configuration is via environment variables — see [`.env.example`](.env.example)
for the full annotated list.

- **Required:** `DISCORD_TOKEN`, `CLIENT_ID`, and the `RCON_*_1` trio.
- **Optional:** servers 2–3, ModSave/economy paths, log/leaderboard channel IDs,
  role IDs, and:
  - `BOT_NAME` — display name stamped on every embed (default `Server Authority`)
  - `IPHUB_API_KEY` / `IPQS_API_KEY` / `IPINFO_TOKEN` — VPN/proxy detection + geo
  - `UFW_BLOCK=1` — also deny banned/flagged IPs at the OS firewall via `sudo ufw`
  - `DB_EXPORT_INTERVAL_MS` — how often SQLite is mirrored back to `.json` backups

### Owner override

Owner Discord user IDs are hardcoded in `OWNER_IDS` at the top of `index.js`.
Owners pass every permission check, skip rate limits, and can never be
blacklisted. Master in-game names (`MASTER_NAMES`) and a master IP allowlist
(`MASTER_IPS`) are likewise code-only — master names are never banned/tracked,
and master IPs are never firewall-blocked (a periodic reconcile enforces this).

## Commands

| Tier | Commands |
|------|----------|
| 🌐 Public | `/help` `/ping` `/dashboard` `/serverinfo` `/find` `/checkban` `/banlist` `/stats` `/kd` `/checkbalance` `/wagelist` `/link` · casino: `/slots` `/coinflip` `/blackjack` `/roulette` `/cockfight` `/russianroulette` `/jackpot` |
| 🛡️ Moderator | `/kick` `/mute` `/unmute` `/flush` `/tempban` `/unban` `/announce` `/givecaps` |
| ⚔️ Faction Leader | `/faction add\|remove\|rank\|transfer\|setcap\|setrankcap\|wipe` `/addwage` `/removewage` |
| 🔒 Admin | `/permban` `/cleartempbans` `/setroles` `/setrconroles` `/givemenu` `/stripmenu` `/manual` `/adjustcaps` `/donator` `/staffactivity` `/staffleaderboard` `/casino` |
| 👑 Owner | `/configure` (control panel) `/inspect` `/firewall block\|unblock\|status` `/autorotate` `/stripmenuall` `/clearallbans` |

Read-only `/faction list\|audit\|playtime` are public. Roles map to tiers with
`/setroles`; an unset tier role means that tier is unrestricted. `/tempban`
uses punishment presets that set the duration automatically.

### Automation

- Temp-ban expiry sweep every **60s**; online-ban enforcement sweep every **30s**
- Ban-list reconcile from the DB every **5 min**
- Leaderboards / player list refreshed on a short interval; weekly wages every **7 days**
- VPN check on connect (one lookup per new IP, cached); auto-ban on a confirmed hit
- Firewall reconcile every **2 min** when `UFW_BLOCK=1` (keeps flagged IPs blocked,
  master IPs never blocked); SQLite → JSON export every **10 min**

## Data

State lives in **SQLite** (`bot.db`, WAL mode). The legacy `.json` files are kept
as a human-readable backup and are refreshed periodically from the DB. `ipBans.js`
shares the same database. Everything runtime (`bot.db*`, the `.json` state files,
logs) is **git-ignored** — it's state, not source.

## Development

```bash
npm run check   # syntax check (node --check on index.js + ipBans.js)
npm test        # node:test suites — pure logic + the module-wiring guard
```

Tests cover the pure/leaf modules (utils, casino math, theme, firewall guard,
SQLite round-trip in an isolated temp dir, faction rank registry, ipBans) plus a
static wiring check of every module's `ctx` contract. No Discord token or network
is needed to run them.

## Backups (off-site, via rclone)

`scripts/backup.sh` takes a **consistent** snapshot of `bot.db` (WAL-safe — it
uses the bot's own better-sqlite3 online backup, so it's fine to run while the
bot is writing), gzips it alongside the JSON exports, uploads both to a cloud
remote with [`rclone`](https://rclone.org), and prunes old backups. Run it from
system cron so it keeps working even when the bot is down.

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
pm2 stop pavlov-bot
rclone lsf b2:my-bucket/pavlov-bot-backups                    # list, pick a file
rclone copy b2:my-bucket/pavlov-bot-backups/botdb-YYYYMMDD-HHMMSS.db.gz /tmp/
gunzip -c /tmp/botdb-YYYYMMDD-HHMMSS.db.gz > bot.db
rm -f bot.db-wal bot.db-shm                                   # drop stale WAL
pm2 start pavlov-bot
```

Retention defaults to 30 days (`RETENTION_DAYS` in `backup.env`).

## Deployment

The intended flow is a git checkout on the host:

```bash
git pull origin <branch> && pm2 restart <app>
```

Because the bot is split across many small modules, deploy the whole tree
together (a partial copy fails at boot) — `git pull` guarantees that.
