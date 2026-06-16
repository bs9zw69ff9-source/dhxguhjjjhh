# Mojave Authority Bot

![CI](https://github.com/bs9zw69ff9-source/bot/actions/workflows/ci.yml/badge.svg)

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

CI (GitHub Actions) runs the syntax check and the test suite on every push —
see [`.github/workflows/ci.yml`](.github/workflows/ci.yml).

## Deployment

### Docker

```bash
docker build -t mojave-authority-bot .
docker run --env-file .env -v "$PWD/data:/app/data" mojave-authority-bot
```

The bot persists runtime state as JSON files in its working directory, so
mount a volume to keep that data across restarts. If it also manages faction
spawn/rank files or the donator file on the game-server host, mount those
paths too and point `DONATOR_PATH` (and the faction paths in `index.js`) at
the mounted location.

### Bare metal

```bash
npm install --omit=dev
npm start
```

Use a process manager (systemd, pm2) to keep it running and restart on
failure.

## License & ownership

This is **proprietary** software — see [`LICENSE`](LICENSE) and
[`NOTICE`](NOTICE). No permission is granted to copy, modify, distribute, or
host it without written authorization. Authorship is evidenced by the git
commit history, the copyright headers, and the in-app attribution shown in
`/help` and `/ping`.

> If you'd rather *allow* others to run it but force them to publish any
> modified source, switch the license to **AGPL-3.0** instead. Tell the
> maintainer and it can be swapped in.

### Protecting the project from theft (legitimate measures)

These are the approaches that actually help — none of them involve hidden
malicious code:

- **Don't ship secrets.** Tokens and RCON passwords live in `.env`
  (git-ignored), so a stolen `index.js` is useless without the operator's own
  configuration.
- **Keep the git history.** Commit timestamps are strong proof of authorship
  for DMCA takedowns or disputes.
- **Visible attribution / build stamp.** `/help` and `/ping` display the
  copyright and a `BUILD_ID` (override per deployment with the `BUILD_ID` env
  var) so unauthorized copies are identifiable.
- **Canary tokens (optional).** Drop a tracking token from
  <https://canarytokens.org> into a comment or config file; you'll be alerted
  if a leaked copy is opened somewhere unexpected. This only notifies *you* —
  it does not tamper with anyone's system.
- **Takedowns.** For unauthorized public copies, file a DMCA notice with the
  hosting provider (GitHub, etc.) citing your commit history.

> Note: hidden "backdoors" (covert remote access, kill switches, data
> exfiltration) are **not** an anti-theft measure — they are illegal in most
> jurisdictions, destroy trust, and become the very vulnerability attackers
> exploit. The measures above are the safe, effective alternatives.

