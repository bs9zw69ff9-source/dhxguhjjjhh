# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Changed
- **All command lists now use interactive ◀ ▶ pagination.** Added buttons to
  `/warnings`, `/note list`, `/donator list`, and converted `/faction list`
  and `/faction audit` from a `page` option to buttons (option removed).
  (`/history`, `/banlist`, `/wagelist`, `/hardbanlist` already had them.)

### Fixed (debug pass)
- **Temp-ban writes are now serialized.** `tempban`, `unban`, `permban`,
  `hardban`, `cleartempbans`, the 60s expiry sweep, and warn auto-escalation
  all go through `update()` (`upsertTempBan`/`removeBans`) instead of raw
  `saveBans()`, eliminating lost-update races between commands and the timer.
  The expiry sweep and mass-clear now remove only the IDs they actually
  lifted, preserving bans added concurrently.
- **`/clearwarnings`** now uses the serialized `update()` like `issueWarn`/
  `delwarn` (they previously mixed serialized + unserialized writes on the
  same file).
- **`/transfercaps`** rolls back the sender's debit if the recipient credit
  fails, so caps can no longer vanish mid-transfer.
- Removed remaining case-sensitive ban-list comparisons.
- Removed dead code (`saveBans`, `saveWarns`).

### Removed
- The companion web app (`web/`) was removed.
- **`/blacklist` command** — the command-blacklist is now configured via the
  `BLACKLIST_IDS` env var (comma/space/newline-separated user IDs; restart to
  apply) instead of a slash command and `blacklist.json`. Owners stay exempt.

### Added
- **Playtime leaderboard** — auto-posts the top 30 most-active couriers (by
  tracked playtime) every 6h to a dedicated channel (default
  `1517198961918611566`, override with `PLAYTIME_LB_CHANNEL`); edits its own
  message in place like the caps leaderboard.
- **Context-aware autocomplete** — player suggestions now match the command:
  `/unban` lists only banned players, `/stripmenu` only menu-grant holders,
  `/removewage` only payroll, `/donator remove` only donators, `/faction
  remove|rank|transfer` only that faction's members, `/delwarn`/`/clearwarnings`/
  `/warnings` only the warned, `/addnote` only hard-banned, `/history` only
  players with history. (Faction subcommands reordered so you pick the faction
  first.) Add/grant/lookup commands keep the full online + known-player list.
- **Known-player registry** (`known_players.json`) — records everyone who's
  ever been seen online (display name + first/last seen). Autocomplete now
  falls back to it, so offline players can still be picked: e.g. typing
  `ncr_` surfaces `ncr_private (offline)` even when they're not in-game.
  Seeded on startup from existing data (playtime, faction files, wages,
  donators, bans) so it's useful immediately.
- **Staff applications** — `/acceptstaffapp <user>` DMs the applicant a
  welcome and grants the two staff roles; `/denystaffapp <user> [reason]`
  DMs a denial and does nothing else. (admin)
- **New faction ranks** — Legion: Prime Legionary, Centurion, Praetorian;
  Khans: Mid Rank.
- **Punishment DMs** — `/kick`, `/warn`, `/tempban`, `/permban`, and
  `/hardban` accept an optional `discord_user`. When provided, the bot DMs
  that account a branded breakdown of their punishment (action, reason,
  duration/expiry, server), and the moderator's reply shows whether the DM
  was delivered or the user's DMs were closed.

### Changed
- **UI overhaul** — unified visual identity across embeds: a branded author
  header with the bot's avatar, consistent timestamps/footers, avatar
  thumbnails on showcase embeds, refined section rules, status pips, and
  unicode meter bars. Applied to `/help`, `/ping`, `/stats`, `/serverinfo`,
  `/find`, the leaderboard, the player list, every paginated list, and all
  moderation-action confirmations.

### Added
- **Per-rank caps** within each faction: `/faction setrankcap <faction> <rank> <cap>`
  (0 = unlimited). Enforced on `/faction add`, `/faction rank`, and
  `/faction transfer`; shown in `/faction list` and `/faction overview`.
- **`/donator add|remove|list`** — manage a donator whitelist text file
  (location via `DONATOR_PATH`).
- **`/note add|list|clear`** — freeform staff notes on any player.
- **`/seen`** — last-online tracking, also shown in `/stats`.
- **`/delwarn`** — remove a single warning by number.
- **`/blacklist add|remove|list`** — bar a Discord user from all commands.
- **Hardcoded owner override** (`OWNER_IDS`): full access, rate-limit-exempt,
  cannot be blacklisted.
- **Interactive pagination** (◀ ▶) for `/history`, `/banlist`, `/wagelist`,
  `/hardbanlist`.
- Autocomplete on `/unban`, `/checkban`, `/addnote`.
- Bot presence ("Watching over the Mojave · /help").
- Project scaffolding: `package.json`, `.gitignore`, `.env.example`,
  `README.md`, `SECURITY.md`, `NOTICE`, and a self-contained test suite
  (`npm test`).
- Proprietary license and in-app authorship/build attribution.

### Fixed
- Fatal `SyntaxError` from stray dead code after `writeFactionAudit()` that
  prevented the file from parsing at all.
- `sendRconRaw` leaked a timer and could settle more than once per call.
- `permban`/`hardban` cleared temp bans case-sensitively, inconsistent with
  the rest of the codebase.
- `validateConfig()` now runs before any side effects (fail fast).

### Changed
- De-duplicated player-name parsing into shared helpers.
- Removed unused imports and dead code.

## [3.2.2]
- Baseline version prior to this work.
