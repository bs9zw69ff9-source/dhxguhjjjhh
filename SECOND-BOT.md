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

## Give each bot its own install, if you can

This is by far the simpler answer, and it makes everything below unnecessary. If the clone
runs against a second Pavlov install — `/home/steam/pavlovserver2` beside
`/home/steam/pavlovserver` — then each bot simply ignores the other's whole tree:

```bash
# the normal bot
IGNORE_PATHS=/home/steam/pavlovserver2

# the Fallout bot
IGNORE_PATHS=/home/steam/pavlovserver
```

Nothing is shared, so a whole-directory ignore has nothing to collide with, and the
file-by-file advice further down does not apply.

Matching is separator-bounded, which matters here: **`/home/steam/pavlovserver` does not
cover `/home/steam/pavlovserver2`**. The two names look like a prefix of each other and are
treated as completely separate trees.

If you get it backwards and a bot ignores its *own* install, it refuses to start rather than
running with every whitelist write silently failing:

```
FACTION_ROLES_PATH (...) is inside IGNORE_PATHS, so this bot could never write a roster
```

### Splitting three installs between two bots

With `pavlovserver`, `pavlovserver1` and `pavlovserver2`, give the normal bot two and the
clone one. **Each bot numbers its own servers from 1** — the clone's only server is
`RCON_*_1` even though you think of it as server 2, and its channel reads `Server 1: 3/24`.

**The normal bot** (`pavlovserver` + `pavlovserver1`):

```bash
RCON_HOST_1=127.0.0.1
RCON_PORT_1=9100
RCON_PASSWORD_1=...
RCON_HOST_2=127.0.0.1
RCON_PORT_2=9101
RCON_PASSWORD_2=...

# SAME ORDER as the RCON slots - unit N is the unit for server N, by position.
PAVLOV_UNITS=pavlovserver,pavlovserver1

PAVLOV_BASE_1=/home/steam/pavlovserver
PAVLOV_BASES=/home/steam/pavlovserver,/home/steam/pavlovserver1
FACTION_ROLES_PATH=/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/FactionRoles

IGNORE_PATHS=/home/steam/pavlovserver2
PLAYER_COUNT_CHANNELS=<server 1 channel>,<server 2 channel>
```

**The Fallout bot** (`pavlovserver2`):

```bash
RCON_HOST_1=127.0.0.1
RCON_PORT_1=9102
RCON_PASSWORD_1=...

PAVLOV_UNITS=pavlovserver2
PAVLOV_BASE_1=/home/steam/pavlovserver2
PAVLOV_BASES=/home/steam/pavlovserver2
FACTION_ROLES_PATH=/home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave/FactionRoles

# BOTH, listed separately - see below.
IGNORE_PATHS=/home/steam/pavlovserver,/home/steam/pavlovserver1
PLAYER_COUNT_CHANNELS=<its one channel>
```

#### `PAVLOV_BASES` is not optional here

Leave it unset and the bot **scans for siblings** of `PAVLOV_BASE_1` matching
`pavlovserver*` and claims every one it finds:

```
Pavlov installs: pavlovserver, pavlovserver1, pavlovserver2      <- unset
Pavlov installs: pavlovserver, pavlovserver1                      <- PAVLOV_BASES set
```

Whitelist writes go to **every** install in that list, so without it both bots write
`whitelist.txt` into all three. Set it explicitly on both.

#### List each install separately in `IGNORE_PATHS`

Matching is separator-bounded, so `/home/steam/pavlovserver` does **not** cover
`pavlovserver1` or `pavlovserver2`. That is what stops a bot refusing writes to its own
install — and it means the clone must name both of the others, not just the shortest one.

#### Everything below stops applying

Separate installs means separate files. The clone can have its own
`MODSAVE_BLACKLIST_PATH`, `PAYROLL_AMOUNT` and `MODSAVE_PATH`, all pointed inside
`pavlovserver2`. The collision rules below only matter if two bots really do share one
install.

---

## Sharing one game server: what must not collide

Only if both bots really do point at one install. This is the part that costs you data if it
is wrong: two bots against one server share the game's files, and several subsystems
**rewrite a whole file from their own database**.

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

Assumes the clone owns `pavlovserver2`. Adjust the paths if yours differ.

### 1. Its own working directory

```bash
mkdir -p /root/pavlov-bot-fallout/data
cd /root/pavlov-bot
cp .env /root/pavlov-bot-fallout/.env
cp factions.fallout.example.json /root/pavlov-bot-fallout/factions.json
```

### 2. The roster directory must already exist

The bot writes roster FILES but never creates a DIRECTORY inside a game install — a missing
one means the path is wrong, and building it produces a tree the game never reads. On a fresh
install `FactionRoles` may not be there yet:

```bash
sudo -u steam mkdir -p /home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave/FactionRoles
ls -la /home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave/FactionRoles
```

Owned by `steam`, so the bot must run as root or be in the `steam` group.

### 3. A second Discord application

At <https://discord.com/developers/applications>: New Application → Bot → Reset Token. Invite
it with scopes `bot` and `applications.commands`. It is a separate app with its own token —
reusing the first bot's token would run two gateways on one identity and answer every command
twice.

### 4. Its `.env`

```bash
# ── its own identity ──
DISCORD_TOKEN=            # the SECOND application's token
CLIENT_ID=
GUILD_ID=                 # the Fallout Discord server's id
BOT_NAME=Mojave Authority

# ── its own factions and state ──
FACTIONS_PATH=/root/pavlov-bot-fallout/factions.json
DATA_DIR=/root/pavlov-bot-fallout/data

# ── its own game server ──
RCON_HOST_1=127.0.0.1
RCON_PORT_1=9102                      # pavlovserver2's RCON port
RCON_PASSWORD_1=...
RCON_HOST_2=                          # blank - it has ONE server
RCON_PORT_2=
RCON_PASSWORD_2=

PAVLOV_UNITS=pavlovserver2
PAVLOV_BASE_1=/home/steam/pavlovserver2
PAVLOV_BASES=/home/steam/pavlovserver2
PAVLOV_LOGS=/home/steam/pavlovserver2/Pavlov/Saved/Logs/Pavlov.log

FACTION_ROLES_PATH=/home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave/FactionRoles
MODSAVE_PATH=/home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave
MODSAVE_BLACKLIST_PATH=/home/steam/pavlovserver2/Pavlov/Saved/Config/ModSave/blacklist.txt

# ── never touch the other bot's installs ──
IGNORE_PATHS=/home/steam/pavlovserver,/home/steam/pavlovserver1

# ── its own channels ──
MOD_LOG_CHANNEL=
BAN_LOG_CHANNEL=
PLAYER_COUNT_CHANNELS=
SHACK_TOTAL_CHANNEL=
JOIN_WEBHOOK_URL=
KILL_WEBHOOK_URL=
CONNECT_WEBHOOK_URL=
```

Copy `OWNER_IDS`, `SUPER_OWNER_IDS`, `MASTER_NAMES` and any API keys across unchanged — those
are about people, not about which server this is.

### 5. Start it from the same build

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

### Sharing one install instead

If the clone has no install of its own, blank `MODSAVE_PATH`, `MODSAVE_BLACKLIST_PATH` and
`PAYROLL_AMOUNT`, share `FACTION_ROLES_PATH` with the first bot, and read the collision rules
above. Everything else is the same.

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
