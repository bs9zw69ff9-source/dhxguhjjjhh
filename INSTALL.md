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

**Turn "Public Bot" OFF** (Bot page). New applications have it on, which lets anyone
who knows the application id invite the bot to a server of their own. The bot only
treats Discord's *Administrator* permission as bot-Admin inside your staff guild
(`HOME_GUILD_ID`, else `GUILD_ID`, else the only guild it is in), so an invite no
longer grants anything - but there is no reason to allow one.

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
BLACKLIST_PATH=           # the game's own blacklist file; blank uses
                          # <PAVLOV_BASE_1>/Pavlov/Saved/Config/blacklist.txt
```

The bot **never creates a directory inside a game install**. A path that does not
exist is treated as not configured, on the grounds that a wrong path quietly
building a tree the game never reads is worse than a refusal that says so.

---

## 4. Run it as its own user, not root

A bot running as root turns any bug in it - or in Discord.Net, or in a plugin -
into full control of the box. It logs a warning at startup when it is root, and it
refuses to load plugins as root unless `PLUGINS_ALLOW_ROOT=1`. Everything except
`/provisionserver` and `/deleteserver` works unprivileged.

```bash
useradd -m -s /bin/bash pavlovbot
usermod -aG steam pavlovbot                   # the rosters, ledgers and ban list are steam's
chmod -R g+w /home/steam/pavlovserver*/Pavlov/Saved/Config
chmod 600 /path/to/bot/.env                   # the token and RCON passwords
```

Then `visudo` and grant exactly what it needs - the unit names are your
`PAVLOV_UNITS` (the bot prints this line itself if a restart is refused):

```
pavlovbot ALL=(root) NOPASSWD: /bin/systemctl start pavlovserver, /bin/systemctl stop pavlovserver, /bin/systemctl restart pavlovserver
pavlovbot ALL=(root) NOPASSWD: /usr/sbin/ufw
```

The ufw line is only for `/firewall`. As a non-root user the bot calls both through
`sudo -n`, so a missing grant fails with a message instead of hanging.

Getting group permissions wrong does not fail loudly. Reads succeed and writes fail,
so `/whitelist add` answers "the roster could not be written" while everything else
looks healthy.

**Game files keep their owner.** When the bot rewrites a ledger, roster or ban file
it gives the new file the old one's owner and mode, so the game server (running as
`steam`) can still save it. Earlier versions left such files owned by the bot's
user - if in-game caps or bans stopped saving after an upgrade from one, fix the
ownership once: `chown -R steam:steam /home/steam/pavlovserver*/Pavlov/Saved`.

**If this box was provisioned by an older version** of `/provisionserver`, the
`steam` account was given passwordless root and a fixed password. Remove both:

```bash
rm -f /etc/sudoers.d/pavlov-steam-full
passwd -l steam
```

(`/provisionserver` now removes that sudoers file itself when it runs, but only
then.)

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
Both live in `DATA_DIR` (default `data/` under the directory the bot runs in).
`scripts/backup.sh` handles offsite via rclone — see `scripts/backup.env.example`.
Set `BOT_DIR` to the directory the bot runs in (its pm2 cwd): the script used to
look for `bot.db` in the repo root, which is not where the C# bot keeps it.
Keep a copy of `.env` somewhere safe yourself; the script deliberately does not
upload secrets.

## Knowing when the bot itself is down

The bot's own alerts stop when the bot does. `scripts/watchdog.sh` runs from cron,
checks the bot's `/healthz`, and posts to a Discord webhook once when it goes down
and once when it comes back. Set `METRICS_PORT` in `.env`, then see
`scripts/watchdog.env.example`.
