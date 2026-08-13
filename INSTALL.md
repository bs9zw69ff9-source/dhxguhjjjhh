# Installing on a fresh VPS

Ubuntu or Debian, x86-64. Roughly fifteen minutes, most of it the first build.

The bot is Linux-only by design: it shells out to `systemctl` and `ufw` and reads
Pavlov's files from disk. Do not try to run it on Windows or macOS.

---

## 1. What it needs from the box

```bash
apt-get update
apt-get install -y git curl ca-certificates libicu-dev
```

**`libicu-dev` is not optional.** The bot keeps timestamps in `America/New_York`,
so it is built with globalization enabled and needs ICU at runtime. A minimal
server image often ships without it, and .NET then dies before `Main` with
"Couldn't find a valid ICU package installed on the system" — which reads like a
build failure and is not one.

**Node.js is still required**, for pm2 only. The Node *bot* was removed; the
process manager it ran under was not, and nothing else knows how to start this.

```bash
curl -fsSL https://deb.nodesource.com/setup_20.x | bash -
apt-get install -y nodejs
npm install -g pm2
```

Disk: budget **~2 GB**. The .NET SDK is ~600 MB, the NuGet cache it fills on the
first build ~400 MB, and the self-contained publish output ~150 MB.

---

## 2. The Discord application

At <https://discord.com/developers/applications>:

1. **New Application**, then **Bot** → **Reset Token**. Copy it — this is
   `DISCORD_TOKEN` and it is shown once.
2. Copy the **Application ID** from General Information. This is `CLIENT_ID`.
3. **OAuth2 → URL Generator**: scopes `bot` and `applications.commands`.
   Permissions: Send Messages, Embed Links, Attach Files, Read Message History,
   Manage Roles (for verification), Manage Channels (for the player-count voice
   channels). Open the generated URL and invite it.

**No privileged intents.** The bot requests `Guilds` and nothing else — it works
entirely through slash commands and never reads message content. Leave Message
Content Intent and Server Members Intent **off**; enabling them changes nothing
and widens what a leaked token can reach.

Get your own Discord user ID for `OWNER_IDS`: Settings → Advanced → Developer
Mode on, then right-click yourself → Copy User ID.

---

## 3. Clone and configure

```bash
git clone https://github.com/bs9zw69ff9-source/dhxguhjjjhh.git /root/pavlov-bot
cd /root/pavlov-bot
cp .env.example .env
```

`.env.example` documents every setting. The bot will not start without these:

```bash
DISCORD_TOKEN=          # from step 2
CLIENT_ID=              # from step 2
RCON_HOST_1=127.0.0.1
RCON_PORT_1=9100
RCON_PASSWORD_1=        # from your Pavlov RconSettings.txt
```

Three more that are not required to boot but that you will regret leaving blank:

```bash
# WITHOUT THIS, commands register GLOBALLY and Discord takes up to an hour to
# show them. With it, they appear in seconds. This is the single most common
# reason a deploy looks like it did nothing.
GUILD_ID=

# LEAVE THIS BLANK AND NOBODY HOLDS OWNER TIER. Role-based mod and admin keep
# working, so the gap is quiet - every owner-gated command just refuses
# everyone, including you.
OWNER_IDS=

# Your own in-game names, which can then never be auto-banned. A false positive
# on your own account locks you out of your own server.
MASTER_NAMES=
```

Then the paths into the Pavlov install. Get these right — every one of them is a
place where a wrong value makes a command report success and change nothing:

```bash
PAVLOV_BASE_1=/home/steam/pavlovserver
MODSAVE_PATH=/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave
FACTION_ROLES_PATH=/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/FactionRoles
MODSAVE_BLACKLIST_PATH=   # the game's own blacklist file
```

The bot **never creates a directory inside a game install**. A path that does not
exist is treated as not configured, on the grounds that a wrong path quietly
building a tree the game never reads is worse than a refusal that says so.

---

## 4. File permissions

The bot writes the faction rosters and the ban list, which the `steam` user owns.
Either run it as root, or add it to the `steam` group:

```bash
usermod -aG steam <botuser>
```

Getting this wrong does not fail loudly. Reads succeed and writes fail, so
`/whitelist add` answers "the roster could not be written" while everything else
looks healthy.

If you want `/firewall` (manual IP blocking at the OS level, never automatic),
the bot needs root or passwordless sudo for ufw only:

```
<botuser> ALL=(root) NOPASSWD: /usr/sbin/ufw
```

---

## 5. Build and verify

```bash
bash scripts/deploy-csharp.sh --install-sdk
```

This fetches the .NET SDK into `$HOME/.dotnet` (no root needed), publishes
self-contained, and then runs `--selftest`, which **builds the entire object
graph and exits without connecting to anything**. If the configuration is wrong
it prints every problem at once and changes nothing.

Fix whatever it lists and run it again. Nothing has started yet.

---

## 6. Start it

```bash
bash scripts/deploy-csharp.sh --start
pm2 save
pm2 startup        # then run the command it prints, so it survives a reboot
```

The script stamps the git commit into the binary and then **checks the running
process reports that same build**. That is not a formality: a deploy that quietly
leaves the old process running is indistinguishable from a fix that does not
work, and pm2's `start` on an already-running app does nothing while printing
success.

---

## 7. Confirm it actually came up

```bash
pm2 logs pavlov-bot-cs --lines 60
```

The startup summary names every subsystem and says why each one is off. Read it
rather than assuming — it is the fastest way to catch a path typo:

```
whitelists: /home/steam/.../FactionRoles     <- on
economy: off (MODSAVE_PATH not set)          <- off, and why
payroll: off (PAYROLL_AMOUNT not set)
```

Then check the command registration line:

```
Registered N command(s) in guild <name>          <- GUILD_ID set, instant
Registered N global command(s) - propagation...  <- no GUILD_ID, up to an hour
```

In Discord, `/health` is the end-to-end proof: it reports RCON connectivity per
server and the build id it is running.

---

## 8. Updating later

```bash
bash scripts/deploy.sh
```

Pulls `main`, resets the working tree, rebuilds, re-runs the selftest, restarts,
and verifies the build stamp. `.env`, `bot.db` and everything else git-ignored
survive the reset untouched.

Roll back to an earlier commit by naming it:

```bash
bash scripts/deploy.sh <older-sha>
```

---

## Backups

`bot.db` is the source of truth; the JSON files beside it are a readable export.
Back up the database and your `.env`. `scripts/backup.sh` handles offsite via
rclone — see `scripts/backup.env.example`.
