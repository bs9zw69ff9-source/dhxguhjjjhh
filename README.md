# ☢️ Mojave Authority Bot

![CI](https://github.com/bs9zw69ff9-source/bot/actions/workflows/ci.yml/badge.svg)
![node](https://img.shields.io/badge/node-%E2%89%A518-339933)
![license](https://img.shields.io/badge/license-proprietary-c0392b)

A **Fallout: New Vegas–themed Discord moderation bot for [Pavlov VR](https://pavlov-vr.com/) servers.**
It drives one or two game servers over RCON and layers on a full server-management
suite — bans, warnings with auto-escalation, faction whitelists & ranks, a caps
economy with weekly wages and leaderboards, donator management, staff
applications, staff notes, and a tiered permission system — all behind themed,
auto-paginated slash commands with context-aware autocomplete.

> Single-file bot (`index.js`), **zero runtime config beyond `.env`**, no
> database — state is kept in small JSON files next to the bot.

---

## Table of contents

- [Features](#features)
- [How it works](#how-it-works)
- [Prerequisites](#prerequisites)
- [1. Discord application setup](#1-discord-application-setup)
- [2. Install](#2-install)
- [3. Configure](#3-configure)
  - [Environment variables](#environment-variables)
  - [In-code values you must review](#in-code-values-you-must-review)
  - [Role tiers](#role-tiers)
- [4. Run](#4-run)
  - [Bare metal](#bare-metal)
  - [systemd (recommended)](#systemd-recommended)
  - [PM2](#pm2)
  - [Docker / Compose](#docker--compose)
- [Command reference](#command-reference)
- [Automation](#automation--background-jobs)
- [Autocomplete](#autocomplete)
- [Data files](#data-files)
- [Pavlov server integration](#pavlov-server-integration)
- [Troubleshooting](#troubleshooting)
- [Development & testing](#development--testing)
- [Project layout](#project-layout)
- [License & ownership](#license--ownership)

---

## Features

- **Moderation** — kick, warn (auto-escalates to temp/permanent bans at 3/5/7),
  temp bans with auto-expiry, permanent bans, hard bans with an alt-account
  registry, per-player staff notes, and a full mod-action history log.
- **Punishment DMs** — `kick`/`warn`/`tempban`/`permban`/`hardban` can DM the
  punished player a breakdown via an optional `discord_user` field.
- **IP-match auto-ban (alt detection)** — tails the server logs to map IPs to
  players; banning someone flags their IPs, and the live log watcher then
  RCON-bans anyone who reconnects from a flagged IP (`ipBans.js`, `PAVLOV_LOGS`).
- **Factions** — per-faction whitelists, faction-specific rank ladders, member
  caps and per-rank caps, transfers, rosters, and an audit log. Rank changes
  update both the rank registry and the on-disk spawn/rank files.
- **Economy** — per-player caps balances, gifts, transfers (with rollback),
  manual adjustments, weekly automated wage payouts, and a caps leaderboard.
- **Playtime** — tracked while players are online; a top-30 playtime leaderboard
  auto-posts to a channel; surfaced per player in `/stats` and `/seen`.
- **Donators** — manage a donator whitelist text file the server reads.
- **Staff applications** — `/acceptstaffapp` DMs the applicant, grants staff
  roles, and posts a public welcome; `/denystaffapp` DMs a denial.
- **Access control** — Owner / Admin / Moderator / Faction-Leader tiers, an
  env-driven command blacklist, and per-command rate limiting.
- **UX** — branded embeds, interactive ◀ ▶ pagination on every list, and
  context-aware autocomplete that suggests offline players from a persistent
  "known players" registry.

## How it works

The bot is a single Node process that:

1. **Talks to Pavlov over RCON** (TCP + the MD5-password handshake) for live
   actions — kick, ban, player lists, server info, map rotation, raw commands.
2. **Reads/writes the server's local files** directly — faction spawn/rank
   files and the donator file in `FactionRoles/`, and per-player caps ledgers in
   `ModSave/`. *Because of this it's simplest to run the bot on the **same host**
   as the Pavlov server* (or somewhere with access to those paths).
3. **Persists its own state** in small JSON files (see [Data files](#data-files)).
   Writes are atomic (temp-file + rename) and serialized per file.
4. **Runs background jobs** on timers (see [Automation](#automation--background-jobs)).

No database, no message-content intent, no external services.

## Prerequisites

- **Node.js 18+** (`node -v`). The repo pins Node 20 via `.nvmrc`.
- A **Pavlov VR server** (or two) with **RCON enabled** (`RconSettings` in the
  server config — note the port + password).
- A **Discord application / bot** (free — see below).
- Filesystem access to the Pavlov server's `ModSave` directory (for factions,
  donators, and the caps economy).

---

## 1. Discord application setup

1. Go to the **[Discord Developer Portal](https://discord.com/developers/applications)** → **New Application**.
2. **Bot** tab → **Reset Token** → copy it → this is `DISCORD_TOKEN`.
3. **General Information** tab → copy the **Application ID** → this is `CLIENT_ID`.
4. **Privileged Gateway Intents:** none required. Leave Message Content and
   Server Members **off** — the bot only uses the default `Guilds` intent.
5. **Invite the bot** with the `bot` + `applications.commands` scopes and these
   permissions: **Manage Roles, View Channels, Send Messages, Embed Links, Read
   Message History.** Quick URL (replace `CLIENT_ID`):

   ```
   https://discord.com/api/oauth2/authorize?client_id=CLIENT_ID&permissions=268520448&scope=bot%20applications.commands
   ```

6. To get your server's **`GUILD_ID`** (only needed if you later want it),
   enable Developer Mode in Discord → right-click your server → Copy Server ID.

> **Role hierarchy matters:** for `/acceptstaffapp` and faction role logic to
> work, drag the **bot's role above** the roles it must assign in
> *Server Settings → Roles*. Discord won't let a bot manage roles at or above
> its own highest role.

Slash commands are **registered automatically** on startup (globally). Global
commands can take a few minutes to appear the first time.

## 2. Install

```bash
git clone https://github.com/bs9zw69ff9-source/bot.git mojave-authority-bot
cd mojave-authority-bot
npm install
cp .env.example .env        # then edit .env (next section)
```

## 3. Configure

### Environment variables

Copy `.env.example` → `.env` and fill it in. Only the first three groups are
required.

| Variable | Required | Description |
|---|---|---|
| `DISCORD_TOKEN` | ✅ | Bot token from the Developer Portal. |
| `CLIENT_ID` | ✅ | Application (client) ID — used to register slash commands. |
| `RCON_HOST_1` / `RCON_PORT_1` / `RCON_PASSWORD_1` | ✅ | Server 1 RCON connection. |
| `RCON_HOST_2` / `RCON_PORT_2` / `RCON_PASSWORD_2` | – | Server 2 RCON — enables the **Server 2 / Both** options. |
| `MODSAVE_PATH` | – | Pavlov `ModSave` dir holding per-player caps ledgers. Needed for balances, wages, and the caps leaderboard. |
| `DONATOR_PATH` | – | Full path of the donator whitelist file. Defaults to `<FactionRoles>/donator.txt`. |
| `MOD_LOG_CHANNEL` | – | Channel ID for the mod-action audit log. |
| `LEADERBOARD_CHANNEL` | – | Channel ID for the auto-posted **caps** leaderboard. |
| `PLAYTIME_LB_CHANNEL` | – | Channel ID for the auto-posted **playtime** leaderboard (has a hardcoded default — see below). |
| `BLACKLIST_IDS` | – | Discord user IDs barred from **all** commands (comma/space/newline separated). Owners are exempt. |
| `PAVLOV_LOGS` | – | Pavlov server log file(s) to tail for IP↔username mapping / IP-match auto-ban (comma or colon separated). Needed if the bot doesn't run as the server's owning user. |
| `LOG_LEVEL` | – | `DEBUG` \| `INFO` \| `WARN` \| `ERROR` (default `INFO`). `DEBUG` logs everything. |
| `BUILD_ID` | – | Build stamp shown in `/help` and `/ping`. |

> Logs print to the console **and** append to `bot.log` in the working directory.

### In-code values you must review

A few things are intentionally **hardcoded in `index.js`** (so they can't be
changed at runtime) or are server-specific. Open `index.js` and review these
near the top / in the constants block before going live:

| Constant | What it is |
|---|---|
| `OWNER_IDS` | Super-user Discord IDs — bypass every permission, never rate-limited, never blacklistable. **Set this to your own ID.** |
| `STAFF_ROLE_IDS` | The two role IDs granted by `/acceptstaffapp`. |
| `STAFF_ANNOUNCE_CHANNEL` | Channel the staff-accept welcome is posted to. |
| `PLAYTIME_LB_CHANNEL` (default) | Default playtime-leaderboard channel (override via env). |
| `FACTION_ROLES_PATH` | Absolute path to the Pavlov `FactionRoles` directory. **Set to your install.** |
| `FACTION_RANKS` / `SPAWN_FILE_MAP` | Faction names, rank ladders, badges, and the `.txt` filenames each maps to. Match these to your server's faction files. |
| `MENUS` | RCON menu IDs granted by `/givemenu` / `/stripmenu`. |
| `BAN_REASONS`, `WARN_THRESHOLDS`, `WAGE_TIERS` | Tunable reason list, auto-escalation thresholds, and wage amounts/tiers. |

### Role tiers

Permissions are tiered: **Public → Faction-Leader → Moderator → Admin → Owner.**
Map your Discord roles to tiers in-app:

```
/setroles mod_role:@Moderator admin_role:@Admin faction_leader_role:@FactionLeader
```

If a tier's role is left unset, that tier is **unrestricted** (anyone can use it)
— so set them before relying on access control. Owners (in `OWNER_IDS`) always
pass. Blacklisted users (in `BLACKLIST_IDS`) are blocked from everything.

## 4. Run

### Bare metal

```bash
npm start          # = node index.js
```

### systemd (recommended)

Run on the Pavlov host so the bot can reach the server files. Copy the unit and
edit `User` / `WorkingDirectory`:

```bash
sudo cp deploy/mojave-authority-bot.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now mojave-authority-bot
journalctl -u mojave-authority-bot -f      # follow logs
```

### PM2

```bash
npm i -g pm2
pm2 start ecosystem.config.js
pm2 save && pm2 startup
```

### Docker / Compose

The bot needs the Pavlov files, so bind-mount them at the same path the bot
expects. Edit the host paths in `docker-compose.yml`, then:

```bash
docker compose up -d --build
docker compose logs -f
```

(Or plain Docker: `docker build -t mojave-authority-bot . && docker run --env-file .env -v "$PWD:/app" -v /home/steam/.../ModSave:/home/steam/.../ModSave mojave-authority-bot`.)

---

## Command reference

Slash commands grouped by the role tier that can use them. Player fields support
[autocomplete](#autocomplete).

### 🌐 Public

| Command | Description |
|---|---|
| `/help` | Command roster + your current access level. |
| `/ping` | Bot + server health, latency, uptime. |
| `/listplayers <server>` | Live player list. |
| `/serverinfo <server>` | Map, mode, player count, status. |
| `/find <name>` | Search online players across both servers. |
| `/seen <id>` | When a courier was last online. |
| `/stats <id>` | Player dossier: status, playtime, factions, balance, donator, notes count. |
| `/checkban <id> <server>` | Check temp/hard/permanent ban status. |
| `/banlist <server>` | All active exiles (paginated). |
| `/warnings <id>` | A player's warnings (paginated). |
| `/checkbalance <id>` | A player's caps balance. |
| `/wagelist` | Everyone on the weekly payroll (paginated). |
| `/faction list\|overview\|audit` | Rosters, all-faction overview, and the change log. |

### 🛡️ Moderator

| Command | Description |
|---|---|
| `/kick <id> <server> [reason] [discord_user]` | Eject a player (optional punishment DM). |
| `/warn <id> <reason> <server> [discord_user]` | Warn — auto-bans at 3/5/7. |
| `/delwarn <id> <number>` | Remove a single warning. |
| `/tempban <id> <duration> <server> <reason> [discord_user]` | Timed exile (auto-lifts). |
| `/unban <id> <server>` | Lift an exile. |
| `/announce <message> <server> <target>` | RCON `Notify <target> <message>` to a player or **All**. |
| `/history <id>` | Full mod-action history (paginated). |
| `/note add\|list <id>` | Staff notes on a player. |
| `/givecaps <id> <amount> [reason]` | Grant caps. |
| `/faction transfer <from> <to> <id> [rank]` | Move a player between factions. |

### ⚔️ Faction Leader

| Command | Description |
|---|---|
| `/faction add <id> <faction> [rank]` | Whitelist a player into a faction. |
| `/faction remove <faction> <id>` | Remove from a faction. |
| `/faction rank <faction> <id> <rank>` | Set a member's rank (FL only). |
| `/addwage <id> <tier>` | Enrol in payroll or pay a one-time mercenary sum. |
| `/removewage <id>` | Remove from payroll. |

### 🔒 Admin

| Command | Description |
|---|---|
| `/permban <id> <server> <reason> [notes] [discord_user]` | Permanent ban. |
| `/hardban <id> <server> <reason> [linked_id] [notes] [discord_user]` | Hard ban + alt registry. |
| `/addnote <id> <note>` | Append a note to a hard-ban record. |
| `/hardbanlist` | The hard-ban registry (paginated). |
| `/clearwarnings <id>` | Wipe all of a player's warnings. |
| `/note clear <id>` | Delete a player's staff notes. |
| `/cleartempbans` | Lift all temp bans (with confirm). |
| `/setroles […]` | Map Discord roles to permission tiers. |
| `/givemenu` / `/stripmenu <id> <server> <menu>` | Grant/revoke an RCON menu (re-applied on rejoin). |
| `/transfercaps <from_id> <to_id> <amount>` | Move caps between players (rolls back on failure). |
| `/adjustcaps <id> <amount> [reason]` | Add/subtract caps. |
| `/rotatemap <server>` | Rotate the map (with confirm). |
| `/manual <command> <server>` | Send a raw RCON command. |
| `/donator add\|remove\|list <id>` | Manage the donator whitelist file. |
| `/autoban add\|remove\|list <pattern>` | Auto-ban players whose name contains a pattern (now + on join). |
| `/acceptstaffapp <user>` | DM acceptance, grant staff roles, post a public welcome. |
| `/denystaffapp <user> [reason]` | DM a denial (no other action). |
| `/faction setcap <faction> <cap>` | Set a faction's member cap. |
| `/faction setrankcap <faction> <rank> <cap>` | Cap members per rank (0 = unlimited). |

## Automation & background jobs

| Job | Interval |
|---|---|
| Lift expired temp bans | every **60s** |
| Refresh player cache + accrue playtime + re-apply menu grants | every **60s** |
| Caps leaderboard (top 30) | every **6h** |
| Playtime leaderboard (top 30) | every **6h** |
| RCON health check | every **5 min** |
| Weekly wage payout | every **7 days** |

Warn auto-escalation: **3** → 1-day ban · **5** → 1-week ban · **7** → permanent.

## Autocomplete

Player fields suggest the population relevant to the command, e.g. `/unban`
lists only banned players, `/stripmenu` only menu-grant holders, `/faction
remove` only that faction's members. Add/lookup commands suggest currently
**online** players first, then previously-seen **offline** players from the
persistent `known_players.json` registry (so typing `ncr_` surfaces
`ncr_private (offline)` even when they're not in-game). You can always type an
ID manually. The registry is seeded on startup from existing data (playtime,
faction files, payroll, donators, bans).

## Data files

Created automatically in the working directory; **git-ignored** (state, not
source). Atomic + serialized writes.

| File | Contents |
|---|---|
| `tempbans.json` | Active temp bans (with expiry). |
| `hardban_registry.json` / `hardban_notes.json` | Hard bans + alt links / staff notes on them. |
| `warnings.json` | Per-player warnings. |
| `modlog.json` | Mod-action history (capped at 10k). |
| `roles.json` | Role→tier mapping from `/setroles`. |
| `wages.json` | Payroll enrolments. |
| `playtime.json` | Minutes per player. |
| `lastseen.json` | Last-online timestamps. |
| `known_players.json` | Every player ever seen (powers offline autocomplete). |
| `ip_registry.json` / `ip_blacklist.json` | IP↔username map from logs / flagged (banned) IPs. |
| `faction_ranks.json` / `faction_config.json` / `faction_audit.json` | Per-player ranks / caps & rank-caps / change log. |
| `menu_grants.json` | Persistent RCON menu grants (re-applied on rejoin). |
| `player_notes.json` | Freeform staff notes on any player. |

> The **donator list** and **faction spawn/rank files** live on the Pavlov
> server (in `FactionRoles/`), not here — the bot edits those in place.

## Pavlov server integration

- **RCON** — set `RCON_HOST/PORT/PASSWORD` per server. The bot uses the standard
  Pavlov MD5 handshake.
- **`FactionRoles/`** (`FACTION_ROLES_PATH` in `index.js`) — faction spawn files,
  per-rank files, and `donator.txt`. The bot reads and rewrites these.
- **`ModSave/`** (`MODSAVE_PATH`) — per-player caps ledgers (`<id>.txt`). Used by
  balances, wages, and the caps leaderboard.

Run the bot where these paths are reachable (same host, or bind-mounted in
Docker at the **same absolute path**).

## Troubleshooting

- **Slash commands don't appear** — global registration can take a few minutes
  on first run; check the startup log for "slash commands registered" and that
  `CLIENT_ID`/`DISCORD_TOKEN` are correct.
- **`/acceptstaffapp` says "could not grant roles"** — give the bot **Manage
  Roles** and drag its role **above** the staff roles.
- **Caps/leaderboard empty** — set `MODSAVE_PATH` to the correct `ModSave` dir.
- **Donator/faction edits not taking** — verify `FACTION_ROLES_PATH` (and
  `DONATOR_PATH`) point at the files the server actually reads, and that the bot
  user can write them. `/donator add` shows the exact path it wrote to.
- **DMs not delivered** — the target must share a server with the bot and allow
  DMs; failures are reported, never fatal.
- **Need verbose logs** — set `LOG_LEVEL=DEBUG`.

## Development & testing

```bash
npm run check   # node --check index.js (syntax)
npm test        # unit tests for the pure logic — no token / Discord / network
```

The test runner sandboxes the bot with stub modules so it can `require` and
exercise the exported helpers without `discord.js` installed. CI runs both on
every push (`.github/workflows/ci.yml`).

## Project layout

```
index.js                     the entire bot
ipBans.js                    IP↔username log parser + IP-match auto-ban
test/run.js                  self-contained unit tests (npm test)
.env.example                 annotated env template
package.json .nvmrc          deps + Node version
Dockerfile .dockerignore     container build
docker-compose.yml           Compose deployment
deploy/…service              systemd unit
ecosystem.config.js          PM2 config
.github/workflows/ci.yml     CI
LICENSE NOTICE SECURITY.md   legal / security policy
CHANGELOG.md                 release notes
```

## License & ownership

This is **proprietary** software — see [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).
No permission is granted to copy, modify, distribute, or host it without written
authorization. Authorship is evidenced by the git commit history, the copyright
headers, and the in-app attribution shown in `/help` and `/ping`.

> Want to *allow* others to run it but force them to publish modifications?
> Switch the license to **AGPL-3.0** instead.

### Protecting the project from theft (legitimate measures)

- **Don't ship secrets.** Tokens and RCON passwords live in `.env` (git-ignored),
  so a stolen `index.js` is useless without the operator's own configuration.
- **Keep the git history** — commit timestamps are strong proof of authorship.
- **Visible attribution / build stamp** — `/help` and `/ping` show the copyright
  and `BUILD_ID`, so unauthorized copies are identifiable.
- **Canary tokens (optional)** — a token from <https://canarytokens.org> alerts
  *you* if a leaked copy is opened; it doesn't tamper with anyone's system.
- **Takedowns** — file a DMCA with the host (GitHub, etc.) citing your history.

> Hidden "backdoors" (covert remote access, kill switches, data exfiltration) are
> **not** an anti-theft measure — they're illegal in most jurisdictions and
> become the very vulnerability attackers exploit. The measures above are the
> safe, effective alternatives.
