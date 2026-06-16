# Mojave Authority Bot

A Fallout: New Vegas–themed Discord moderation bot for **Pavlov VR** servers.
It drives the game servers over RCON and adds bans, warnings with
auto-escalation, faction whitelists/ranks, a caps economy with weekly wages
and a leaderboard, donator management, staff notes, and an owner/blacklist
access layer — all behind themed slash commands.

## Requirements

- Node.js **18+**
- One or two Pavlov servers with RCON enabled
- A Discord application + bot token

## Setup

```bash
npm install
cp .env.example .env      # then fill in the values
npm start
```

Slash commands are registered automatically on startup.

## Configuration

All configuration is via environment variables — see [`.env.example`](.env.example)
for the full annotated list. Required: `DISCORD_TOKEN`, `CLIENT_ID`, and the
`RCON_*_1` trio. Server 2, ModSave paths, log/leaderboard channels, and the
donator file path are optional.

### Owner override

One or more Discord user IDs are hardcoded in `OWNER_IDS` at the top of
`index.js`. Owners pass every permission check, skip rate limits, and can
never be blacklisted. This list is intentionally code-only — edit the source
to change it.

## Commands

| Tier | Commands |
|------|----------|
| 🌐 Public | `/help` `/ping` `/listplayers` `/serverinfo` `/find` `/checkban` `/banlist` `/stats` `/checkbalance` `/wagelist` `/warnings` `/seen` · `/faction list\|overview\|audit` |
| 🛡️ Moderator | `/kick` `/warn` `/delwarn` `/tempban` `/unban` `/announce` `/history` `/note add\|list` `/givecaps` `/faction transfer` |
| ⚔️ Faction Leader | `/faction add\|remove\|rank` `/addwage` `/removewage` |
| 🔒 Admin | `/permban` `/hardban` `/addnote` `/hardbanlist` `/clearwarnings` `/note clear` `/cleartempbans` `/setroles` `/givemenu` `/stripmenu` `/transfercaps` `/adjustcaps` `/rotatemap` `/manual` `/blacklist` `/donator` `/faction setcap` |

Roles are mapped to tiers with `/setroles`. If a tier's role is unset, that
tier is unrestricted.

### Automation

- Temp bans auto-lifted every **60s**
- Caps leaderboard re-posted every **6h**
- Weekly wages disbursed every **7 days**
- RCON health check every **5 min**
- Warn thresholds: **3** → 1-day ban · **5** → 1-week ban · **7** → permaban

## Data

Runtime state is stored as JSON files in the working directory (e.g.
`tempbans.json`, `warnings.json`, `faction_ranks.json`, `donator.txt`, …).
They are created automatically and are **git-ignored** — they are state, not
source. Writes are atomic (temp file + rename) and serialized per file.

## Development

```bash
npm run check   # syntax check (node --check)
npm test        # unit tests for the pure logic (no Discord/network needed)
```

The test runner sandboxes the bot with stub modules so it can require and
exercise the exported helpers without a token or `discord.js` installed.
