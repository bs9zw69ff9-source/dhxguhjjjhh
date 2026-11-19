# Installing on a fresh VPS

Ubuntu or Debian, x86-64. Roughly fifteen minutes, most of it the first build.

The bot is Linux-only by design: it shells out to `systemctl` and `ufw` and reads
Pavlov's files from disk. Do not try to run it on Windows or macOS.

---

## 1. What it needs from the box

```bash
apt-get update
apt-get install -y git curl ca-certificates build-essential
```

`build-essential` is for the build: the bot bundles SQLite and its TLS library, and both
compile a little C. The binary it produces links only glibc, so nothing else is needed at
runtime. There is no Node.js, pm2 or .NET any more.

Disk: budget **~2 GB**. The Rust toolchain and the build cache take ~1.5 GB, the binary a few
tens of MB.

---

## 2. The Discord application

At <https://discord.com/developers/applications>:

1. **New Application**, then **Bot** → **Reset Token**. Copy it — this is
   `DISCORD_TOKEN` and it is shown once.
2. Copy the **Application ID** from General Information. The bot reads it from Discord
   itself, so `CLIENT_ID` in `.env` is optional; it is only needed for the invite URL below.
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
MODSAVE_PATH=/home/steam/pavlovserver/Pavlov/Saved/ModSave
FACTION_ROLES_PATH=/home/steam/pavlovserver/Pavlov/Saved/ModSave/ServerRoles
BLACKLIST_PATH=           # the game's own blacklist file; blank uses
                          # <PAVLOV_BASE_1>/Pavlov/Saved/Config/blacklist.txt
```

The bot **never creates a directory inside a game install**. A path that does not
exist is treated as not configured, on the grounds that a wrong path quietly
building a tree the game never reads is worse than a refusal that says so.

---

## 4. Run it as its own user, not root

The shipped unit (`rust/deploy/pavlov-bot.service`) runs as root, by owner decision, because
the bot runs `systemctl`, `ufw` and the provisioner itself. If you would rather not, change
`User=` in the unit and do the following. A bot running as root turns any bug in it into full
control of the box. It logs a warning at startup when it is root. Everything except
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

### Provisioning a game server

`/provisionserver` follows the [Pavlov wiki's dedicated server guide](https://pavlovwiki.com/index.php/Setting_up_a_dedicated_server)
step for step, as root, and ends with a systemd unit:

1. the dependencies (`gdb curl lib32gcc-s1 libc++-dev unzip`, `lib32gcc1` on Ubuntu before 22.10);
   Rocky/RHEL are not touched, since the wiki's commands for them turn SELinux off machine-wide
2. the locked `steam` account and SteamCMD
3. straight after SteamCMD is there: `steamcmd.sh +login anonymous +app_update 1007 +quit`, then
   `steamclient.so` copied to `~/.steam/sdk64` and to the install's `Pavlov/Binaries/Linux`
4. `app_update 622970 -beta shack`, then the `libc++.so` link and `chmod +x PavlovServer.sh`
5. `RconSettings.txt`, `Game.ini`, the `Logs` and `Config/LinuxServer` folders, and empty
   `mods.txt`, `whitelist.txt` and `blacklist.txt`
6. the wiki's systemd unit, written as given: only the install folder and `-PORT=<port>` change
   per server (`pavlovserver` / 7777, `pavlovserver1` / 7778, and so on), then `enable --now`
7. `ufw allow` for the game, query and RCON ports
8. last, if `Pavlov/Saved/Config/LinuxServer/Engine.ini` exists, `[ConsoleVariables]` /
   `net.MaxPlayersOverride=32` is added to the bottom of it (an existing value is left alone) and
   the server restarted so it reads it. The file is never created: if it is missing the step says
   so and does nothing

`Game.ini` is the wiki's block with your name, player cap, PIN and maps filled in. If one already
exists it is left alone: the only thing ever added is `ServerKey=<value>`, and only when you give
a `serverkey` and the file has none. The API key is ignored. The wiki's `bVerboseLogging=false`
is kept, which means the kill feed shows nothing until you set it to `true`.

`/deleteserver` stops and removes the systemd unit, and also removes a Docker container of the same
name if one is still there, so servers left over from the Docker build go the same way.

A folder holding only an old server's `Pavlov/Saved` is accepted (it is handed to `steam`);
anything else in the folder is refused.

`serverkey` is 1-128 characters from letters, digits and `_-.!@%^*+=/:`, and the bot never shows it
in Discord or its log.

**Coming from the Docker build.** Servers run under systemd only now. If this box ran them as
Docker containers, `scripts/migrate-from-docker.sh` shows its plan first, and with `--yes` backs up
each `Saved` folder and removes the container, leaving `Saved` where it is for `/provisionserver`.
A leftover `docker:` prefix in `PAVLOV_UNITS` is ignored, so each server keeps its slot.

---

## 5. Build and verify

```bash
bash scripts/deploy-rust.sh --install-rust
```

This installs a Rust toolchain into `$HOME/.cargo` (no root needed), builds the release binary
into `rust-out/pavlov-bot`, and then runs `--selftest`, which **builds the entire object graph
and exits without connecting to anything**. If the configuration is wrong it prints every
problem at once and changes nothing.

Fix whatever it lists and run it again. Nothing has started yet.

---

## 6. Start it

```bash
cp rust/deploy/pavlov-bot.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable pavlov-bot
bash scripts/deploy-rust.sh --start --unit pavlov-bot
```

If you cloned somewhere other than `/root/pavlov-bot`, edit `WorkingDirectory` and `ExecStart`
in the unit first.

The script stamps the git commit into the binary and then **checks the running process
reports that same build** in the journal. That is not a formality: a deploy that quietly
leaves the old process running is indistinguishable from a fix that does not work.

---

## 7. Confirm it actually came up

```bash
journalctl -u pavlov-bot -n 80 --no-pager
```

The startup summary names every subsystem and says why each one is off. Read it rather than
assuming: it is the fastest way to catch a path typo. Then find the command registration line:

```
registered commands in guild ...                              <- GUILD_ID set, instant
registered global commands; propagation can take up to an hour  <- no GUILD_ID
```

In Discord, `/health` is the end-to-end proof: it reports RCON connectivity per server and the
build it is running.

---

## 8. Updating later

```bash
bash scripts/deploy.sh
```

Pulls `main`, resets the working tree, rebuilds, re-runs the selftest, restarts, and verifies
the build stamp. `.env`, `bot.db` and everything else git-ignored survive the reset untouched.

Roll back to an earlier commit by naming it:

```bash
bash scripts/deploy.sh <older-sha>
```

---

## Backups

`bot.db` is the source of truth; the JSON files beside it are a readable export.
Both live in `DATA_DIR` (default `data/` under the directory the bot runs in).
`scripts/backup.sh` handles offsite via rclone — see `scripts/backup.env.example`.
The script reads the directory the bot runs in from the `pavlov-bot` unit's
`WorkingDirectory`; set `BOT_DIR` only if systemd cannot tell it.
Keep a copy of `.env` somewhere safe yourself; the script deliberately does not
upload secrets.

## Knowing when the bot itself is down

The bot's own alerts stop when the bot does. `scripts/watchdog.sh` runs from cron,
checks the bot's `/healthz`, and posts to a Discord webhook once when it goes down
and once when it comes back. Set `METRICS_PORT` in `.env`, then see
`scripts/watchdog.env.example`.
