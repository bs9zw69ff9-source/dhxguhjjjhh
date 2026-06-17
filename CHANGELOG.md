# Changelog

All notable changes to this project are documented here.

## [Unreleased]

### Changed
- **Commands consolidated into topic groups** (`/bans`, `/moderation`,
  `/economy`, `/server`, `/config`). Running a group shows a dropdown of its
  actions; picking one opens a modal to collect inputs. Driven by a single
  action registry (one source of truth for menus, modals, and permissions);
  existing handler logic is reused via an option-accessor bridge.
  `/faction`, `/setroles`, and `/blacklist` stay native (they need
  role/user/choice pickers a text modal can't provide).

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
