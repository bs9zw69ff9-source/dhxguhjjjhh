# Running a themed second bot

One binary, two bots: the normal RP and a themed clone with its own factions, side by side
on the same box and against the **same Pavlov server**.

Not a fork. This repo took thirty pull requests of fixes in a single evening; a copied
codebase means every one of those lands twice, which in practice means it lands once and the
copy quietly rots. The clone is the same build with a different `.env`.

---

## What actually differs

Only the **faction roster**. Everything else — bans, RCON, economy, the intelligence
commands, the police commands — is the same code behaving the same way.

`FACTIONS_PATH` points at a JSON file that **replaces** the built-in set. Unset, you get
Gambino / Colombo / NYPD exactly as before, which is why the normal bots need no change at
all.

```bash
cp factions.fallout.example.json /root/pavlov-bot-fallout/factions.json
```

Ranks and caps live in that file. Editing it needs a restart, not a deploy.

---

## Sharing one game server: what must not collide

This is the part that costs you data if it is wrong. Two bots against one server share the
game's files, and several subsystems **rewrite a whole file from their own database**.

### The ban list — only ONE bot may own it

`ModsaveBanlist.ExportAsync` rewrites the entire blacklist from that bot's ban store. Two
bots doing that means each one erases every ban the other made, every five minutes, forever.

**Leave `MODSAVE_BLACKLIST_PATH` blank on the clone.** The clone can still ban over RCON;
it just does not manage the file.

### Payroll — only ONE bot may pay

Both bots would see the same players on the same server and pay them separately. Wages are
real money in your economy.

**Leave `PAYROLL_AMOUNT` unset on the clone.**

### Player balances — prefer ONE bot

Ledger writes are serialised *within* a process, not across two. Two bots writing the same
`modsave/<player>.txt` can lose an update.

**Leave `MODSAVE_PATH` blank on the clone** unless you specifically want its economy
commands, and accept the risk if you set it.

### Or name them once with `IGNORE_PATHS`

Blanking each setting is the primary control. `IGNORE_PATHS` is the backstop, and it exists
because the clone's `.env` is a copy of the original — so every shared path is already in it,
correct, and pointing at files it must not touch.

```bash
IGNORE_PATHS=/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/blacklist.txt,/home/steam/pavlovserver/Pavlov/Saved/Config/whitelist.txt
```

Any **write** at or under a listed path is refused and logged once. Reads are unaffected, so
the clone can still read the rosters to enforce one faction per player. Matching is
separator-bounded: `/home/steam/pavlovserver` does not cover `pavlovserver-backup`.

### Name FILES here, not the ModSave tree

`FactionRoles` lives **inside** `ModSave`, so ignoring the whole tree to protect the ban list
and the ledgers takes the clone's own rosters with it — and the failure is silent in the worst
way: the bot starts, reports `whitelists: <path>` as though the feature were on, and then
refuses every write. `/whitelist add` answers "the roster could not be written" forever.

The bot now **refuses to start** on that combination rather than letting you find it later:

```
FACTION_ROLES_PATH (...) is inside IGNORE_PATHS, so this bot could never write a roster
```

The ledgers are the one thing `IGNORE_PATHS` cannot express neatly, because they sit directly
in `ModSave` alongside `FactionRoles`. Leave `MODSAVE_PATH` blank on the clone instead — which
is the primary control anyway.

The startup summary lists what is ignored, so a clone that is not protected says so.

### Its own database

`DATA_DIR` **must** differ. Two processes on one `bot.db` is the one mistake that corrupts
state rather than just duplicating work.

### Safe to share

- **`FACTION_ROLES_PATH`** — same directory is fine, because the file names differ. The
  shipped template is prefixed `ncr*`, `legion*`, `bos*`, `enclave*`, `kings*`, `followers*`
  precisely so nothing can touch `gambinospawn.txt` or the `police*.txt` files. Keep the
  prefixes. The bot refuses to start if two factions inside one file collide, but it cannot
  see the other bot's roster.
- **`PAVLOV_LOGS`** — both tail the log read-only. Each posts its own join/leave feed to its
  own channels, which is what you want.
- **RCON** — two connections to one server. More load, no conflict.

---

## Setting it up

```bash
mkdir -p /root/pavlov-bot-fallout
cd /root/pavlov-bot
cp .env /root/pavlov-bot-fallout/.env
cp factions.fallout.example.json /root/pavlov-bot-fallout/factions.json
```

Then edit `/root/pavlov-bot-fallout/.env`:

```bash
# ── its own identity ──
DISCORD_TOKEN=            # a SECOND Discord application, not the same token
CLIENT_ID=
GUILD_ID=                 # the Fallout server's id
BOT_NAME=Mojave Authority

# ── its own factions and state ──
FACTIONS_PATH=/root/pavlov-bot-fallout/factions.json
DATA_DIR=/root/pavlov-bot-fallout/data

# ── the shared-file subsystems stay with the normal bot ──
MODSAVE_BLACKLIST_PATH=
PAYROLL_AMOUNT=
MODSAVE_PATH=

# Backstop: refuse writes to the files the normal bot owns, whatever else is set.
# FILES, not the ModSave tree - FactionRoles lives inside it and this bot needs to
# write its own rosters there.
IGNORE_PATHS=/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/blacklist.txt,/home/steam/pavlovserver/Pavlov/Saved/Config/whitelist.txt

# ── its own channels ──
MOD_LOG_CHANNEL=
BAN_LOG_CHANNEL=
PLAYER_COUNT_CHANNELS=
SHACK_TOTAL_CHANNEL=
JOIN_WEBHOOK_URL=
KILL_WEBHOOK_URL=
CONNECT_WEBHOOK_URL=
```

Same RCON settings, same `FACTION_ROLES_PATH`, same `PAVLOV_LOGS`, same `PAVLOV_UNITS` —
it is the same game server.

Run it from the same build:

```bash
cd /root/pavlov-bot
pm2 start ./dotnet-out/PavlovBot --name pavlov-bot-fallout \
  --cwd /root/pavlov-bot-fallout --interpreter none
pm2 save
```

`--cwd` is what makes it read the other `.env`; the binary reads `.env` from its working
directory. Both apps run the same published output, so `scripts/deploy.sh` updates both —
restart the clone after a deploy:

```bash
pm2 restart pavlov-bot-fallout --update-env
```

---

## Confirming it came up as the right bot

```bash
pm2 logs pavlov-bot-fallout --nostream --lines 60 | grep -E "factions|whitelists"
```

The faction line is printed at every start and names the set that is loaded:

```
factions: /root/pavlov-bot-fallout/factions.json - NCR, Legion, Brotherhood of Steel, Enclave, Kings, Followers
```

If it says `built in (FACTIONS_PATH not set)`, the clone is running the police roster and
would write the other bot's files. That line is the check worth glancing at after every
deploy.

A faction file that cannot be used **stops the bot** with the problems listed, rather than
falling back to the built-ins — a Fallout bot silently running the NYPD roster looks exactly
like the file being ignored, because it is.
