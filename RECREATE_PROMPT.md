# Build Prompt — Pavlov VR RCON Moderation & Roleplay Discord Bot

> Paste everything below into an AI coding agent to recreate this bot from scratch.

---

## 0. Objective

Build a production-grade **Node.js Discord bot** that administrates one to three **Pavlov VR (Shack)** dedicated game servers over **RCON**. It is a moderation, anti-ban-evasion, whitelist/faction, police-roleplay, and economy platform. Target ~10,000 lines across ~30 modules with a real unit-test suite.

**Non-negotiables:** never crash the process on a command error; never silently corrupt player data; never lose a ban; every destructive action is logged and reversible where possible.

---

## 1. Stack & Constraints

- **Node.js ≥ 18** (native `fetch`), CommonJS (`require`, not ESM).
- **discord.js v14** — `SlashCommandBuilder`, `EmbedBuilder`, `ActionRowBuilder`, `ButtonBuilder`, `StringSelectMenuBuilder`, `ModalBuilder`, `TextInputBuilder`, `PermissionFlagsBits`, `MessageFlags`.
- **better-sqlite3** for storage. **No ORM.**
- **Zero privileged Discord intents.** Never require `GuildMembers`/`MessageContent`. Resolve members via `guild.members.fetch({ query })` (op8) only.
- Test runner: `node --test --test-force-exit`. No Jest/Mocha.
- Only other runtime dep allowed: none. Everything else is Node built-ins (`net`, `fs`, `path`, `crypto`).

---

## 2. Architecture — Context Dependency Injection

**`index.js` is the composition root** (single large file, ~2,900 lines). It owns all shared state and wires every other module.

Every non-pure module is a factory:

```js
module.exports = function createThing(ctx) {
  const { logger, sendRcon, safeRead /* ...deps */ } = ctx;
  function doWork() { /* ... */ }
  return { doWork };
};
```

`index.js` calls `require("./thing")({ logger, sendRcon, safeRead, ... })`. **No module ever requires `index.js`** — this guarantees no circular dependencies. Pure data/logic modules (`utils`, `penal/codes`, `stats/peaks`, `moderation/hierarchy`, `verify/rules`, `factions/ranks`) may be required directly since they hold no state.

**Module map:**

| Path | Responsibility |
|---|---|
| `index.js` | Composition root: config, storage wiring, all shared helpers/state |
| `rcon/index.js` | Pavlov RCON TCP transport (md5 auth) |
| `database/index.js` | SQLite layer, dataset registry, serialized writes, JSON export |
| `discord/theme.js` | Colour palette, glyphs, shared embed builders |
| `discord/textify.js` | Renders embeds to plain text at interaction send-time |
| `commands/index.js` | Single `interactionCreate` dispatcher + permission routing |
| `commands/definitions.js` | Every `SlashCommandBuilder` (registration payload) |
| `commands/{info,moderation,admin,factions,economy,warrant,police}.js` | Handlers by domain |
| `moderation/bans.js` | Native RCON ban/kick enforcement, sweep, reconcile, unban |
| `moderation/vpn.js` | IPHub/IPQS/proxycheck detection, geolocation, VPN auto-ban |
| `moderation/firewall.js` | Optional OS `ufw` block (manual owner action only) |
| `moderation/hierarchy.js` | Pure staff-tier + override rules |
| `ipBans.js` | **Standalone** Pavlov.log tailer: EOS/IP/name registry, alts, auto-ban, K/D |
| `factions/{ranks,files,whitelist}.js` | Rank registry (pure data), roster file I/O, snapshots |
| `penal/codes.js` | Pure penal-code data + helpers |
| `leaderboards/index.js` | Auto-posting boards, live player list, server dashboard |
| `events/index.js` | `clientReady` + ipBans join/confirm/kill/autoban callbacks |
| `casino/ledger.js` | Atomic per-player balance mutation queue |
| `stats/peaks.js`, `utils/index.js`, `verify/rules.js` | Pure helpers |

**A static test must enforce the ctx contract** (see §14).

---

## 3. RCON Transport (`rcon/index.js`)

Pavlov RCON is a line-based TCP protocol with md5 password auth.

```
connect → server sends "Password:" → write md5(password)
        → server sends "Authenticated=1" → write "<command>\n" → read response
```

Requirements:
- `sendRconRaw(command, server, timeoutMs)` returns a Promise that **settles exactly once** (guard flag) and **always clears its fallback timer** and destroys the socket.
- A socket that closes/times out **before authenticating** must **reject** ("no RCON auth"), never resolve empty — otherwise a dead server reports success.
- `sendRcon(cmd, server, timeoutMs=3000, retries=2)` — retry with exponential backoff (500ms × 2^attempt), log each failure, throw the last error.
- `sendRconBoth(cmd, server)` — `"both"` fans out to all active servers via `Promise.allSettled` (one dead server must never mask another). Uses tight timings (2500ms, 1 retry) so a slash command can't hang ~10s.
- Servers configured via `RCON_HOST_{1,2,3}` / `RCON_PORT_{n}` / `RCON_PASSWORD_{n}`. Server 1 required; 2 and 3 optional.

**Pavlov commands used:** `RefreshList`, `ServerInfo`, `Ban <UniqueId>`, `Kick <UniqueId>`, `Unban <UniqueId>`, `Notify`, `Gag`, `GiveItem`, `SwitchMap`, plus RCON-Plus menu commands (`GiveMenu`, `ClearMenuAccess`, `AddMod`, `AddAccessManager`, `ClearAccessManagers`).

> **Critical:** `Ban`/`Kick` take the player's **UniqueId** (on Shack that is the Oculus username), which `RefreshList` returns alongside the display `Username`. The two can differ, and display names may contain spaces. Always enforce against the **UniqueId** when available; only fall back to the display name.

---

## 4. Storage (`database/index.js`)

**SQLite (`bot.db`) is the source of truth.** Single table:

```sql
CREATE TABLE IF NOT EXISTS kv (file TEXT PRIMARY KEY, data TEXT NOT NULL)
```
`journal_mode = WAL`, `busy_timeout = 5000`.

- A **`FILES` registry** maps logical dataset names to `./name.json` keys (37 datasets: `TEMPBAN, ROLES, WAGES, PLAYTIME, MODLOG, FACTION_RANKS, FACTION_CONFIG, FACTION_AUDIT, FACTION_BACKUP, MENU_GRANTS, LASTSEEN, KNOWN, USER_BLACKLIST, USER_UNBARRED, MENU_PANEL, MENU_ROLES, MENU_LINKS, AUTOROTATE, MUTES, DISCORD_LINKS, AUTOBAN_EXEMPT, CASINO_CONFIG, UPDATE_LOG_STATE, BAN_RECONCILE_STATE, CASINO_QUOTA, CASINO_POT, AUTOPOST_STATE, VPN_CHECKS, DONATOR_SUSPEND, SERVER_STATS, WARRANTS, VERIFICATIONS, VERIFY_STATE, ARRESTS, SENTENCES, RANK_SUSPENSIONS, POLICE_CONFIG`), each with a JSON default.
- **One-time migration:** on boot, import any pre-existing `.json` file into `kv`, then seed defaults for anything missing.
- **API:** `safeRead(file, fallback)` (cached, returns a **deep clone** so callers can't mutate cache), `safeWrite(file, data)`, and `update(file, fallback, mutator)` — a **serialized per-file read-modify-write promise chain** so concurrent commands and sweeps can't clobber each other. `update` never rejects; it logs and returns `{ ok:false }`.
- **Periodic export:** every `DB_EXPORT_INTERVAL_MS` (default 10 min, `0` disables), dump every dataset back to pretty-printed `.json` via atomic temp-file + rename, as a human-readable backup.
- **`ensureFile(path, default)`** creates missing files; **`matchTreeOwner`** chowns files written into the Steam tree back to the owning uid/gid when the bot runs as root.
- All runtime `*.json`, `bot.db*`, and `bot.lock` are gitignored.

---

## 5. Theme & Output (`discord/theme.js`, `discord/textify.js`)

- Colour palette object `NV` (AMBER, GOLD, IRRAD_GREEN, NCR_TAN, LEGION_RED, RUST_RED, DEAD_GREY, BLUE_VATS, PURPLE, PINK, TEAL, CYAN, ORANGE) and a ban-specific `CLIN` set. Every `setColor()` routes through these.
- `GLYPH` = `{ ok:✅ bad:❌ warn:⚠️ deny:🚫 info:ℹ️ dot:• up:🟢 down:🔴 caps:💰 rank:🏅 }`.
- Shared builders: `successEmbed`, `errorEmbed`, `warningEmbed`, `deniedEmbed`, plus gate cards `adminOnlyEmbed`, `ownerOnlyEmbed`, `modOnlyEmbed`, `factionLeaderOnlyEmbed`, `policeOnlyEmbed`, `blacklistedEmbed`, `emptyIdEmbed`, `rateLimitEmbed`.
- `brand(embed)` = minimal stamp; strips any timestamp and **clamps to Discord hard limits** (title 256, description 4096, field name 256, value 1024) so one long field can never reject the whole message.
- `bar(value, max, width)` — smooth ⅛-cell progress bar, must be **monotonic** (a higher value never renders shorter).
- **Tone: strictly professional.** No flavour quotes, no RP aphorisms anywhere in user-facing output.
- `patchInteractionOutput(interaction)` renders embeds to plain text at interaction send-time (`channel.send` keeps real embeds).
- Punctuation house style: plain hyphens `-`, never em-dashes; `•` for bullets.

---

## 6. Permission Model

**Tiers (`moderation/hierarchy.js`, pure):** `Super Owner (4) > Owner (3) > Admin (2) > Mod (1) > none (0)`.
**Override rule:** a lower tier can **never** lift/undo a moderation action issued by a strictly higher tier. Equal tiers may override each other. Applies to unban, unmute, and clearing another staffer's ban.

**Roles** are stored in the `ROLES` dataset and set via `/setroles`: `modRoleId, adminRoleId, factionLeaderRoleId, policeRoleId, gambinoRoleId, colomboRoleId, nypdRoleId`. Owner/super-owner IDs come from `OWNER_IDS` / `SUPER_OWNER_IDS` env (with hardcoded fallbacks).

Predicates: `hasModRole`, `hasAdminRole`, `hasFactionLeaderRole`, `hasPoliceRole`, `hasWhitelistManageRole(member, faction)` (per-faction: the general leader role manages all; Gambino/Colombo roles manage only their own), `isOwner`, `isSuperOwner`, `isBlacklisted`, `isMasterName`, `isProtectedPlayer`.

**Dispatcher gating (`commands/index.js`)** — in order:
1. **Blacklist gate** — users in `BLACKLIST_IDS`/`USER_BLACKLIST` get nothing (owners immune).
2. Component/modal safety net: any unacked message component gets `deferUpdate()` after 2.5s so an expired collector never leaves "thinking…".
3. **Guild-only** — DMs answered cleanly (no member object ⇒ role checks meaningless).
4. Tier arrays: `PUBLIC = [help, serverinfo, checkban]`, `MOD_COMMANDS`, `FL_COMMANDS`, `ADMIN_COMMANDS`. Commands not in an array **must** self-gate in-handler.
5. **Rate limit** 4s per user per command (owners exempt), backed by a self-pruning Map.
6. Handler dispatch inside try/catch that replies with an error embed — **a command must never crash the bot**.

---

## 7. Commands (34 top-level)

**Public:** `/help` (access-aware command list), `/serverinfo`, `/checkban`.

**Moderation (mod):** `/kick`, `/flush` (randomly kick one online player), `/tempban`, `/unban`, `/announce`, `/givecaps`.

**Admin:** `/permban`, `/cleartempbans`, `/setroles`, `/setrconroles`, `/givemenu`, `/stripmenu`, `/manual` (raw RCON), `/adjustcaps`, `/donator add|remove|list`, `/staffactivity`, `/staffleaderboard`.

**Owner:** `/configure` (interactive menu), `/health`, `/inspect`, `/firewall block|unblock|status`, `/clearallbans`, `/stripmenuall`.

**Factions:** `/whitelist add|remove|list|playtime|wipe`, `/promotion`, `/demotion`, `/subclass`.

**Police:** `/warrant give|remove|check`, `/arrest`, `/backgroundcheck`, `/suspendrank`, `/bail increase|decrease|reset|show`.

### `/tempban` — punishment presets
A choice list sets the duration automatically:
`MassRDM in Protected Zone` 3d · `Spawn Killing – Whitelist Spawn` 5d · `Spawn Killing – Civ Spawn` 7d · `Hard R` **permanent** · `Soft A` 3d · `Slur` 1d · `Exploiting` 2d · `Harassment` 1d · `ERP` 1d · `Sexually Explicit` 7d · `Donator Abuse` 7d (+14d donator-perk suspension) · `Other` (custom date, autocompleted, 12pm Eastern).
On ban: DM the player a punishment notice (report DM success/failure in the embed), write the mod log, enforce over RCON, and flag IP/EOS.

### `/configure` — owner menu (select + confirm loop)
Options: view/clear IP blacklist, clear flags, clear flagged names, clear a specific IP, blacklist an IP, view alts, ignore-list add/remove/list, bar/unbar Discord users + list, save/load faction snapshots, **wipe all money**, **wipe all player data**, firewall status. Destructive actions require a confirm button.

### `/inspect <player>` — owner
Full dossier: every known EOS id, all/confirmed IPs, alts, first/last seen, login count, recent connections, K/D, playtime, factions/ranks, ban status, VPN verdict + provider, linked Discord account, and geolocation.

---

## 8. `ipBans.js` — Log Tailer, Registry & Anti-Evasion

**Standalone module** (only `fs`/`path` + the shared SQLite kv). Tails every `Pavlov.log` (auto-discovered per install, or `PAVLOV_LOGS`), parsing:

- **Accept lines** → remember the pending IP; track *distinct* pending IPs so an unambiguous join (exactly 1) is distinguishable from concurrent joins.
- **Login lines** → name + EOS id; correlate with the pending IP (`confident` only when unambiguous).
- **Disconnect/close lines** → IP **and** EOS id on the *same line* = a **confirmed pairing** (100% reliable).
- **Kill records** → K/D tallies and a per-killer victim log.

**Registry:** `id → { name, ips[], cips[], firstSeen, lastSeen, logins, recent[] }` where `cips` = confirmed IPs.
**Flag sets (all persisted):** `flagged` (IPs), `flaggedNames`, `flaggedIds` (EOS), `manualIps` (admin flags, never cleared by a player unban), `untrackedNames`/`untrackedIds` (ignore list), `pendingFlag`, `cutoffTs`.

**Alts:** another EOS id that shares a **confirmed** IP. Surfaced as `getRecord().alts`.

**Callbacks:** `onConnect`, `onConfirm`, `onKill`, `onAutoBan` — each debounced (join/confirm/auto) with self-pruning maps.

**Auto-ban on join** fires for: flagged EOS id · flagged username · a confirmed IP on record that is flagged · the current join IP **only when the correlation is confident**. Never a mis-correlated join IP — that would ban an innocent.

### Hard-won invariants (must reproduce exactly)
1. **Only confirmed IPs are ever flagged.** Join-time IPs are a timing guess; flagging them auto-bans strangers.
2. **EOS ids are flagged on permanent bans only.** A temp ban flags IPs but must never brand an account forever.
3. **`pendingFlag` must be persisted.** Banning a player who is still *online* leaves nothing to flag; their IP is flagged when their disconnect confirms it (5-min window). If this map is memory-only, a restart inside that window silently drops it and the ban is evadable. Clear it on unban and once satisfied.
4. A **cutoff timestamp** makes a post-wipe restart ignore old log lines so backfill can't rebuild data you just cleared.

---

## 9. Ban System (`moderation/bans.js`)

- **`banWithIp(name, server, { permanent, ip })`** — refuse master names; clear any unban exemption; `hardEnforce`; schedule a 30s recheck (blacklist.txt backup + re-enforce); flag IP/EOS via `ipBans.blacklistPlayer(name, { flagId: permanent })`.
- **`hardEnforce(name, { banToo, kick, uniqueId })`** — issue `Ban` + `Kick` on every server, logging each server's raw response. **Prefer `uniqueId`**; log loudly when the sanitizer rewrites the target (that mismatch is why a ban silently no-ops).
- **`enforceBansSweep()`** (30s) — `RefreshList` is authoritative. Force-remove anyone online who matches an active ban record or a flagged IP/EOS. Guarded by a busy flag; **dedupe per sweep** (one enforcement already hits every server, so per-server enforcement is O(N²) RCON connections). Never creates a ban record.
- **`reconcileBans()`** — re-apply every active ban to each server; rate-limited to once per 30 min so bot restarts don't flood RCON; re-verify each ban at issue time so an in-flight `/unban` isn't undone; 400ms pacing.
- **`processExpiredBans()`** (60s) — lift expired temp bans, add an autoban exemption ("sentence served"), and delete only entries **still expired at write time** (so a re-issued ban isn't clobbered). **Never** auto-lift permanent bans.
- **`unbanEverywhere()`** — remove from blacklist.txt (all installs), clear IP/EOS/name/pending flags, issue native `Unban`, and report per-server failures.
- **`autoBanDecision(existing, reason)`** → `"block"` (unexpired temp ban already covers them), `"lift"` (served temp ban whose stale flag re-caught them — never escalate to permanent), or `"ban"`.
- **`isRealBan` / `sourceBanFor`** — resolve the *original* offence (by EOS id, then name/alt) so an evasion ban shows the real punishment instead of "IP-Guard".

**Ban records must be validated on read.** A record missing `playerId` makes `String(undefined)` match a player named "undefined"; a non-finite `expires` makes a ban look active forever. Validate, **report** corrupt rows (throttled), and **do not delete them** — leave them on disk for manual repair.

**Structured audit logging** — every ban/unban emits one line:
```
permanent ban | player="X" | target="X_EOS" | eos=... | ip=... | server=both |
flagged: ips=1 eosIds=1 | alts=... | rcon=2/2 accepted | result=ENFORCED | at=<ISO>
```
Plus diagnostics for *why* enforcement failed: no usable target, rewritten target, 0/N accepted, flag failure, lookup failure.

---

## 10. VPN / Proxy Detection (`moderation/vpn.js`)

Layered to conserve free API quotas; **cached per-IP permanently** (an IP is checked once, ever) with a shared in-flight promise so concurrent joins don't double-call.

1. **ipinfo.io** — geolocation for every IP, keyless (optional `IPINFO_TOKEN`), never touches VPN quotas.
2. **IPHub** (`IPHUB_API_KEY`) — baseline; `block === 1` ⇒ flagged.
3. **Only on a flagged IP**, in parallel: **IPQS** (cross-check) and **proxycheck.io** (`provider`/`operator.name` ⇒ the actual VPN brand, e.g. "NordVPN"; keyless 100/day, free key 1000/day).

**Verdict:** IPHub + IPQS agree ⇒ auto-ban (RCON Ban+Kick, IP/EOS flagged). IPHub flags but IPQS disputes ⇒ **log only** (likely false positive). IPQS unconfigured ⇒ IPHub flag alone bans. Master names and unban-exempt players are never auto-actioned. An action TTL (7d) re-arms so the next player from that IP can still be actioned.

Display the **VPN provider name** in the connection feed, `/inspect`, and auto-ban logs.

---

## 11. Factions / Whitelists

Three **mutually exclusive** factions, each with its own spawn file in the game's `FactionRoles` dir:

| Faction | Spawn file | Ranks (low→high) | Per-rank caps |
|---|---|---|---|
| **Gambino** | `gambinospawn.txt` | Associate, Soldier, Capo, Consigliere, Underboss, Boss | 18 / 12 / 3 / — / 1 / 1 |
| **Colombo** | `colombospawn.txt` | *(same)* | *(same)* |
| **NYPD** | `policespawn.txt` | Cadet, Patrolman, Corporal, Sergeant, Lieutenant, Captain, Deputy Chief, Chief of Police | 50 / 20 / 15 / 20 / 8 / 4 / 1 / 1 |

Every rank has its own `.txt` rank file (e.g. `gambinocapo.txt`, `policecadet.txt`). **NYPD sub-classes** (not ranks, each its own whitelist file): **Vice Officer** (`policevice.txt`), **Detective** (`policedetective.txt`), **Tactical Response Unit** (`policetacticalresponse.txt`).

**Membership rules (enforce all):**
- **One faction per player** — `/whitelist add` blocks anyone already in another faction.
- **One rank per player** — promotion/demotion clears *every* rank file before writing the new one.
- **At most one sub-class**, held *in addition to* a rank. Assigning a second is refused until the first is removed.
- Per-rank caps enforced on `/whitelist add` and on promotion/demotion (refuse before touching any file). A rank absent from the cap table is uncapped.
- `/whitelist list` shows `n/cap` per rank and is computed from the roster already loaded (never re-read rank files per rank).

**File-write safety (`factions/files.js`):** refuse non-array payloads; refuse a write that would **delete more than 5 existing entries** (corruption guard); take a rolling pre-write `.bak` snapshot; mirror every write to all installs. On startup, create missing faction files everywhere and prune a known list of obsolete legacy files.

---

## 12. Police Roleplay

**Penal code (`penal/codes.js`, pure data)** — 59 charges across 7 sections, keyed by hundreds series: `100` Public Order · `200` Crimes Against Persons · `300` Property Crimes · `400` Weapons Offenses · `500` Crimes Against Government · `600` Vehicle Code (`VC` prefix) · `700` Organized Crime. Each charge: `{ code:"PC 200", name, cls:"Infraction|Misdemeanor|Felony", min (jail minutes), bail (dollars), special }`.

Special cases: **`PC 210 Homicide`** → `special:"execution"` (no jail timer, no bail, label "Execution" dominates). **`PC 707 Aiding and Abetting`** → `special:"variable"` (jail and bail "based on the associated charge", class "Misdemeanor / Felony"). Infractions (e.g. `VC 600 Speeding`) carry bail but **no jail**.

- **`/arrest <player>`** — interactive booking: select a section → multi-select charges (dedupe by code) → running **jail + bail** total → Confirm/Cancel. On confirm: record the arrest, start a sentence timer, log it, and **ping the arresting officer when the sentence ends**. Bail is **stamped at the rate in effect at arrest time** so history stays accurate.
- **`/bail increase|decrease <percent>`** — scales every charge's bail, **compounding** off current prices, **rounded to the nearest dollar**; `reset` returns to base, `show` displays the multiplier with examples. Stored in `POLICE_CONFIG`.
- **`/backgroundcheck`** — warrants, arrest history (charges + sentence + bail), total jail time served.
- **`/warrant give|remove|check`** — warrants **stack**; `give` requires a reason; `remove` takes an optional 1-based number (omit = clear all); `check` lists one player's numbered stack or everyone with warrants.
- **`/suspendrank <player> <time>`** — pull a whitelist rank for a duration; auto-restores via a 30s sweep.

All police output goes to a dedicated police log channel.

---

## 13. Other Subsystems

- **Economy** — per-player balance files (`<name>.txt`, a bare integer) under `MODSAVE_PATH`. `mutateBalance` serializes per player so concurrent writes can't double-spend. **Refuse to write a non-finite amount** (writing "NaN" corrupts the ledger). Filenames strip path separators/control chars but **preserve spaces**. Currency is displayed as **dollars with a `$` prefix** everywhere. `preserveBalanceAcrossKick` snapshots and restores a balance the game wipes on force-kick. Cross-install newest-wins sync with a guard that never overwrites a positive balance with 0/empty.
- **Verification** — a public channel with a Verify button → modal for the Pavlov name → request posted to a private staff channel with Accept/Deny (mod-gated). On accept: grant the Verified role, remove Unverified, store the link, and log Discord↔IP to the webhook. **One person per name; no alts** (conflict detection via confirmed IPs). An auto-created **Unverified** role is denied view on previously-public channels only (bot-lock signature: `@everyone` deny + Verified allow) — channels are never made private.
- **Leaderboards** — self-refreshing single messages edited in place every 30s: richest players, **arrests/jail-time "Most Wanted"**, live player list (with staff tags and today's/all-time peak), and a live server dashboard (monospace HUD, per-server health, player bars, gateway ping). One shared `autopost(key, channelId, getEmbed)` helper. On startup, purge stray messages but **keep the tracked board messages** so a restart edits in place instead of reposting.
- **RCON menu panel** — a channel where staff enter their in-game name and receive the menu matching their highest Discord role (`/setrconroles` maps role→menu). Grants are recorded and **re-applied on every join** (the server drops menus on disconnect). High Staff additionally runs `AddMod` + `AddAccessManager`.
- **Kill feed** — plain-text (no embed, no emoji) `killer → killed` posts to `KILLFEED_CHANNEL`.
- **Mod log** — every action appended to `MODLOG`; `/staffactivity` and `/staffleaderboard` read it (bans/kicks/mutes only, automated actions excluded).
- **Playtime & peaks** — sampled every 60s while online; all-time and daily-reset peak tracking (Eastern time).
- **Update log** — posts a changelog when `BUILD_ID` changes, with private info redacted (IPs, tokens, snowflakes, home paths).

---

## 14. Reliability, Startup & Testing

**Startup order:** validate config (**fail fast** — `process.exit(1)` on missing `DISCORD_TOKEN`/`CLIENT_ID`/`RCON_HOST_1`/`RCON_PORT_1`/`RCON_PASSWORD_1`) → acquire a **single-instance PID lock** (`bot.lock`, stale-lock aware; two instances fight over the boards) → open SQLite/migrate → wire modules → `startIntervals()` → `client.login()`.

**Intervals must live inside `startIntervals()`** (called only when run directly), so `require("./index.js")` from a test never starts timers: expired bans 60s · donator restores 60s · ban sweep 30s · sentence + rank-suspension sweeps 30s · leaderboards/player list/dashboard 30s · RCON health · player cache + playtime 60s · reconcile 5 min · ModSave sync · faction backup 24h.

**Shutdown:** `SIGINT`/`SIGTERM` → drain all pending write queues (`Promise.allSettled`), flush ipBans, export JSON backups, release the lock, exit 0.

**Error handling:** `unhandledRejection` and `uncaughtException` are logged (with counters feeding `/health`), never fatal. Add `client.on("error")`/`shardError` listeners. `client.login()` must have a `.catch`.

**Logging:** levelled (`DEBUG/INFO/WARN/ERROR` via `LOG_LEVEL`), tagged (`[Bans]`, `[RCON]`, `[IPGuard]`…), with a rolling ring buffer of the last 25 warn/error entries for `/health`.

**Tests (target 120+, `node --test`):** pure-logic units (hierarchy, penal, peaks, verify rules, utils, textify, theme); runtime flow tests that drive real handlers against stubbed ctx (whitelist, ranks, warrants, police, bail, ban enforcement, arrest board); a database test asserting the dataset count and clone semantics; an ipBans test covering alts, EOS flagging on permanent bans, and that a temp ban does **not** flag the EOS id.

**Mandatory static wiring test:** parse `index.js` and every module to prove that every name a module destructures from `ctx` (and every `...spread` and bare reference to an `index.js` binding) is actually provided by the ctx literal that feeds it. This catches "X is not defined" at test time instead of at runtime.

---

## 15. Configuration (`.env`)

**Required:** `DISCORD_TOKEN`, `CLIENT_ID`, `RCON_HOST_1`, `RCON_PORT_1`, `RCON_PASSWORD_1`.

**Optional:** `RCON_{HOST,PORT,PASSWORD}_{2,3}` · `BOT_NAME` · `MODSAVE_PATH`, `MODSAVE_SYNC`, `MODSAVE_SYNC_SKIP_EXTRA`, `MODSAVE_BLACKLIST_PATH`, `DONATOR_PATH` · `PAVLOV_BASE_1`, `PAVLOV_BASES`, `PAVLOV_LOGS` · channels: `MOD_LOG_CHANNEL` (staff log), `BAN_LOG_CHANNEL` (staff punishment log), `POLICE_LOG_CHANNEL`, `ARREST_CHANNEL`, `LEADERBOARD_CHANNEL`, `ARREST_LEADERBOARD_CHANNEL`, `PLAYERLIST_CHANNEL`, `DASHBOARD_CHANNEL`, `KILLFEED_CHANNEL`, `MENU_PANEL_CHANNEL`, `UPDATE_LOG_CHANNEL`, `VERIFY_CHANNEL`, `VERIFY_STAFF_CHANNEL`, `CONNECT_WEBHOOK_URL` · roles: `VERIFIED_ROLE`, `MENU_ROLE_{STAFF,HIGHSTAFF,BLACKLIST}` · detection: `IPHUB_API_KEY`, `IPQS_API_KEY`, `PROXYCHECK_API_KEY`, `IPINFO_TOKEN` · `UFW_BLOCK` · access: `OWNER_IDS`, `SUPER_OWNER_IDS`, `BLACKLIST_IDS`, `APPEAL_LINK` · `BUILD_ID`, `LOG_LEVEL`, `DB_EXPORT_INTERVAL_MS`.

Ship a fully commented `.env.example`.

---

## 16. Security Requirements

- **Sanitize all player input.** `sanitizeId` strips to `[A-Za-z0-9_\-.]` and caps at 64 (prevents RCON command injection); `sanitizeMessage` flattens control chars and caps at 200. Ledger filenames strip path separators but keep spaces.
- **No path traversal** — every game-file path is built from a sanitized basename joined to a configured root.
- **No shell interpolation of user input** — `ufw` calls use fixed argv via `spawn`.
- **Redact private data** on public surfaces (IPv4/IPv6, 24+ hex tokens, Discord snowflakes, `/home/<user>` and `/root` paths).
- IP-revealing feeds go to a **webhook/private channel** only.
- Never log or echo API keys — strip them from error messages before logging.
