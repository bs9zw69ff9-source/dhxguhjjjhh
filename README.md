# Pavlov RP Moderation Bot

> **This bot runs on C# / .NET 9.** The source is in [`dotnet/`](dotnet/), and
> [`dotnet/README.md`](dotnet/README.md) is the reference for it.
>
> The Node.js implementation that used to live in this directory was the rollback target and is
> no longer the deployment. It still builds, still passes its tests, and still
> reads and writes the same `bot.db` — that is the point of keeping it. It will
> be removed once C# has a run of live service behind it.

A theme-neutral Discord moderation bot for **Pavlov VR** RP servers. It drives
the game servers over RCON and adds native bans/kicks with a reason + duration picker,
ban-evasion + VPN/proxy detection with optional OS-firewall blocking, whitelists
and ranks, a dollar economy, a wealth leaderboard, donator management,
self-service RCON menus, and a layered owner/admin/mod/whitelist permission
model — all behind slash commands.

It ships **brand-neutral** and skins to any RP: set `BOT_NAME` in `.env` (e.g.
`Mojave Authority`, `LSPD Command`) and every embed is stamped with it.

## Requirements

- **.NET 9 SDK** to build. Nothing to install on the game host — the deploy
  publishes self-contained.
- One to three Pavlov servers with RCON enabled
- A Discord application + bot token

(Removed: the Node rollback target needed Node.js 18+, and `better-sqlite3` compiled a
native binding on install.

## Setup

**Setting up a fresh VPS: see [INSTALL.md](INSTALL.md)** — prerequisites, the
Discord application, file permissions and the checks that prove it came up.

On a box that is already prepared:

```bash
cp .env.example .env                      # then fill in the values
bash scripts/deploy-csharp.sh             # build + verify, starts nothing
bash scripts/deploy-csharp.sh --start     # start it under pm2
```

`deploy-csharp.sh` gates on `--selftest`, which builds the entire object graph
and exits without connecting: if the configuration is wrong it prints every
problem at once and changes nothing.

It used to **refuse to start while the Node bot was online**. Both bots on one
Discord application would answer every slash command twice and issue every ban
twice, and nothing inside either can undo that.

Slash commands are registered automatically on startup.

### Rolling back

The Node bot it used to roll back to has been removed, so there is no second app
to switch to. Rolling back means deploying an earlier commit:

```bash
bash scripts/deploy.sh <older-sha>
```

The on-disk format is still the one the Node bot wrote — camelCase keys,
millisecond timestamps — because those files are read on a live server and the
shape is a contract rather than history. `NodeCompatibilityTests` keeps it that
way.

## Architecture

The bot is the C# solution under [`dotnet/`](dotnet/):

```
dotnet/src/PavlovBot.Core   pure domain logic - no Discord, sockets or files (AOT-safe)
dotnet/src/PavlovBot.Rcon   the Pavlov RCON protocol: one persistent, serialised session per server
dotnet/src/PavlovBot.Host   the process: Discord gateway, commands, background services,
                            storage (SQLite), log tailing, systemd/ufw control
dotnet/tests                the test suite, including a fake RCON server that records the wire
```

`Program.cs` in the host is the composition root. [`dotnet/README.md`](dotnet/README.md)
covers the layout, the RCON design and how it is run. (`ARCHITECTURE.md` describes the
Node bot this replaced and is kept only as history.)

## Configuration

All configuration is via environment variables — see [`.env.example`](.env.example)
for the full annotated list.

- **Required:** `DISCORD_TOKEN`, `CLIENT_ID`, and the `RCON_*_1` trio.
- **Optional:** servers 2–9, ModSave/economy paths, log/leaderboard channel IDs,
  role IDs, and:
  - `BOT_NAME` — display name stamped on every embed
  - `IPHUB_API_KEY` / `IPQS_API_KEY` / `PROXYCHECK_API_KEY` / `SENTINEL_API_KEY` — VPN/proxy detection
  - `GEOAPIFY_API_KEY` — geolocation and the location map on connect cards
  - `FIREWALL_BLACKLIST` — on by default: an IP an owner blacklists is also denied at ufw
  - `HOME_GUILD_ID` — the staff guild; the only one where Discord's Administrator counts
  - `METRICS_PORT` — `/metrics`, `/health`, `/healthz`, `/ready` on 127.0.0.1

### Owner override

Owner Discord user IDs come from `OWNER_IDS` / `SUPER_OWNER_IDS` in `.env`,
plus one super owner compiled into `PavlovBot.Core/Security/OwnerGuard.cs`.
Owners pass every permission check, skip rate limits, and can never be
blacklisted. Master in-game names (`MASTER_NAMES`, plus one compiled in) are
never auto-banned.

### Staff hierarchy

A staff tier ladder governs who may **override** (lift/undo) whose moderation
actions — `SUPER_OWNER_IDS` sits above `OWNER_IDS`:

**Super Owner → Owner → Admin → Mod**

Each moderation action records the tier of the staffer who issued it, and a lower
tier can never override a higher tier's action:

- A **Super Owner** can override anything.
- An **Owner** cannot override a Super Owner's ban.
- An **Admin** cannot override a Super Owner's or Owner's.
- A **Mod** cannot override a Super Owner's, Owner's, or Admin's.

Equal tiers can override each other. This is enforced on `/unban`,
re-banning an already-banned player, and the bulk `/cleartempbans` / `/clearallbans`
(which skip protected higher-tier bans). The tier rules are a pure, unit-tested
module — [`moderation/hierarchy.js`](moderation/hierarchy.js). Bans made before this
feature carry no tier and stay overridable by anyone, so nothing is retroactively
locked.

## Commands

| Tier | Commands |
|------|----------|
| 🌐 Public | `/help` `/serverinfo` `/checkban` |
| 🛡️ Moderator | `/kick` `/flush` `/tempban` `/unban` `/announce` `/givecaps` |
| ⚔️ Whitelist Leader | `/whitelist add\|remove` `/promotion` `/demotion` `/subclass` |
| 🚔 Police Officer | `/warrant give\|remove\|check` `/arrest` `/backgroundcheck` |
| 🔒 Admin | `/permban` `/cleartempbans` `/givemenu` `/stripmenu` `/manual` `/adjustcaps` `/donator` `/staffactivity` `/staffleaderboard` |
| 👑 Owner | `/setroles` `/setrconroles` `/configure` (control panel — incl. wipe money / wipe all player data) `/inspect` `/health` `/firewall block\|unblock\|status` `/stripmenuall` `/clearallbans` `/whitelist wipe` |

Read-only `/whitelist list\|playtime` are public. `/setroles` and `/setrconroles` are
Owner-only: they decide who IS staff, so an admin could otherwise make access for
themselves that outlives their own. Roles map to tiers with
`/setroles` (`mod_role`, `admin_role`, `whitelist_leader_role`, `police_role`,
`gambino_role`, `colombo_role`, `nypd_role`); an unset tier role means that tier
is unrestricted.

**Whitelist management is per-faction.** The general **Whitelist Leader**
(`whitelist_leader_role`) manages every whitelist, while the **Gambino**
(`gambino_role`), **Colombo** (`colombo_role`), and **NYPD** (`nypd_role`) roles
manage only their own whitelist. Those per-faction options are **built from the
factions this bot actually runs**, so a deployment using `FACTIONS_PATH` gets one
option per faction of its own (`ncr_role`, `legion_role`, …) rather than these
three. One role may manage several factions by naming it in more than one option.
Mods, admins, and owners can manage any of them. A manager can
`/promotion` / `/demotion` a member one rank up or down the faction's ladder, and
`/subclass` assigns or removes a **sub-class** — an extra designation a member
holds alongside their rank (NYPD ships with **Detective** and **Vice Officer**).
New members join at the lowest rank; sub-classes survive promotions and are only
cleared when the member leaves the whitelist.

The **Police Officer** role (`police_role`) gates the warrant board. Warrants
**stack** — a player can hold several. `/warrant give <player> <reason>` requires
a reason and adds one; `/warrant check <player>` shows that player's numbered
stack (or lists everyone with warrants when blank); `/warrant remove <player>
[number]` clears one warrant by its number, or all of them when no number is
given. Admins and owners can manage warrants too.

`/tempban` takes a free-text reason and a ban length picked from a list (1h, 1d, 1w, ... or Permanent).

### Police RP (arrests & records)

`/arrest <player>` opens an interactive booking: pick a penal-code section, then
the charge(s) from it, with a running total for jail time and bail - add as many
as needed, then **Confirm Arrest**. The charges, jail time, and bail are recorded,
a sentence timer starts, and a booking notice posts to `ARREST_CHANNEL` (falls back to the mod-log);
when the sentence ends the bot posts a release notice. The penal code lives in
[`penal/codes.js`](penal/codes.js). `/backgroundcheck <player>` shows a player's
active warrants, arrests, and total jail time served. Both are gated to the
**Police Officer** role. `/suspendrank <player> <time>` (mod) pulls a member's
whitelist rank for a duration (`30m`, `2h`, `1d`) and auto-restores it when it
expires. `/bail increase|decrease <percent>` (mod) scales the bail price on every
charge by a percentage (compounding, rounded to the nearest dollar); `/bail reset`
returns to the base prices and `/bail show` prints the current multiplier. *Note:* the bot records/announces the sentence — it does not itself jail
or ban the player in-game.

### Verification

An optional member-gate. Set `VERIFY_CHANNEL` (public), `VERIFY_STAFF_CHANNEL`
(private), and `VERIFIED_ROLE`. On startup the bot posts a **Verify** button in
the verify channel and auto-creates an **Unverified** role. Channels stay
**public** — the bot doesn't lock them behind a role; instead it denies the
**Unverified** role from viewing every channel except the verify one (needs
*Manage Roles* + *Manage Channels*, and the bot's role above the Unverified
role). Assign the Unverified role to newcomers (via Discord onboarding, since the
bot doesn't use the privileged join intent) to actually gate them. A member
presses **Verify**, enters their exact Pavlov name, and the bot links that name
to their **confirmed IP(s)** (trustworthy same-line pairings from the connection
tracker) and posts an accept/deny request to the staff channel. **One person per
name, and no alts** — a name or a confirmed IP already tied to another verified
member is rejected. On approval the bot removes the Unverified role (and adds the
Verified label), and the Discord→IP link is logged to the `CONNECT_WEBHOOK_URL`
feed. Nothing else changes (no whitelist/rank side effects).

### Automation

- Temp-ban expiry sweep every **60s**; online-ban enforcement sweep every **30s**
- Ban-list reconcile from the DB every **5 min**
- Leaderboards / player list refreshed on a short interval
- VPN check on connect (one lookup per new IP, cached); auto-ban on a confirmed hit
- SQLite → JSON export every **15 min**

## Data

State lives in **SQLite** (`bot.db`, WAL mode). The legacy `.json` files are kept
as a human-readable backup and are refreshed periodically from the DB. `ipBans.js`
shares the same database. Everything runtime (`bot.db*`, the `.json` state files,
logs) is **git-ignored** — it's state, not source.

## Development

```bash
```

Tests cover the pure/leaf modules (utils, theme, firewall guard,
SQLite round-trip in an isolated temp dir, faction rank registry, ipBans, the
plain-text renderer), a static wiring check of every module's `ctx` contract,
and handler-level tests that drive the kick/tempban/unban/checkban flows against
a stubbed ctx (master-name guards, hierarchy overrides, flag-visibility gating).
No Discord token or network is needed. The same suite runs in CI on every push
(`.github/workflows/test.yml`).

Deploys are one command on the VPS — `bash scripts/deploy.sh` fetches the branch,
hard-resets the working tree (git-ignored state like `.env`/`bot.db` untouched),
and restarts pm2. `/health` (owner) shows uptime, per-server RCON reachability,
and the warn/error counters + recent entries the resilient error handling would
otherwise hide.

## Backups (off-site, via rclone)

`scripts/backup.sh` takes a **consistent** snapshot of `bot.db` (WAL-safe via
`sqlite3 .backup`, so it's fine to run while the bot is writing — install the
`sqlite3` package), gzips it alongside the JSON exports, uploads both to a cloud
remote with [`rclone`](https://rclone.org), and prunes old backups. Run it from
system cron so it keeps working even when the bot is down.

The database lives in `DATA_DIR` (default `data/`) under the directory the bot
**runs** in, so tell the script where that is with `BOT_DIR` — for the live bot,
`/root/pavlov-bot-fallout`. It warns if the database it finds has not been
written for a day, which means it is looking at the wrong one.

```bash
# 1. install rclone and configure a remote (Backblaze B2 / S3 / R2 / Drive / …)
curl https://rclone.org/install.sh | sudo bash
rclone config                                    # creates a remote, e.g. "b2"

# 2. point the script at your remote (backup.env is git-ignored)
cp scripts/backup.env.example backup.env
# edit backup.env -> RCLONE_REMOTE=b2:my-bucket/pavlov-bot-backups

# 3. schedule it (hourly)
( crontab -l 2>/dev/null; echo "0 * * * * BOT_DIR=/root/pavlov-bot-fallout $(pwd)/scripts/backup.sh >> /var/log/pavlov-backup.log 2>&1" ) | crontab -
```

**Restore:**

```bash
pm2 stop pavlov-bot-fallout
rclone lsf b2:my-bucket/pavlov-bot-backups                    # list, pick a file
rclone copy b2:my-bucket/pavlov-bot-backups/botdb-YYYYMMDD-HHMMSS.db.gz /tmp/
cd /root/pavlov-bot-fallout/data
gunzip -c /tmp/botdb-YYYYMMDD-HHMMSS.db.gz > bot.db
rm -f bot.db-wal bot.db-shm                                   # drop stale WAL
pm2 start pavlov-bot-fallout
```

Retention defaults to 30 days (`RETENTION_DAYS` in `backup.env`).

## Deployment

The intended flow is a git checkout on the host:

```bash
git pull origin <branch> && pm2 restart <app>
```

Because the bot is split across many small modules, deploy the whole tree
together (a partial copy fails at boot) — `git pull` guarantees that.
