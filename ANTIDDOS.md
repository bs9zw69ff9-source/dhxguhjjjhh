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
  as possible: blocklisted sources, always-on per-address limits (game-port packet rate and TCP
  connection rate, instant kernel blocks), a cap on non-player game traffic during an attack, an
  SSH new-connection limit, a ping limit.
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
                         blocked / pings / per-address limits / attack cap ─> drop + count + log (NFLOG 7)
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
- `anti-ddos-capture.service` (dumpcap), the packet header ring, which keeps recording while the
  bot is down. Its settings are in `/etc/anti-ddos/capture.env`: the interface from the default
  route (override with `CAPTURE_IFACE=`) and the budget from the bot's `PACKET_RING_MB`;
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
| `ATTACK_PER_IP_PPS` / `ATTACK_BLOCK_MINUTES` | 1000 / 10 | One non-player over this to the game ports is blocked in the kernel, at once, for this long |
| `ANTIDDOS_ALWAYS_BLOCK` | on | That per-address limit all the time (`off`: only during a detected flood) |
| `ANTIDDOS_SYN_BLOCK_PPS` | 50 | New TCP connections a second from one address (not SSH) before it is blocked |
| `ATTACK_UNKNOWN_PPS` | 5000 | During a flood only: the cap on all non-player game traffic together |
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

### When the connection is lost, or the bot is

What happens to the record when something breaks:

- **The link itself goes down.** Nothing arrives, so there is nothing to record. The bot writes a
  `link` event when the interface goes down and another when it comes back up, and sends an alert
  for each.
- **A flood outruns the server.** A flood that fills the network card or the kernel's input queue
  is dropped before nftables, tshark or any per-source counter sees it. Every traffic record now
  carries what was lost that minute:
  `"link":"up","loss":{"lost":…,"nic_dropped":…,"nic_missed":…,"nic_fifo":…,"nic_over":…,"softnet_dropped":…,"conntrack_drops":…,"conntrack_count":…,"conntrack_max":…}`.
  A minute with any loss also gets a `kernel-loss` event. When the connection table reaches 90%
  full, the bot writes a `conntrack` event and sends an alert, then writes a second event when it
  drops back under 80%.
- **The bot is down** (crashed, restarted, being deployed, or the server rebooted). The kernel
  keeps counting. The bot saves its last reading to `/var/lib/anti-ddos/last-reading.json` every
  minute and when it stops. On the next start it writes one traffic record covering the whole gap,
  with `"gap":"<why>"`, plus source records marked `"gap":true` and a `gap` event:
  - The packet total is exact.
  - Per-source totals are a lower bound: the kernel forgets a source three minutes after its last
    packet, so a source that went quiet earlier than that is missing.
  - After a reboot, the record covers only the time since boot.
  - Gaps under 90 seconds or over 7 days are not written.
- **The packet headers.** `anti-ddos-capture.service` writes the first 128 bytes of every inbound
  IPv4 packet to the ring in `capture/`, with or without the bot.
  - While the service runs, the bot does not start its own tshark. It only keeps the ring to
    budget, cuts attack pcaps from it, and stops the service under storage pressure. It starts the
    service again once the disk recovers.
  - The ring is finite: at the default 2 GB, a heavy flood fills it in minutes, and then the oldest
    files go.
  - If the ring loses packets, dumpcap records how many in the pcap itself.
- **Discord is unreachable.** Alerts are held, oldest first, at most 50, and posted when Discord
  answers again. Each one is marked "Posted late" with the time it was raised. Every alert is also
  an event on disk, and each event is flushed to disk as it is written.

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

**`/configure` → the second menu, "Anti-DDoS"** is the owner's control panel for all of it:
- views: status, last hour's traffic, busiest sources in 24h, look up an IP, detections,
  everything blocked, CrowdSec, storage, the trusted list and thresholds;
- changes: ban, unban, trust, untrust, change a threshold, incident/elevated/normal logging,
  export, and unblock everything (which asks you to type UNBLOCK);
- packet tools: status, install, and analyze the latest capture.

Each entry runs exactly what the slash command below runs, with the same checks and the same audit
record. Entries that need a value open a short form. The slash commands stay, for Admin-level viewing
and for the options the menu fixes (custom time ranges, filters, pages).

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

## CrowdSec health alerts

Every 10 minutes the bot checks that CrowdSec is actually working, and alerts once when that
changes ("CrowdSec needs attention", then "CrowdSec healthy again"):

- **Bouncers:** none registered, one revoked, or one that hasn't pulled decisions for 5 minutes.
  CrowdSec's bans are then not enforced.
- **Parsers:** "lines read, none parsed" is *not* a fault by itself. A parser takes only the lines
  a scenario wants, such as a failed SSH login or a ufw block. A quiet auth.log is all cron and sudo
  sessions, and those are normal unparsed lines. So when a source shows none parsed, the bot asks
  `cscli explain` whether a fake failed login (auth.log) or ufw block (kern.log, ufw.log, syslog)
  parses. The fake line is written in the file's own timestamp format and comes from 192.0.2.1, a
  documentation address; `explain` raises no alerts and makes no bans. The bot alerts only if that
  line fails to parse, and it re-tests each source at most every 6 hours.

## Detection and response

1. **Monitor.** Every minute each source is judged:
   - `udp-rate`: a non-player sustaining over `ANTIDDOS_SUSPECT_UDP_PPS` (a Pavlov client sends
     roughly 30-130 packets/s, so 300 is far above it);
   - `syn-burst`: new connections over `ANTIDDOS_SYN_PER_MINUTE`;
   - `port-spread`: a scan, or replies to a reflection attack;
   - `dropped`: heavy drops, recorded only.

   The first detection of an address opens an incident and posts one alert. Further minutes of the
   same incident do not post again.
2. **Block in the kernel, at once.** Always on with mitigation, in the same second:
   - a non-player over `ATTACK_PER_IP_PPS` to the game ports, blocked for
     `ATTACK_BLOCK_MINUTES` (Pavlov clients send roughly 30-130 packets/s; players never reach
     this rule);
   - an address opening TCP connections faster than `ANTIDDOS_SYN_BLOCK_PPS`, for the same time
     (SSH has its own limit and is never blocked);
   - the SSH new-connection limit and the ping limit.

   Each block is an event (`firewall-block`) and counts as evidence below. During a detected
   flood, the cap on all non-player game traffic (`ATTACK_UNKNOWN_PPS`) also engages.
3. **Temporary ban.** Banned through CrowdSec for `ANTIDDOS_BAN_MINUTES`, or in the kernel block
   list without CrowdSec, when either:
   - it collects `ANTIDDOS_ESCALATE_AFTER` detection minutes within the window, firewall blocks
     included, so an address the firewall keeps having to block gets a real ban;
   - or a single minute is severe: five times over a threshold for the whole minute, such as a
     sustained 1,500 UDP packets/s from a non-player, or 250 ports scanned.
4. **Escalate.** A repeat within a day doubles the ban, up to `ANTIDDOS_MAX_BAN_HOURS`. The last
   day's bans are reloaded from the event log at startup, so a restart doesn't reset the doubling.
   Outside a flood, automatic bans always expire.
5. **Permanent ban during a flood** (`ANTIDDOS_ATTACK_PERMABAN`, on by default; the
   `attack_permaban` threshold turns it off without a restart).
   - **When it applies:** while the attack monitor has a flood on, and for one correlation window
     after it ends. The per-minute judging lags the flood, so detections just after it still count.
   - **What it does:** any address detected in that time (`udp-rate`, `syn-burst`, `port-spread`,
     or a `firewall-block`) is banned through CrowdSec in that first minute, for ten years, which
     is CrowdSec's "forever". The ban event carries `"permanent":"true"`.
   - **Limits:**
     - The hourly cap rises to `ANTIDDOS_ATTACK_BANS_PER_HOUR` (300), because a botnet is hundreds
       of addresses.
     - Players and trusted addresses are still never banned. The spoofing guard does **not** apply
       during a flood: a spoofed flood's addresses are banned too.
     - An address CrowdSec already holds for a year or more is not banned again.
     - `/antiddos unban` lifts a ban, and the address can then be banned by a later flood.
   - **Without CrowdSec:** the kernel's block list is lost at a reboot, so the address gets the
     longest temporary ban instead (`ANTIDDOS_MAX_BAN_HOURS`), and the event says why.
   - **It only fires when a flood is detected.** If `ATTACK_PPS` and `ATTACK_MBPS` are above what
     an attack actually reaches, no flood starts and these bans never happen.
   - **The cost:** botnet addresses are mostly home connections that get reassigned, so a permanent
     ban can outlive its attacker and land on a future player. A ban on a known player shows in
     the CrowdSec alerts for you to lift.
6. **Every source of a flood, banned when it ends.** A botnet of thousands sends a few packets a
   second from each address and never trips a per-address rate, so step 5 alone would catch a few
   dozen. When the attack monitor declares the flood over, the bot bans **every** address in it, in
   one `cscli decisions import` (about a second for 5,000 addresses), before the summary is
   posted.
   - **What counts as a source:** any address that sent at least `ANTIDDOS_FLOOD_BAN_MIN_PACKETS`
     (50) packets to the game ports during the attack.
     - The packets come from the attack capture, which sees them before the firewall drops them.
     - A server browser's query (one or two packets) and a monitoring ping (not a game port) don't
       count.
     - Neither do replies to the server's own connections, such as Discord or Steam, because those
       arrive on other ports.
   - **Never banned:**
     - known players, checked at the end, so one who joined mid-flood has had time to appear on
       the roster;
     - trusted addresses;
     - private, carrier-NAT, loopback, link-local and multicast addresses;
     - addresses CrowdSec already holds for a year or more.
   - **No spoofing guard, by choice.** A flood with forged source addresses gets those addresses
     banned. They belong to someone, possibly a future player, and banning them doesn't stop the
     next spoofed flood, which forges different ones.
   - **Without CrowdSec:** the addresses are blocked in the kernel for `ANTIDDOS_MAX_BAN_HOURS`
     instead, since the kernel's list doesn't survive a reboot.
   - **Where it's recorded:**
     - one `flood-ban` event with the counts;
     - a `crowdsec-decision` event per address as the decisions appear;
     - the summary's "Recorded / banned" line;
     - every source address in the attached report.
   - **Off switch:** the same as step 5 (`attack_permaban`).

**Safeguards against spoofing and false positives.** A UDP source address is whatever the sender
wrote, so:

- Known players (every confirmed address seen in 30 days) and trusted addresses are never banned
  automatically, and are let through the attack limits. Spoofing a player's address cannot get them
  banned.
- Outside a flood, when a minute has more than `ANTIDDOS_SPOOF_SOURCES` sources, the traffic is
  likely spoofed. Per-address bans are held and recorded as held, and the aggregate cap does the
  work. During a flood this guard is off (steps 5 and 6).
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
- **Packets that never reach the server cannot be logged.** If your host's upstream drops a flood,
  or the link is saturated before your network card, this server sees nothing, and the `loss`
  counters cannot count it either. Only your provider's own graphs show that traffic.
- **Peak packets/s** in `traffic.jsonl` comes from the attack monitor's one-second readings.
