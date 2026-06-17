# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Added (companion web app)
- A zero-dependency, **public** web dashboard (`web/`, `npm run web`) that
  reads the bot's data + live RCON: **Servers**, **Leaderboard**,
  **Factions**, and **Courier Lookup** pages, with a Pip-Boy/New-Vegas CRT UI.

### Removed
- **`/blacklist` command** — the command-blacklist is now configured via the
  `BLACKLIST_IDS` env var (comma/space/newline-separated user IDs; restart to
  apply) instead of a slash command and `blacklist.json`. Owners stay exempt.

### Added
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
