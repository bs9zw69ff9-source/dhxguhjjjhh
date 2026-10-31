# Anti-DDoS: firewall, packet logs and CrowdSec

This is the operator's guide to the bot's anti-DDoS layer on the Ubuntu 24.04 VPS. Commands are
run as root over SSH; `/antiddos ...` is the Discord command.

## What it can and cannot do

Read this first, because it decides what to expect.

**It cannot stop a flood that fills the VPS's network link.** Once the pipe to the server is full,
packets are lost before nftables, CrowdSec or anything else on the box sees them, and the bot loses
Discord too. Only filtering upstream stops that: the hosting provider's DDoS protection, a provider
with game/UDP filtering, or a protected tunnel in front of the server. If an attack takes the server
offline while `/antiddos traffic` shows modest packet rates, the flood was dropped upstream or
saturated the link. Either way, the provider is who to talk to, and the event log and the attack
reports are the evidence to send them.

**What it does, for traffic that reaches the server:**

- Filters in the kernel, before connection tracking, so a flood that gets through costs as little
  as possible: blocklisted sources, per-address limits during an attack, a cap on non-player game
  traffic, an SSH new-connection limit, a ping limit.
- Counts every source address in the kernel, per protocol and port, and writes one record per
  source per minute. No packet is copied to the bot to be counted.
- Logs selected packets (drops, suspicious sources, a small sample of game traffic) as JSON Lines,
  rate-limited in the kernel, so the log cannot outrun its budget.
- Detects sustained UDP floods from one address, connection bursts and port spreading, and bans
  repeat offenders through CrowdSec. Known players and trusted addresses are never auto-banned.
- Runs CrowdSec for what it is good at: SSH brute force and ufw-logged port scans, plus the
  community blocklist, enforced by CrowdSec's own nftables bouncer.
- Keeps it all on disk, rotated and bounded, and searchable from Discord after the attack.

**What it does not have:** packet contents. It records headers of the packets it was configured to
log, per-minute totals for every source, and interface counters. Packets that were not logged
cannot be reconstructed later, and nothing here pretends otherwise: a packet record exists only
for what the firewall chose to log at that moment.

## How it fits together

```
 internet ──> ens3 ──> [prerouting -300: GUARD]  inet pavlovbot_ddos   (before conntrack)
                         trusted ─────────────> skip the limits below
                         SSH (after new-conn limit) ─> through
                         known players ───────> through
                         blocked / pings / attack limits ─> drop + count + log (NFLOG 7)
                  ──> [conntrack -200]
                  ──> [prerouting -150: OBSERVE] inet pavlovbot_ddos   (never drops)
                         per-source counters (kernel sets), suspicious + sample logs (NFLOG 7)
                  ──> [input/forward -10]  ip crowdsec / ip6 crowdsec6   (CrowdSec bouncer bans)
                  ──> [ufw / Docker]       untouched
                  ──> Pavlov, SSH, ...

 NFLOG 7 ──> ulogd2 ──> /var/log/anti-ddos/packets/packets.jsonl
 bot, every minute: nft -j list table ──> sources.jsonl, traffic.jsonl, detections ──> events.jsonl
                    cscli decisions ──> events.jsonl;  bans ──> cscli decisions add
 logrotate every 10 min (anti-ddos-logrotate.timer): size + age, gzip, retention
```

- **The table `inet pavlovbot_ddos` is the bot's.** It is owned by the one bot with `ATTACK_CHANNEL`
  set. It holds drop rules only with `ATTACK_MITIGATION=on`, and every chain has policy `accept`,
  so it can only drop the specific things above. It never flushes the ruleset or touches ufw,
  Docker or CrowdSec's tables. `nft delete table inet pavlovbot_ddos` removes all of it.
- **An accept in one table does not bypass another.** A trusted address skips the bot's limits,
  but ufw and CrowdSec still judge it. Nothing here can open a port ufw has closed.
- **Both chains hook prerouting**, so containers on a Docker bridge are covered too, not just the
  host. Loopback and other interfaces are never touched.
- **Every change is checked, then applied atomically.** The bot runs `nft -c` first, then loads the
  whole table in one transaction. A refused change leaves the old rules in force.
- **Boot order, with no circular dependency:**
  1. `ufw` and `anti-ddos-firewall.service` load the last good copy of the table. They run before
     the network is up and need only the disk.
  2. `ulogd2`, `crowdsec`, then `crowdsec-firewall-bouncer`.
  3. `pavlov-bot`, which re-applies its table with the current settings.

  None of them waits on the bot. If the bot never starts, the boot copy keeps filtering.

## Install

The only risk to remote access is CrowdSec banning *you* after failed SSH logins. The install
allowlists your address first. Nothing here needs downtime: the game servers keep running.

```bash
cd /root/pavlov-bot && git pull
sudo bash scripts/antiddos/audit.sh                      # read-only; read what it reports
sudo OWNER_IP=<the IP you SSH from> bash scripts/antiddos/install.sh
```

The install backs up what it touches to `/root/anti-ddos-backup-<time>/` and installs:

- `ulogd2` and `ulogd2-json`, writing the packet log;
- the rotation timer and the boot unit;
- CrowdSec 1.8.x and `crowdsec-firewall-bouncer-nftables` from CrowdSec's signed repository;
- the `sshd` and `iptables` collections, the latter for ufw port scans;
- 30 days of CrowdSec history (`/etc/crowdsec/config.yaml.local`).

It ends by running `verify.sh`. Then add the bot settings below and restart:

```bash
sudo systemctl restart pavlov-bot
sudo bash scripts/antiddos/verify.sh
```

In Discord, run `/antiddos status`, then `/antiddos allow add ip:<your IP> comment:owner`.

For port-scan detection, ufw must log blocked packets: `sudo ufw logging low`. That is ufw's own,
rate-limited setting, read by CrowdSec. Then re-run the install.

### Files and permissions

| Path | Owner, mode | Holds |
|---|---|---|
| `/var/log/anti-ddos/` | root:ulog 0710 | everything below; nobody else can list it |
| `packets/packets.jsonl` | ulog 0600 | packet records (ulogd2, which runs as `ulog`, not root) |
| `sources.jsonl`, `traffic.jsonl`, `events.jsonl` | root 0600 | the bot's records |
| `capture/ring_*.pcap` | root 0600 | optional header ring (`PACKET_LOG`, `PACKET_RING_MB`) |
| `exports/` | root 0700 | `/antiddos export` output, newest 10 kept |
| `/var/lib/anti-ddos/firewall.nft` | root 0600 | boot copy of the table (attack limits off) |
| `/root/pavlov-bot/data/antiddos-state.json` | root | trusted list, threshold overrides, mode |
| `/etc/anti-ddos/logrotate.conf` | root | rotation, run by `anti-ddos-logrotate.timer` |

The bot runs as root, by the owner's decision, so it can read everything here. That is a deliberate
trade-off, not least privilege: the bot itself is the boundary. Discord users only see what
`/antiddos` shows them: Admin to read, Owner to change. Every view is ephemeral and every change
is audited.

## Settings (`.env`)

All are optional. The defaults are chosen so normal play never trips anything.

| Key | Default | What |
|---|---|---|
| `ATTACK_CHANNEL` | - | Turns the DDoS monitor and this whole layer on, on ONE bot |
| `ATTACK_MITIGATION` | off | `on`: the table has drop rules. Off, it only counts and logs |
| `ATTACK_INTERFACE` | default route | The public interface (never `lo`) |
| `ATTACK_GAME_PORTS` | `7777-7799,8177-8199` | Pavlov UDP ports |
| `ATTACK_PER_IP_PPS` / `ATTACK_UNKNOWN_PPS` / `ATTACK_BLOCK_MINUTES` | 1000 / 5000 / 10 | Attack-time limits |
| `ANTIDDOS_SSH_PORTS` | `22` | Every port sshd listens on: always let through |
| `ANTIDDOS_SSH_NEW_PER_MINUTE` | 30 | New SSH connections a minute per address |
| `ANTIDDOS_ICMP_PPS` | 50 | Pings a second, everybody together |
| `ANTIDDOS_SUSPECT_UDP_PPS` | 300 | A non-player above this is suspicious (logged, detected) |
| `ANTIDDOS_SUSPECT_SYN_PPS` | 20 | Connection rate per address that is logged |
| `ANTIDDOS_SYN_PER_MINUTE` / `ANTIDDOS_PORT_SPREAD` | 120 / 50 | Burst and scan detection |
| `ANTIDDOS_AUTOBAN` | on | Ban repeat offenders (off: alert only) |
| `ANTIDDOS_ESCALATE_AFTER` / `ANTIDDOS_CORRELATION_MINUTES` | 3 / 10 | Detection minutes within the window before a ban |
| `ANTIDDOS_BAN_MINUTES` / `ANTIDDOS_MAX_BAN_HOURS` | 60 / 168 | First ban; repeats within a day double it, up to the cap |
| `ANTIDDOS_MAX_BANS_PER_HOUR` / `ANTIDDOS_SPOOF_SOURCES` | 30 / 5000 | Safeguards (see below) |
| `ANTIDDOS_LOG_MODE` | normal | Resting mode: `normal` or `elevated` |
| `ANTIDDOS_INCIDENT_MINUTES` / `ANTIDDOS_AUTO_INCIDENT` | 30 / on | A detected flood raises logging for this long |
| `ANTIDDOS_LOG_DIR` | `/var/log/anti-ddos` | Must be persistent storage |
| `ANTIDDOS_LOG_MAX_MB` | 2048 | Cap on the log folder (rotated files included) |
| `ANTIDDOS_WARN_FREE_PERCENT` / `ANTIDDOS_PRESSURE_FREE_PERCENT` / `ANTIDDOS_RESERVE_MB` | 20 / 10 / 2048 | Disk guard |
| `ANTIDDOS_CROWDSEC` | on | Use CrowdSec when installed |
| `ANTIDDOS_ALERT_CHANNEL` | `ATTACK_CHANNEL` | Where alerts go. **Staff-only**: alerts show addresses |
| `ANTIDDOS_NFLOG_GROUP` | 7 | Must match `[log1] group=` in `/etc/ulogd.conf` |
| `PACKET_LOG` / `PACKET_RING_MB` | on / 2048 | Optional rolling pcap of headers; paused first under disk pressure |

The thresholds the owner can change from Discord (`/antiddos threshold set`) are kept in the state
file and layered over these. Each has a floor, so a typo cannot block players or lock out SSH.

## Logging

### Three kinds of record, never mixed

| File | Kind | One line per |
|---|---|---|
| `packets/packets.jsonl` | **individual packets** the kernel logged | dropped, suspicious or sampled packet |
| `sources.jsonl` | **aggregated** per-source totals | source per minute (the 100 busiest) |
| `traffic.jsonl` | **aggregated** interface and rule totals | minute |
| `events.jsonl` | **events** | detection, ban, CrowdSec decision, mode change, admin action, health |

Timestamps are UTC. Bot records carry `ts` (ISO 8601, `Z`). Packet records carry
`oob.time.sec`/`usec`, which are epoch seconds. ulogd's own `timestamp` field has no zone; ignore it.

### Modes

Every log rule draws on one of three shared kernel limits per mode. So the most records a second
is fixed, whatever arrives:

| Mode | Drops/s | Suspicious/s | Samples/min | Worst case |
|---|---|---|---|---|
| minimal (storage pressure) | 1 | 1 | 0 | ~2/s |
| **normal** | 2 | 2 | 6 | ~4/s, ~2.6 KB/s |
| elevated | 20 | 20 | 60 | ~41/s |
| incident | 50 | 50 | 300 | ~105/s, ~240 MB/hour |

Measured on Ubuntu 24.04 with nftables 1.0.9 and ulogd2 2.0.8, against a 1,400 packets/s flood:
- In incident mode the suspicious records came in at exactly 50 a second.
- A packet record averages about 640 bytes.

Rules for modes:
- **Incident mode is always time-boxed.** A detected flood switches it on for
  `ANTIDDOS_INCIDENT_MINUTES`. `/antiddos logging mode:incident minutes:60` sets or extends it.
  It returns to the resting mode on its own, and survives a bot restart until it expires.
- **A mode changes logging, never filtering.** The drop rules are the same in every mode, and a
  test pins that.
- **Storage pressure caps the mode** whatever was asked: minimal under pressure, off when the disk
  is nearly full.

### Rotation, retention, storage limits

- **logrotate**, every 10 minutes (`anti-ddos-logrotate.timer`, with its own state file, so the
  daily job never double-rotates):
  - packets, sources and traffic at 50 MB or daily, gzipped one cycle later, deleted after 30 days;
  - events at 20 MB or weekly, kept 365 days.

  Rotated files are named `name-YYYYMMDD-HHMMSS[.gz]` and stay searchable.
- **The bot's storage guard**, every minute:
  - **warning** at 20% free, or the logs at 80% of `ANTIDDOS_LOG_MAX_MB`;
  - **pressure** at 10% free, under the 2 GB reserve, or over the cap. Logging drops to minimal,
    the pcap ring pauses, and the oldest rotated files are deleted (events last).
  - **critical** under half the reserve: packet logging stops. Filtering and events carry on.

  Each change of level is one alert and one event, not one a minute.
- **Nothing loops.** Packets go kernel → NFLOG → ulogd2 → file, never through printk, journald or
  rsyslog. CrowdSec's bouncer logs to its own file, and `deny_log` is left off.

### Sample records (real, from the test run)

Packet (ulogd2, trimmed):

```json
{"oob.prefix": "pbd suspect syn-burst ", "oob.time.sec": 1793305125, "oob.time.usec": 313190, "oob.in": "veth-h", "oob.out": "", "src_ip": "10.99.0.2", "dest_ip": "10.99.0.1", "ip.protocol": 6, "src_port": 45000, "dest_port": 9020, "raw.pktlen": 60, "tcp.syn": 1, "tcp.ack": 0, "ip.ttl": 64}
```

The prefix is the verdict: `pbd <drop|suspect|sample> <reason>`. `drop` means this table dropped
it. `suspect` and `sample` mean it passed this table, and ufw may still have dropped it later.

Source minute:

```json
{"v":1,"type":"source","ts":"2026-10-29T20:19:38Z","window_s":60,"src":"10.99.0.2","packets":83910,"bytes":7693800,"pps":1398.4,"udp":83100,"tcp":810,"other":0,"syn":810,"dropped":0,"distinct_ports":31,"top_ports":[{"proto":"udp","port":7777,"packets":83100},{"proto":"tcp","port":9000,"packets":27}],"player":false,"trusted":false}
```

Events:

```json
{"v":1,"type":"event","ts":"2026-10-29T20:20:38Z","category":"ban","severity":"warning","src":"10.99.0.2","action":"banned","reason":"udp-rate+syn-burst repeated (src-202610292019-10.99.0.2)","expires":"2026-10-29T21:20:38Z","actor":"auto","incident":"src-202610292019-10.99.0.2","detail":{"via":"crowdsec"}}
{"v":1,"type":"event","ts":"2026-10-29T20:20:38Z","category":"crowdsec-decision","severity":"info","src":"10.99.0.2","action":"ban","reason":"pavlovbot: udp-rate+syn-burst repeated (src-202610292019-10.99.0.2)","expires":"2026-10-29T21:20:38Z","actor":"bot","detail":{"id":"8","known_player":"false","origin":"cscli","scope":"Ip"}}
```

## Investigating, after the fact

### From Discord

| Command | Shows |
|---|---|
| `/antiddos status` | firewall, mode, last minute's rates, drops, CrowdSec, storage, capture |
| `/antiddos traffic minutes:180` | packet and byte rates over time, busiest sources |
| `/antiddos top from:"2026-10-29 17:00" to:"2026-10-29 18:00"` | highest-volume sources in a window |
| `/antiddos history ip:203.0.113.5 minutes:1440` | every record about one address: packets, minutes, events (all attached) |
| `/antiddos history action:dropped category:blocked minutes:60` | what the block list dropped |
| `/antiddos history kind:events category:ban minutes:10080` | every ban for a week |
| `/antiddos history protocol:udp port:7777 kind:packet` | logged packets to a game port |
| `/antiddos detections` | recent detections and what was done |
| `/antiddos bans` | everything blocked now: firewall blocks and CrowdSec bans |
| `/antiddos crowdsec` | decisions by origin, alerts in 24h, bouncer and parser health |
| `/antiddos storage` | size, file count and oldest record per kind; disk; rotation |
| `/antiddos export ip:... from:... to:...` | gzip JSON Lines on the server; attached if under 8 MB |

Results are paged (15 rows). A search attaches up to 5,000 matching records, and a range is at most
7 days (31 for traffic, top and export). For more, export and read it on the server.

To see the packets around a ban: take the ban's `ts` from `history kind:events category:ban`, then
run `history ip:<address> from:<ts minus 10 min> to:<ts plus 10 min>`. That shows what the address
sent before and after, in one list.

### From the shell

```bash
cd /var/log/anti-ddos

# Top sources over the last day, rotated and compressed files included
zcat -f sources.jsonl* | jq -r 'select(.ts >= "2026-10-29T00:00") | [.src, .packets] | @tsv' \
  | awk '{s[$1]+=$2} END {for (a in s) print s[a], a}' | sort -rn | head -20

# Everything one address did
zcat -f sources.jsonl* events.jsonl* packets/packets.jsonl* | grep -F '"203.0.113.5"' | jq -c .

# Inbound packet and UDP rate, minute by minute
zcat -f traffic.jsonl* | jq -r '[.ts, .pps, .mbps, .counters.udp.packets] | @tsv' | tail -60

# What the firewall dropped, by reason, and from whom
zcat -f packets/packets.jsonl* | jq -r 'select(."oob.prefix" | startswith("pbd drop")) | [."oob.prefix", .src_ip] | @tsv' | sort | uniq -c | sort -rn | head

# Bans and CrowdSec decisions, with who made them
zcat -f events.jsonl* | jq -c 'select(.category == "ban" or .category == "crowdsec-decision")'

# Retention: size, oldest record
du -sh /var/log/anti-ddos; zcat -f events.jsonl* | head -1 | jq -r .ts

# Live counters, straight from the kernel
nft list table inet pavlovbot_ddos | grep -E 'counter packets [1-9]'
cscli decisions list; cscli alerts list --since 24h; cscli metrics
```

## Detection and response

1. **Monitor.** Every minute each source is judged:
   - `udp-rate`: a non-player sustaining over `ANTIDDOS_SUSPECT_UDP_PPS` (a Pavlov client sends
     roughly 30-130 packets/s, so 300 is far above it);
   - `syn-burst`: new connections over `ANTIDDOS_SYN_PER_MINUTE`;
   - `port-spread`: a scan, or replies to a reflection attack;
   - `dropped`: heavy drops, recorded only.

   The first detection of an address opens an incident and posts one alert. Further minutes of the
   same incident do not post again.
2. **Rate-limit.** Always on with mitigation: the SSH new-connection limit and the ping limit.
   During a detected flood the attack limits engage: per-address block at `ATTACK_PER_IP_PPS`, and
   a cap on all non-player game traffic at `ATTACK_UNKNOWN_PPS`.
3. **Temporary ban.** After `ANTIDDOS_ESCALATE_AFTER` detection minutes within the window, the
   address is banned through CrowdSec for `ANTIDDOS_BAN_MINUTES`. Without CrowdSec it goes in the
   kernel block list for the same time.
4. **Escalate.** A repeat within a day doubles the ban, up to `ANTIDDOS_MAX_BAN_HOURS`. Bans
   always expire. Permanent bans are only ever placed by hand (`/antiddos block duration:permanent`).

**Safeguards against spoofing and false positives.** A UDP source address is whatever the sender
wrote, so:

- Known players (every confirmed address seen in 30 days) and trusted addresses are never banned
  automatically, and are let through the attack limits. Spoofing a player's address cannot get them
  banned.
- When a minute has more than `ANTIDDOS_SPOOF_SOURCES` sources, the flood is likely spoofed.
  Per-address bans are held and recorded as held, and the aggregate cap does the work.
- At most `ANTIDDOS_MAX_BANS_PER_HOUR` automatic bans an hour.
- Bans are by single address only. A hand ban is at most a /24 or /48, never a provider's range.
- Reputation alone never bans a player. A CrowdSec decision on a known player is flagged in the
  alert for a human to look at: `/antiddos unban`, or `/antiddos allow add` to stop it recurring.

**CrowdSec does not see UDP floods.** It reads logs (SSH, ufw), and a UDP flood leaves none. Floods
are the firewall's job, and repeat flooders reach CrowdSec through the bot's bans.

## Verification checklist

`sudo bash scripts/antiddos/verify.sh` automates most of this. Run it after install, after a bot
restart and after a reboot.

- [ ] **Firewall enforcement.**
  - `nft list table inet pavlovbot_ddos` shows `guard` (priority raw) and `observe`.
  - The `ssh` rule comes before any `jump drop_`, and no chain has `policy drop`.
  - Turn the attack limits on with `/antiddos threshold set name:per_ip_pps value:200`, flood from
    a test machine, and watch `counter ... comment "per-source"` grow while SSH keeps working.
    Set the value back afterwards.
- [ ] **Trusted and players untouched.** Run `/antiddos allow add` for your IP, then
  `/antiddos history ip:<yours>`. Trusted minutes show `"trusted":true`, and nothing from you is
  ever dropped.
- [ ] **CrowdSec.**
  - `cscli lapi status` passes, `systemctl is-active crowdsec-firewall-bouncer` is active, and
    `nft list table ip crowdsec` exists.
  - `cscli decisions add --ip 192.0.2.1 --duration 5m` appears in that table's set, and
    `cscli decisions delete --ip 192.0.2.1` removes it.
  - `/antiddos crowdsec` shows the bouncer's last pull within minutes.
- [ ] **Persistent logging.** `packets/packets.jsonl` is written by `ulog`, mode 0600. Sources,
  traffic and events grow every minute. `stat -f -c %T /var/log/anti-ddos` is not `tmpfs`.
- [ ] **Storage limits.** `/antiddos storage` shows the cap and disk. To test the guard, run with
  `ANTIDDOS_LOG_MAX_MB=1` for a minute and watch the alert, `mode minimal`, then eviction. Then put
  it back.
- [ ] **Rotation.** `sudo systemctl start anti-ddos-logrotate.service`, then check for
  `ls /var/log/anti-ddos/*-2026*`. A day later the rotated files are `.gz`, and `/antiddos history`
  still finds records in them.
- [ ] **Historical investigation.** `/antiddos history minutes:1440` and `/antiddos top` return
  records. `/antiddos export` writes a `.jsonl.gz` that `zcat | jq` reads.
- [ ] **Incident mode.** `/antiddos logging mode:incident minutes:5`, then `/antiddos status` shows
  incident. After 5 minutes it is normal again, and `events.jsonl` has `"action":"lapsed"`.
- [ ] **Reboot recovery.**
  - `sudo reboot`. Before the bot is up, `nft list table inet pavlovbot_ddos` already exists
    (anti-ddos-firewall.service).
  - Afterwards `verify.sh` passes, logs carry on in the same files, and the trusted list and
    threshold overrides are still there.

## Rollback and recovery

```bash
sudo bash scripts/antiddos/rollback.sh                    # tables, bouncer, timer, ulogd config
sudo PURGE_CROWDSEC=1 bash scripts/antiddos/rollback.sh   # and remove CrowdSec
```

Rollback keeps the logs. The bot re-creates its table at its next start while `ATTACK_CHANNEL` is
set. Remove `ATTACK_MITIGATION` to keep only counting and logging.

**Locked out of SSH?** Log in through the provider's web console (VNC/serial) as root and run the
rollback. If even that is not possible, these two commands undo every drop this setup can cause:

```bash
nft delete table inet pavlovbot_ddos
systemctl stop crowdsec-firewall-bouncer     # removes CrowdSec's tables on the way out
```

If CrowdSec banned you, the allowlist is the fix:
`cscli decisions delete --ip <you>; cscli allowlists add pavlovbot <you>`.

## Known limits

- **IPv6:**
  - Filtered and counted like IPv4, and neighbour discovery is never touched.
  - Players are tracked by IPv4 address only, so an IPv6 player shares the unknown-traffic cap
    during an attack.
  - ulogd's IPv6 field names come from its BASE plugin and were not exercised on a live IPv6 link
    in testing.
- **Per-source counting is bounded.** The kernel sets hold 65,535 sources. Past that,
  `accounting_saturated` is set, and new sources are only in the totals. That is usually a spoofed
  flood.
- **Detection is per minute.** Floods are caught within seconds by the attack monitor. Per-address
  detection and bans work in minutes, by design: one bad second is never a ban.
- **The bot's in-memory history.** Detection counts and repeat-offender doubling reset when the
  bot restarts, which can only make a ban later, never sooner. Bans themselves live in CrowdSec
  and survive.
- **Peak packets/s** in `traffic.jsonl` comes from the attack monitor's one-second readings.
