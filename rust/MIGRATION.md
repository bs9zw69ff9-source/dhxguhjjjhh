# C# to Rust migration checklist

The Rust port of the police bot. The C# code in `dotnet/` is the reference: where this
file, the docs and the C# disagree, the C# wins. The plan these milestones come from is
summarised here so the checklist stands on its own.

## Status

| Milestone | Scope | State |
|---|---|---|
| M0 | Inventory, workspace, gate, parity tool | done |
| M0b | Remove the Fallout deployment from the repo | done: `deploy.sh` restarts the systemd unit `pavlov-bot` (only if it runs this checkout's build), pm2 fallout files and paths gone |
| M0c | Remove the fallout theme from the C# | done: police is the only skin and faction set; `THEME=fallout` and `FACTION_SET=fallout` stop startup (exit 78) saying they were removed; fallout-data tests moved to police data |
| M1 | `pavlov-config`; `pavlov-core` part 1: Text, Time, Security, Concurrency, Data, Penal, Evasion, Factions, Economy, Licensing, Stats | done: `pavlov-config` (the dotenv grammar, the environment over the `.env`, `BotOptions`, `MonitoringOptions`, `RconOptions`, `FeatureOptions` with its startup summary, the `.env` permission check) and in `pavlov-core` Text, Time, Moderation (staff hierarchy, ban rules, menu link, menu roles), Verification, Stats, Economy ledger, KeyedLock, the JSON timestamp and tier compatibility, Evasion (scorer, flag registry), Penal, Factions (police set), Intelligence (risk scoring, profile redaction), Security (owner guard, `Secret`), Licensing, the roster write guard, `Data.SerializedStore` (with `IgnoreCaseMap` and an in-memory backend), the pure server types (unit names, install discovery, unit matching, crash limits), `MonitorSettings`, `VpnThresholds`, `VpnKeys`; fourteen C# test files fully ported, nine partly (the rest of each needs a later crate) |
| M2 | `pavlov-core` part 2: Logs, Monitoring, Intelligence, Provisioning, Servers, Sync, Verification, Vpn, Events | done: every file in `PavlovBot.Core` is ported. The Logs, StatsLog, KillLine and KillDataBlock tests still listed need the host feed and stats services and port with M6 |
| M3 | `pavlov-rcon` and the byte-recording fake RCON server | done: the session (gated, paced, keepalive, drop-safe dirty flag), the client (read cache, coalescing on a spawned shared read, retry that never re-sends a mutation that may have run, reply correlation, the bot's-own-traffic memory), reply parsing, the roster and lifecycle traits, and `testing::FakeRconServer` recording the exact login and command bytes. The registry moved to M6: it needs the metrics and the Pavlov.log confirmations |
| M4 | `pavlov-storage`: SQLite kv and events, files, atomic writes, game files | in progress: atomic writes (owner and mode carried over), the file and SQLite key-value backends, the dataset names and seeds |
| M5 | `pavlov-host`: systemd, ufw, provisioning, delete, switch, supervisor, crash recovery | not started |
| M6 | `pavlov-services`: moderation, factions, economy, logs, monitoring, metrics, VPN, verification, the 30 services | not started |
| M7 | `pavlov-discord` core: gateway, dispatcher, access, theme, registration, boards, feeds | not started |
| M8 | The 57 slash commands | not started |
| M9 | `pavlov-bot` composition root and `--selftest` | not started |
| M10 | Deploy script, police systemd unit, rollback, documentation | not started |
| M11 | Delete the C# code, after the owner confirms the Rust bot live | not started |

Nothing in `rust/` is deployed. `scripts/deploy.sh` builds and runs the C# bot until the
cutover in M10, which the owner runs.

## Owner decisions (2026-11-17)

1. **Plugins are dropped**, from the C# as well (2026-11-17): no plugin was ever deployed,
   and a Rust binary cannot load .NET assemblies. There is no `pavlov-plugins` crate and no
   `/plugins` command, so the bot has 57 commands. `PLUGIN_DIR`, `PLUGINS_ENABLED` and `PLUGINS_DISABLED` still parse so an
   existing `.env` is accepted; if any is set, or `plugins/` holds files, startup logs one
   warning that plugins are not supported.
2. **The RCON firewall stays as it is.** Provisioning runs plain `ufw allow <port>` for the
   game, query and RCON ports, as the C# does. RCON is therefore reachable from anywhere,
   protected by its password only. Recorded as a known risk, not changed.
3. **The Fallout bot is dropped completely**, deployment and theme, in the C# as well (M0c).
   Both bots are police-only: `THEME` defaults to `police`, and `THEME=fallout` or
   `FACTION_SET=fallout` is a configuration error that says it was removed. The C# tests
   that exercised a mechanism (ranks, sub-classes) through fallout data now use police data,
   and the fallout-only ones were deleted with the theme.
4. **Each milestone is squash-merged to `main`** when its gate is green. `rust/` is inert
   until the cutover.

## Crate map

| Crate | Takes |
|---|---|
| `pavlov-core` | all of `PavlovBot.Core`: pure rules, no I/O. Also the PURE parts of some Host types that configuration needs, because `pavlov-config` sits below `pavlov-host`: unit-name parsing (`ServiceControl.ParseUnits`), the install-discovery decision (`PavlovInstalls.Discover`, behind the `servers::HostProbe` trait that `pavlov-host` implements), install-to-unit matching, the crash-recovery limits, `VpnKeys`, and `Secret` |
| `pavlov-config` | `Host/Configuration`: the dotenv parser and the typed options |
| `pavlov-rcon` | `PavlovBot.Rcon`, and the `IOnlineRoster` / `IServerLifecycle` seams from `Host/Rcon`. `RconRegistry` is in `pavlov-services`, beside the metrics and the log confirmations it uses |
| `pavlov-storage` | `Host/Storage`, `Host/Events/SqliteEventStore`, `Host/Economy/LedgerFileStore` |
| `pavlov-host` | `Host/Servers`: systemd, ufw, provisioning, process running |
| `pavlov-services` | the rest of `Host`: moderation, factions, economy, logs, monitoring, observability, VPN, verification, intelligence, the background service host |
| `pavlov-discord` | `Host/Discord` and its commands |
| `pavlov-bot` | `Host/Program.cs`: the binary |

## Facts the port must keep (verified in the C# source)

- **RCON is Pavlov's text protocol, not Source RCON.** The server sends `Password: `, the
  client answers with the lowercase hex MD5 of the password, the server answers
  `Authenticated=1\r\n` (or `=0`). A command is the text plus `\n`; the reply is JSON or text,
  decoded with a streaming UTF-8 decoder and checked for JSON completeness. One persistent
  session per server, commands paced 100 ms apart. See `PavlovBot.Rcon/RconConnection.cs`.
- **Storage is SQLite plus files.** `kv(file TEXT PRIMARY KEY, data TEXT NOT NULL)` holds one
  JSON document per dataset (names in `Storage/Datasets.cs`); `events` is a second table with
  five indexes. The JSON shape is the Node bot's (camelCase, millisecond timestamps) and is a
  live contract.
- **Server slots are positional.** Entry N of `PAVLOV_UNITS` is server N; a leftover
  `docker:` prefix is stripped in place so numbering never shifts.

## Dead code removed before porting (2026-11-17)

Found by checking every Core type against the Host: each was referenced only by its own
tests, so it ran nowhere. Removed from the C# and from the Rust port, with its tests.

| Removed | Why it was dead |
|---|---|
| `Core/Events/Incidents.cs` (`IncidentDetector`) | superseded by `Core/Monitoring`; nothing called it |
| `Core/Events/StaffAnomaly.cs` | `/staff` never used it; its description promised the check anyway, now corrected |
| `Core/Economy/FraudSignals.cs` | no caller |
| `Core/Stats/PeakTracker.cs` | no caller; the `server_stats` dataset name stays, it is a stored-file contract |
| `Host/Plugins`, `/plugins` | no plugin ever deployed; settings now produce one startup warning |
| `tools/DiffCheck` | replayed cases against the Node bot, which is gone |
| `tools/RconBench` | Node-versus-.NET connection benchmark; the source of the build's only warning |

## Behaviour differences from the C# (deliberate, each a fix)

| Where | C# | Rust | Why |
|---|---|---|---|
| `BanRecord.playerId` missing in stored JSON | `required`: the whole ban list fails to read | that record reads as invalid and is skipped | a list that fails to read is treated as empty and overwritten on the next ban |
| Stored `tier` outside 0 to 5, or quoted | any integer kept; a quoted tier fails the list | clamped into range; a quoted number reads | same failure mode as above; the Node bot accepted quoted tiers |
| Unknown fields in stored records | dropped on the next write | kept (`#[serde(flatten)] extra`) | never silently lose data a newer build wrote |
| `null` where a stored list belongs | let through, fails on first use | reads as empty | |
| `Ledger::credit` overflow | `long` wraps to a huge negative balance | vetoed | |
| `parse_clock_time` with a non-ASCII digit | `FormatException` | `None` | |
| New York time zone data | host tzdata, throws if missing | compiled in (`chrono-tz`) | nothing to be missing |
| Evasion matches with equal scores | `List.Sort`, unstable: tie order unspecified | stable: ties keep ban order | deterministic output |
| `F0`/`F1` number formatting | .NET rounds midpoints away from zero | the same, explicitly (`text::number::fixed`); Rust's own `{:.1}` would round half to even | identical text |
| A stored map whose keys differ only by case | `ReadMap` throws (`ArgumentException` copying into an `OrdinalIgnoreCase` dictionary) | merged into one `IgnoreCaseMap` entry: first spelling, last value | the old ordinal-read bug could leave exactly such a row, and throwing made the whole dataset unreadable |
| Case-insensitive datasets | the caller must remember `ReadMap`/`UpdateMapAsync`; a plain `Read` comes back ordinal | the comparer is the type (`text::case::IgnoreCaseMap`), so there is no `read_map` to forget | the bug cannot be written |
| Non-ASCII text in written JSON | System.Text.Json escapes it (`\u00E9`) | written as UTF-8 | identical once parsed; both bots read either |
| Value reported after a vetoed or failed update | the value the mutator was handed, possibly half-changed | the value still stored (re-read) | `UpdateResult.value` says what is in the store |
| Logging a value that holds a token, password, API key or webhook | a record's generated `ToString` prints it | `Secret` prints `***`; the value is only reachable through `expose()` | a debug log line must never leak a credential |
| `EVENT_RETENTION_DAYS=0` | read as unset: 90 days kept, so the documented "0 turns the timeline off" never worked | 0 turns the timeline off; negative or nonsense is the default | the documented off switch works |
| An interval of `Infinity`, `NaN` or `1e300` ms | `TimeSpan` throws `OverflowException` and the bot dies at startup | the setting's default | a typo is not an outage |
| The installs and systemd units at startup | `PavlovInstalls.Discover` and the systemd scan run up to three times | once each; every decision in one bind sees the same machine | |
| `METRICS_PORT=0` message | ends "the .NET listener needs a fixed port" | ends "the health check and the watchdog need to know where to find it" | the .NET reason does not apply; port 0 is still refused |
| A provisioning value with one trailing newline (RCON password, server key, map id, install dir) | accepted: .NET's `$` also matches before a final `\n`, so the line it was meant to protect splits | refused: the patterns match to the very end | the charset rule exists to keep each value on one line |
| A line break in the game password | not checked, written into Game.ini as a second line | refused by validation | it would add a setting of its own |
| `UGC` map ids with non-ASCII digits | accepted (.NET `\d` is any Unicode digit) | ASCII digits only | no workshop id has any other kind |
| Length caps on event fields and the server name | counted in UTF-16 units, and a cut can split a surrogate pair | counted in characters, cut on a character boundary | never writes half a character |
| `RconOptions.MaxAttempts = 0` | the loop never runs: every command fails "after 0 attempt(s)" without trying | at least one attempt | not configurable from `.env`, so only reachable by a code change |
| A caller abandoning an RCON exchange | the session is dropped in a `finally` when the exchange did not settle | the session is marked dirty before the write and clean after the read; a dropped future leaves the mark, and the next command reconnects | the same rule, but drop-safe by construction |
| A dataset file the file backend cannot read (permissions, a directory in its place) | read as absent, so the next update wrote the fallback over the real file | an error: the store refuses the update and reports it | absent and unreadable are opposite instructions |
| A dataset that no longer parses, at JSON export | exported as `<key>.json.corrupt` only when the store had flagged it | also when the stored text itself no longer parses | the last good backup is never replaced by damaged text |
| Autocomplete-tag stripping | hand-written pattern kept in step with `NameLabels` by a test | pattern built from `NameLabels::ALL` | the two cannot drift |

Open question for M4: the C# reads stored keys case-INSENSITIVELY; the Rust models read the
camelCase the Node bot and the C# both write. Before cutover, the stored documents in a copy
of the live `bot.db` are scanned for any non-camelCase key (a tool for that ships with M4).

## How to check progress

```bash
python3 rust/tools/parity.py           # counts, and fails if a file marked ported is incomplete
python3 rust/tools/parity.py --list    # every C# test not yet in Rust
python3 rust/tools/parity.py --strict  # the completion gate: nothing unaccounted
python3 rust/tools/inventory.py        # regenerate the tables below from the C# source
bash rust/tools/gate.sh                # the merge gate: fmt, clippy -D warnings, test, release build, parity
```

## Inventory

<!-- BEGIN GENERATED -->

_Generated by `rust/tools/inventory.py` from the C# source. Do not edit by hand; edit `rust/tools/status.toml` and re-run._

### Slash commands (57)

| Command | Description (C#) | C# file | Rust |
|---|---|---|---|
| `/alts` | Moderation - Accounts that share an address with this player | AltsCommand.cs | pending |
| `/announce` | Mod - Broadcast a message to the server | UtilityCommands.cs | pending |
| `/arrest` | Police - Book a player on penal-code charges | PoliceCommands.cs | pending |
| `/backgroundcheck` | Police - Pull a player's record | PoliceCommands.cs | pending |
| `/bail` | Mod - Bail prices and the sentence cap | PoliceCommands.cs | pending |
| `/banlist` | Show every ban currently in force | StaffCommands.cs | pending |
| `/botstats` | Owner - CPU and memory for the bot and each game server | OwnerCommands.cs | pending |
| `/checkban` | Check whether a player is banned | BanCommands.cs | pending |
| `/configure` | Owner - The control panel: blacklists, access, tracking, wipes | ConfigPanel.cs | pending |
| `/counts` | Mod - Update the player-count voice channels now | CountsCommand.cs | pending |
| `/deleteserver` | Owner - Delete a server: its service, its install directory and its slot (needs root) | DeleteServerCommand.cs | pending |
| `/donator` | Player ID or username | ConfigCommands.cs | pending |
| `/errors` | Owner - Recent command failures, with the stack trace | ErrorsCommand.cs | pending |
| `/factionstats` | Which faction | IntelligenceCommands.cs | pending |
| `/feeds` | Mod - Check whether the join, connect, kill and money webhooks are working | FeedsCommand.cs | pending |
| `/firewall` | Owner - Manual OS firewall (ufw) control. Bans are also denied automatically. | OwnerCommands.cs | pending |
| `/flush` | Which server to free a slot on | StaffCommands.cs | pending |
| `/givemenu` | Admin - Grant RCON menu access to a player | MenuCommands.cs | pending |
| `/guildinvites` | Owner - DM the super owner an invite to every Discord server this bot is in | GuildInvitesCommand.cs | pending |
| `/health` | Owner - Bot health and background services | UtilityCommands.cs | pending |
| `/help` | Show the available commands and your access level | UtilityCommands.cs | pending |
| `/inspect` | Owner - Everything the bot knows about a player | OwnerCommands.cs | pending |
| `/iplookup` | Owner - Look up an IP: location, ISP and VPN status | AdminCommands.cs | pending |
| `/kick` | Mod - Kick a player | UtilityCommands.cs | pending |
| `/license` | Which license to take away | LicenseCommand.cs | pending |
| `/manual` | Which server | OwnerCommands.cs | pending |
| `/monitor` | Which server (default: all) | ServerStatusCommand.cs | pending |
| `/permban` | Admin - Ban a player permanently | BanCommands.cs | pending |
| `/players` | Who is online right now | PlayersCommand.cs | pending |
| `/provisionserver` | Owner - Install a new Pavlov server, configure it and wire it into this bot (needs root) | ProvisionServerCommand.cs | pending |
| `/removepin` | Which server to unlock | AdminCommands.cs | pending |
| `/rotatemap` | Which server to rotate. Defaults to all of them. | RotateMapCommand.cs | pending |
| `/server` | Browse the public Pavlov server list | ServerLookupCommand.cs | pending |
| `/serverinfo` | Live server info - map, mode and who is online. | ServerInfoCommand.cs | pending |
| `/serverswitch` | Which server | ServerSwitchCommand.cs | pending |
| `/setpin` | Which server to lock | AdminCommands.cs | pending |
| `/setrconroles` | Owner - Map Discord roles to RCON menu tiers | ConfigCommands.cs | pending |
| `/setroles` | Owner - Map Discord roles to the bot's permission tiers | ConfigCommands.cs | pending |
| `/staff` | Admin - Staff activity over a period, busiest first | AnalyticsCommands.cs | pending |
| `/staffactivity` | Admin - Audit one staff member's actions | StaffCommands.cs | pending |
| `/staffleaderboard` | Admin - Rank staff by moderation actions | StaffCommands.cs | pending |
| `/stats` | Playtime, K/D and faction for a player | StatsCommand.cs | pending |
| `/stripmenu` | Admin - Remove a member's RCON menu access | StaffCommands.cs | pending |
| `/subclass` | Which sub-class | StaffCommands.cs | pending |
| `/subclasses` | Which faction | FactionCommands.cs | pending |
| `/suspendrank` | Mod - Pull a member's whitelist rank for a set time; it auto-restores | ConfigCommands.cs | pending |
| `/tempban` | How long the ban lasts | BanCommands.cs | pending |
| `/testalert` | Owner - Send a test security alert and report who received it | TestAlertCommand.cs | pending |
| `/tester` | In-game username, exactly as it appears | TesterCommand.cs | pending |
| `/testmode` | Which server | TestModeCommand.cs | pending |
| `/unban` | Mod - Lift a ban | BanCommands.cs | pending |
| `/unlinkname` | Admin - Break a member's permanent in-game name binding | MenuCommands.cs | pending |
| `/vpncheck` | Owner - Probe every VPN detector: which are up and each one's role | AdminCommands.cs | pending |
| `/wages` | What an on-duty officer has earned, banked and unbanked | EconomyCommands.cs | pending |
| `/warn` | Player ID or username | WarnCommand.cs | pending |
| `/warrant` | Player ID or username | PoliceCommands.cs | pending |
| `/whitelist` | Which faction | FactionCommands.cs | pending |

### Background services (30)

| Service | Rust |
|---|---|
| `rcon-health` | pending |
| `player-cache` | pending |
| `server-monitor` | pending |
| `monitor-board` | pending |
| `runtime-metrics` | pending |
| `log-tail` | pending |
| `stats-log` | pending |
| `ban-expiry` | pending |
| `ban-firewall` | pending |
| `timeline-retention` | pending |
| `ban-sweep` | pending |
| `ban-reconcile` | pending |
| `payroll` | pending |
| `kill-stats` | pending |
| `playtime` | pending |
| `leaderboard` | pending |
| `arrest-board` | pending |
| `kd-board` | pending |
| `warrant-board` | pending |
| `player-board` | pending |
| `player-count-channels` | pending |
| `crash-recovery` | pending |
| `whitelist-backup` | pending |
| `rank-restore` | pending |
| `banlist-sync` | pending |
| `modsave-sync` | pending |
| `verify-panel` | pending |
| `menu-panel` | pending |
| `json-export` | pending |
| `service-supervisor` | pending |

### Datasets (47)

| C# constant | Stored name | Rust |
|---|---|---|
| `TempBans` | `tempbans` | pending |
| `ModLog` | `modlog` | pending |
| `Mutes` | `mutes` | pending |
| `UserBlacklist` | `user_blacklist` | pending |
| `UserUnbarred` | `user_unbarred` | pending |
| `IpFlags` | `ip_flags` | pending |
| `IgnoredNames` | `ignored_names` | pending |
| `AutobanExempt` | `autoban_exempt` | pending |
| `NeverBan` | `never_ban` | pending |
| `MasterPardons` | `master_pardons` | pending |
| `MasterGrants` | `master_grants` | pending |
| `FirewallBans` | `firewall_bans` | pending |
| `Warnings` | `warnings` | pending |
| `BanReconcileState` | `ban_reconcile_state` | pending |
| `MonitorHistory` | `monitor_history` | pending |
| `UnbanTombstones` | `unban_tombstones` | pending |
| `VpnChecks` | `vpn_checks` | pending |
| `ServerLock` | `server_lock` | pending |
| `FactionRanks` | `faction_ranks` | pending |
| `FactionConfig` | `faction_config` | pending |
| `FactionAudit` | `faction_audit` | pending |
| `FactionBackup` | `faction_backup` | pending |
| `Warrants` | `warrants` | pending |
| `Arrests` | `arrests` | pending |
| `Sentences` | `sentences` | pending |
| `PoliceConfig` | `police_config` | pending |
| `RankSuspensions` | `rank_suspensions` | pending |
| `Playtime` | `playtime` | pending |
| `KillStats` | `killstats` | pending |
| `LastSeen` | `lastseen` | pending |
| `KnownPlayers` | `known_players` | pending |
| `DiscordLinks` | `discord_links` | pending |
| `Verifications` | `verifications` | pending |
| `FactionMembers` | `faction_members` | pending |
| `VerifyState` | `verify_state` | pending |
| `DonatorSuspend` | `donator_suspend` | pending |
| `Wages` | `wages` | pending |
| `PayrollState` | `payroll_state` | pending |
| `MenuGrants` | `menu_grants` | pending |
| `MenuPanel` | `menu_panel` | pending |
| `MenuRoles` | `menu_roles` | pending |
| `MenuLinks` | `menu_links` | pending |
| `Roles` | `roles` | pending |
| `AutoRotate` | `autorotate` | pending |
| `ServerStats` | `server_stats` | pending |
| `AutopostState` | `autopost_state` | pending |
| `UpdateLogState` | `update_log_state` | pending |

### Environment variables (131)

"Read in" is a scan for the literal name. `(.env.example only)` means no literal read was found: either the name is built at run time (`RCON_HOST_{n}`) or the C# never reads it (`APPEAL_LINK`). Each one is checked by hand when `pavlov-config` is ported.

| Variable | In .env.example | Read in | Rust |
|---|---|---|---|
| `APPEAL_LINK` | yes | (.env.example only) | pending |
| `ARREST_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `ARREST_LEADERBOARD_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `BAN_LOG_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `BLACKLIST_IDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `BLACKLIST_PATH` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `BLACKLIST_SYNC` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `BOT_NAME` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `BUILD_ID` | yes | (.env.example only) | pending |
| `CLIENT_ID` | yes | (.env.example only) | pending |
| `COMMANDS_DISABLED` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `COMMAND_LOG_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `CONNECT_WEBHOOK_URL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/FeedsCommand.cs | pending |
| `CRASH_RECOVERY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `DATA_DIR` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `DB_EXPORT_INTERVAL_MS` | yes | (.env.example only) | pending |
| `DISCORD_TOKEN` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `DONATOR_PATH` | yes | (.env.example only) | pending |
| `EVENT_RETENTION_DAYS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `FACTIONS_PATH` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `FACTION_BOT_TOKEN` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `FACTION_CLIENT_ID` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `FACTION_ROLES_PATH` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `FACTION_SET` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `FIREWALL_BANS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `FIREWALL_BLACKLIST` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `FIREWALL_NEVER_BLOCK` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `GEOAPIFY_API_KEY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `GUILD_ID` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `HOME_GUILD_ID` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `ID_LIKE` | no | PavlovBot.Host/Servers/ServerProvisioner.cs | pending |
| `IGNORE_PATHS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `INVOCATION_ID` | no | PavlovBot.Host/Servers/BotSupervisor.cs | pending |
| `IPAPIIS_KEY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `IPHUB_API_KEY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `IPINFO_TOKEN` | yes | (.env.example only) | pending |
| `IPQS_API_KEY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `JOIN_WEBHOOK_URL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/FeedsCommand.cs | pending |
| `KD_LEADERBOARD_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `KILL_WEBHOOK_URL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/FeedsCommand.cs | pending |
| `LEADERBOARD_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `LEADERBOARD_INTERVAL_MS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `LOG_LEVEL` | yes | PavlovBot.Host/Program.cs | pending |
| `MASTER_NAMES` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MENU_PANEL_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MENU_ROLE_BLACKLIST` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MENU_ROLE_HIGHSTAFF` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MENU_ROLE_STAFF` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `METRICS_HOST` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `METRICS_PORT` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `METRICS_TOKEN` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `MODSAVE_BLACKLIST_PATH` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MODSAVE_PATH` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MODSAVE_SYNC` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MODSAVE_SYNC_SKIP_EXTRA` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MOD_LOG_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITORING` | yes | (.env.example only) | pending |
| `MONITOR_ALERT_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_ALERT_ROLE` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_BOARD` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_CHECK_INTERVAL_SECONDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_ESCALATION_EVERY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_FAILURE_THRESHOLD` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_LATENCY_CRITICAL_MS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_LATENCY_WARN_MS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_LOG_INACTIVITY_MINUTES` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_PLAYER_DROP_PERCENT` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_PLAYER_WINDOW_SECONDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_RCON_TIMEOUT_SECONDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_RECOVERY_MESSAGES` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_RECOVERY_THRESHOLD` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_RETRY_BASE_SECONDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `MONITOR_RETRY_MAX_SECONDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `OWNER_IDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PAVLOV_BASES` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/DeleteServerCommand.cs, PavlovBot.Host/D | pending |
| `PAVLOV_BASE_1` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/ProvisionServerCommand.cs, PavlovBot.Hos | pending |
| `PAVLOV_LOGS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PAVLOV_PM2_APP` | no | PavlovBot.Host/Servers/BotSupervisor.cs | pending |
| `PAVLOV_SYSTEMCTL_SUDO` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PAVLOV_SYSTEMD_UNIT` | no | PavlovBot.Host/Servers/BotSupervisor.cs | pending |
| `PAVLOV_UNITS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/DeleteServerCommand.cs, PavlovBot.Host/D | pending |
| `PAVLOV_VERSION` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PAYROLL_AMOUNT` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PAYROLL_FACTION` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PAYROLL_INTERVAL_MINUTES` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PLAYERLIST_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PLAYER_COUNT_CHANNELS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PLUGINS_ALLOW_ROOT` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PLUGINS_DISABLED` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PLUGINS_ENABLED` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PLUGIN_DIR` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `POLICE_LOG_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `PRETTY_NAME` | no | PavlovBot.Host/Servers/ServerProvisioner.cs | pending |
| `PROXYCHECK_API_KEY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `RCON_HEALTH_INTERVAL_MS` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `RCON_HOST_1` | yes | (.env.example only) | pending |
| `RCON_HOST_2` | yes | (.env.example only) | pending |
| `RCON_HOST_3` | yes | (.env.example only) | pending |
| `RCON_HOST_{i}` | no | PavlovBot.Host/Configuration/BotOptions.cs, PavlovBot.Host/Discord/Commands/DeleteServerCommand.cs, PavlovBot.Host/Disco | pending |
| `RCON_PASSWORD_1` | yes | (.env.example only) | pending |
| `RCON_PASSWORD_2` | yes | (.env.example only) | pending |
| `RCON_PASSWORD_3` | yes | (.env.example only) | pending |
| `RCON_PASSWORD_{i}` | no | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `RCON_PORT_1` | yes | (.env.example only) | pending |
| `RCON_PORT_2` | yes | (.env.example only) | pending |
| `RCON_PORT_3` | yes | (.env.example only) | pending |
| `RCON_PORT_{i}` | no | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `RCON_READ_CACHE_MS` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `RCON_WEBHOOK_URL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/FeedsCommand.cs | pending |
| `SECURITY_DM_IDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `SENTINEL_API_KEY` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `SERVER_MAX_PLAYERS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `SHACK_TOTAL_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `STAFF_WEBHOOK_URL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs, PavlovBot.Host/Discord/Commands/FeedsCommand.cs | pending |
| `STATS_LOGS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `SUPER_OWNER_IDS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `THEME` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `UFW_BLOCK` | yes | (.env.example only) | pending |
| `UPDATE_LOG_CHANNEL` | yes | (.env.example only) | pending |
| `USER_APP` | yes | PavlovBot.Host/Configuration/BotOptions.cs | pending |
| `VERIFIED_ROLE` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VERIFY_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VERIFY_STAFF_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VERSION_ID` | no | PavlovBot.Host/Servers/ServerProvisioner.cs | pending |
| `VPN_AUTOBAN` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VPN_BAN_MIN` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VPN_CACHE_TTL_DAYS` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VPN_CONFIRM_MIN` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VPN_SCREEN_BAN_MIN` | no | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `VPN_SCREEN_MIN` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |
| `WARRANT_BOARD_CHANNEL` | yes | PavlovBot.Host/Configuration/FeatureOptions.cs | pending |

### C# source files (206, 48,311 lines)

| File | Lines | Rust crate |
|---|---|---|
| `PavlovBot.Core/Concurrency/KeyedLock.cs` | 86 | pavlov-core |
| `PavlovBot.Core/Data/RosterWriteGuard.cs` | 115 | pavlov-core |
| `PavlovBot.Core/Data/SerializedStore.cs` | 242 | pavlov-core |
| `PavlovBot.Core/Economy/Ledger.cs` | 109 | pavlov-core |
| `PavlovBot.Core/Evasion/EvasionModels.cs` | 153 | pavlov-core |
| `PavlovBot.Core/Evasion/EvasionScorer.cs` | 131 | pavlov-core |
| `PavlovBot.Core/Evasion/IpRegistry.cs` | 184 | pavlov-core |
| `PavlovBot.Core/Events/EventMapping.cs` | 97 | pavlov-core |
| `PavlovBot.Core/Events/ServerEvent.cs` | 138 | pavlov-core |
| `PavlovBot.Core/Factions/FactionActivity.cs` | 127 | pavlov-core |
| `PavlovBot.Core/Factions/FactionRegistry.cs` | 188 | pavlov-core |
| `PavlovBot.Core/Factions/FactionSet.cs` | 160 | pavlov-core |
| `PavlovBot.Core/Factions/MembershipRules.cs` | 208 | pavlov-core |
| `PavlovBot.Core/Intelligence/Addresses.cs` | 58 | pavlov-core |
| `PavlovBot.Core/Intelligence/PlayerProfile.cs` | 249 | pavlov-core |
| `PavlovBot.Core/Intelligence/RiskModels.cs` | 127 | pavlov-core |
| `PavlovBot.Core/Intelligence/RiskScorer.cs` | 191 | pavlov-core |
| `PavlovBot.Core/Licensing/License.cs` | 31 | pavlov-core |
| `PavlovBot.Core/Logs/JoinCorrelator.cs` | 111 | pavlov-core |
| `PavlovBot.Core/Logs/PavlovLogLine.cs` | 373 | pavlov-core |
| `PavlovBot.Core/Logs/StatsLog.cs` | 202 | pavlov-core |
| `PavlovBot.Core/Moderation/BanRecord.cs` | 212 | pavlov-core |
| `PavlovBot.Core/Moderation/MenuLink.cs` | 62 | pavlov-core |
| `PavlovBot.Core/Moderation/StaffHierarchy.cs` | 76 | pavlov-core |
| `PavlovBot.Core/Monitoring/MonitorTypes.cs` | 186 | pavlov-core |
| `PavlovBot.Core/Monitoring/ServerHealth.cs` | 362 | pavlov-core |
| `PavlovBot.Core/Penal/PenalCode.cs` | 305 | pavlov-core |
| `PavlovBot.Core/Provisioning/EnvFileEditor.cs` | 91 | pavlov-core |
| `PavlovBot.Core/Provisioning/ProvisionText.cs` | 348 | pavlov-core |
| `PavlovBot.Core/Provisioning/ProvisionValidation.cs` | 167 | pavlov-core |
| `PavlovBot.Core/Provisioning/ServerPortDefaults.cs` | 58 | pavlov-core |
| `PavlovBot.Core/Provisioning/ServerProvisionSpec.cs` | 57 | pavlov-core |
| `PavlovBot.Core/Provisioning/ServerSlotPlanner.cs` | 136 | pavlov-core |
| `PavlovBot.Core/Security/OwnerGuard.cs` | 149 | pavlov-core |
| `PavlovBot.Core/Servers/ServerListing.cs` | 190 | pavlov-core |
| `PavlovBot.Core/Sync/LedgerFile.cs` | 51 | pavlov-core |
| `PavlovBot.Core/Sync/ModSaveFile.cs` | 145 | pavlov-core |
| `PavlovBot.Core/Sync/ModSaveSyncPlanner.cs` | 105 | pavlov-core |
| `PavlovBot.Core/Text/Currency.cs` | 27 | pavlov-core |
| `PavlovBot.Core/Text/NameLabels.cs` | 46 | pavlov-core |
| `PavlovBot.Core/Text/Sanitize.cs` | 151 | pavlov-core |
| `PavlovBot.Core/Time/EasternTime.cs` | 265 | pavlov-core |
| `PavlovBot.Core/Verification/VerificationRules.cs` | 82 | pavlov-core |
| `PavlovBot.Core/Vpn/DetectorReading.cs` | 114 | pavlov-core |
| `PavlovBot.Core/Vpn/IpAddresses.cs` | 74 | pavlov-core |
| `PavlovBot.Core/Vpn/SentinelParser.cs` | 207 | pavlov-core |
| `PavlovBot.Core/Vpn/VpnConsensus.cs` | 383 | pavlov-core |
| `PavlovBot.Core/Vpn/VpnVerdict.cs` | 129 | pavlov-core |
| `PavlovBot.Host/Configuration/BotOptions.cs` | 252 | pavlov-config |
| `PavlovBot.Host/Configuration/DotEnvConfiguration.cs` | 115 | pavlov-config |
| `PavlovBot.Host/Configuration/FeatureOptions.cs` | 778 | pavlov-config |
| `PavlovBot.Host/Discord/Access.cs` | 670 | pavlov-discord |
| `PavlovBot.Host/Discord/AutoPost.cs` | 169 | pavlov-discord |
| `PavlovBot.Host/Discord/Autocomplete.cs` | 113 | pavlov-discord |
| `PavlovBot.Host/Discord/Boards.cs` | 517 | pavlov-discord |
| `PavlovBot.Host/Discord/ChannelStaffLog.cs` | 166 | pavlov-discord |
| `PavlovBot.Host/Discord/CommandCatalog.cs` | 27 | pavlov-discord |
| `PavlovBot.Host/Discord/CommandLog.cs` | 172 | pavlov-discord |
| `PavlovBot.Host/Discord/CommandOptionName.cs` | 79 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/AdminCommands.cs` | 378 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/AltsCommand.cs` | 136 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/AnalyticsCommands.cs` | 99 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ArrestBooking.cs` | 404 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/BanCommands.cs` | 652 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ConfigCommands.cs` | 589 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ConfigPanel.cs` | 311 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/CountsCommand.cs` | 131 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/DeleteServerCommand.cs` | 257 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/EconomyCommands.cs` | 225 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ErrorsCommand.cs` | 102 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/FactionCommands.cs` | 907 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/FeedsCommand.cs` | 133 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/GuildInvitesCommand.cs` | 144 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/IntelligenceCommands.cs` | 166 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/LicenseCommand.cs` | 156 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/MembershipReply.cs` | 51 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/MenuCommands.cs` | 284 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/MenuPanel.cs` | 316 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/OwnerCommands.cs` | 463 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/PlayersCommand.cs` | 102 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/PoliceCommands.cs` | 500 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ProvisionServerCommand.cs` | 485 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/RconFailure.cs` | 62 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/RotateMapCommand.cs` | 131 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ServerInfoCommand.cs` | 158 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ServerLookupCommand.cs` | 410 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ServerStatusCommand.cs` | 220 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/ServerSwitchCommand.cs` | 231 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/StaffCommands.cs` | 541 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/StatsCommand.cs` | 157 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/TestAlertCommand.cs` | 98 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/TestModeCommand.cs` | 212 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/TesterCommand.cs` | 223 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/UtilityCommands.cs` | 318 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/WarnCommand.cs` | 269 | pavlov-discord |
| `PavlovBot.Host/Discord/Commands/WhitelistWipe.cs` | 170 | pavlov-discord |
| `PavlovBot.Host/Discord/ConnectCard.cs` | 250 | pavlov-discord |
| `PavlovBot.Host/Discord/DirectMessageAlerts.cs` | 152 | pavlov-discord |
| `PavlovBot.Host/Discord/DiscordGateway.cs` | 1197 | pavlov-discord |
| `PavlovBot.Host/Discord/EmbedBudget.cs` | 97 | pavlov-discord |
| `PavlovBot.Host/Discord/FeedWebhooks.cs` | 502 | pavlov-discord |
| `PavlovBot.Host/Discord/GatewayAutoPostTarget.cs` | 113 | pavlov-discord |
| `PavlovBot.Host/Discord/GatewayChannelRenamer.cs` | 109 | pavlov-discord |
| `PavlovBot.Host/Discord/GatewayGuildDirectory.cs` | 113 | pavlov-discord |
| `PavlovBot.Host/Discord/HomeGuildMembers.cs` | 161 | pavlov-discord |
| `PavlovBot.Host/Discord/IComponentHandler.cs` | 114 | pavlov-discord |
| `PavlovBot.Host/Discord/IGuildDirectory.cs` | 40 | pavlov-discord |
| `PavlovBot.Host/Discord/ISlashCommand.cs` | 37 | pavlov-discord |
| `PavlovBot.Host/Discord/InteractionDispatcher.cs` | 69 | pavlov-discord |
| `PavlovBot.Host/Discord/Lore.cs` | 93 | pavlov-discord |
| `PavlovBot.Host/Discord/Paged.cs` | 157 | pavlov-discord |
| `PavlovBot.Host/Discord/PlayerBoard.cs` | 253 | pavlov-discord |
| `PavlovBot.Host/Discord/PlayerCountChannels.cs` | 399 | pavlov-discord |
| `PavlovBot.Host/Discord/RconMenu.cs` | 75 | pavlov-discord |
| `PavlovBot.Host/Discord/Skin.cs` | 287 | pavlov-discord |
| `PavlovBot.Host/Discord/SlashCommandValidation.cs` | 156 | pavlov-discord |
| `PavlovBot.Host/Discord/SubcommandFilter.cs` | 149 | pavlov-discord |
| `PavlovBot.Host/Discord/Theme.cs` | 229 | pavlov-discord |
| `PavlovBot.Host/Economy/LedgerFileStore.cs` | 254 | pavlov-storage |
| `PavlovBot.Host/Economy/Payroll.cs` | 468 | pavlov-services |
| `PavlovBot.Host/Events/AnalyticsService.cs` | 135 | pavlov-services |
| `PavlovBot.Host/Events/EventRecorder.cs` | 185 | pavlov-services |
| `PavlovBot.Host/Events/PlayerEventBridge.cs` | 96 | pavlov-services |
| `PavlovBot.Host/Events/SqliteEventStore.cs` | 417 | pavlov-storage |
| `PavlovBot.Host/Factions/FactionMembers.cs` | 190 | pavlov-services |
| `PavlovBot.Host/Factions/FactionRenames.cs` | 142 | pavlov-services |
| `PavlovBot.Host/Factions/FactionsFile.cs` | 157 | pavlov-services |
| `PavlovBot.Host/Factions/RankRestorer.cs` | 130 | pavlov-services |
| `PavlovBot.Host/Factions/RosterService.cs` | 841 | pavlov-services |
| `PavlovBot.Host/Factions/WhitelistBackup.cs` | 137 | pavlov-services |
| `PavlovBot.Host/Intelligence/PlayerIntelligenceService.cs` | 384 | pavlov-services |
| `PavlovBot.Host/Logs/FeedBridge.cs` | 448 | pavlov-services |
| `PavlovBot.Host/Logs/IpTrackingService.cs` | 709 | pavlov-services |
| `PavlovBot.Host/Logs/LogTailer.cs` | 341 | pavlov-services |
| `PavlovBot.Host/Logs/RconConfirmations.cs` | 126 | pavlov-services |
| `PavlovBot.Host/Logs/ServerLabels.cs` | 85 | pavlov-services |
| `PavlovBot.Host/Logs/StatsLogService.cs` | 217 | pavlov-services |
| `PavlovBot.Host/Moderation/AuditLog.cs` | 150 | pavlov-services |
| `PavlovBot.Host/Moderation/BanFirewall.cs` | 289 | pavlov-services |
| `PavlovBot.Host/Moderation/BanService.cs` | 590 | pavlov-services |
| `PavlovBot.Host/Moderation/CommandBlacklist.cs` | 198 | pavlov-services |
| `PavlovBot.Host/Moderation/EvasionResponder.cs` | 354 | pavlov-services |
| `PavlovBot.Host/Moderation/IStaffLogSink.cs` | 33 | pavlov-services |
| `PavlovBot.Host/Moderation/MasterAccess.cs` | 402 | pavlov-services |
| `PavlovBot.Host/Moderation/MasterNames.cs` | 126 | pavlov-services |
| `PavlovBot.Host/Moderation/OwnerActions.cs` | 522 | pavlov-services |
| `PavlovBot.Host/Moderation/SecurityAlerts.cs` | 106 | pavlov-services |
| `PavlovBot.Host/Moderation/ServerBanFile.cs` | 511 | pavlov-services |
| `PavlovBot.Host/Moderation/VpnResponder.cs` | 340 | pavlov-services |
| `PavlovBot.Host/Moderation/WarningService.cs` | 209 | pavlov-services |
| `PavlovBot.Host/Monitoring/MonitorAlertSink.cs` | 150 | pavlov-services |
| `PavlovBot.Host/Monitoring/MonitorBoard.cs` | 189 | pavlov-services |
| `PavlovBot.Host/Monitoring/MonitorFormat.cs` | 68 | pavlov-services |
| `PavlovBot.Host/Monitoring/MonitorHistory.cs` | 119 | pavlov-services |
| `PavlovBot.Host/Monitoring/MonitorTargets.cs` | 29 | pavlov-services |
| `PavlovBot.Host/Monitoring/ServerLogActivity.cs` | 37 | pavlov-services |
| `PavlovBot.Host/Monitoring/ServerMonitor.cs` | 174 | pavlov-services |
| `PavlovBot.Host/Monitoring/ServerProbe.cs` | 107 | pavlov-services |
| `PavlovBot.Host/Observability/HealthRegistry.cs` | 163 | pavlov-services |
| `PavlovBot.Host/Observability/MetricsRegistry.cs` | 283 | pavlov-services |
| `PavlovBot.Host/Observability/MonitoringServer.cs` | 302 | pavlov-services |
| `PavlovBot.Host/Observability/OperationLog.cs` | 97 | pavlov-services |
| `PavlovBot.Host/Observability/RecentErrors.cs` | 82 | pavlov-services |
| `PavlovBot.Host/Program.cs` | 1508 | pavlov-bot |
| `PavlovBot.Host/Rcon/IOnlineRoster.cs` | 53 | pavlov-rcon |
| `PavlovBot.Host/Rcon/IServerLifecycle.cs` | 32 | pavlov-rcon |
| `PavlovBot.Host/Rcon/RconRegistry.cs` | 640 | pavlov-services |
| `PavlovBot.Host/Servers/BotSupervisor.cs` | 104 | pavlov-host |
| `PavlovBot.Host/Servers/CrashRecovery.cs` | 205 | pavlov-host |
| `PavlovBot.Host/Servers/Firewall.cs` | 141 | pavlov-host |
| `PavlovBot.Host/Servers/IServerProvisioner.cs` | 121 | pavlov-host |
| `PavlovBot.Host/Servers/IUnitControl.cs` | 42 | pavlov-host |
| `PavlovBot.Host/Servers/MapRotation.cs` | 77 | pavlov-host |
| `PavlovBot.Host/Servers/MasterServerList.cs` | 147 | pavlov-host |
| `PavlovBot.Host/Servers/PavlovServerDetection.cs` | 107 | pavlov-host |
| `PavlovBot.Host/Servers/PlayerNotice.cs` | 128 | pavlov-host |
| `PavlovBot.Host/Servers/PreservedPlayerData.cs` | 131 | pavlov-host |
| `PavlovBot.Host/Servers/ProcessRunner.cs` | 176 | pavlov-host |
| `PavlovBot.Host/Servers/ServerLayoutGate.cs` | 61 | pavlov-host |
| `PavlovBot.Host/Servers/ServerProvisioner.cs` | 1373 | pavlov-host |
| `PavlovBot.Host/Servers/ServiceControl.cs` | 573 | pavlov-host |
| `PavlovBot.Host/Services/BackgroundServiceHost.cs` | 690 | pavlov-services |
| `PavlovBot.Host/Services/ServiceRegistry.cs` | 594 | pavlov-services |
| `PavlovBot.Host/Stats/KillStats.cs` | 207 | pavlov-services |
| `PavlovBot.Host/Storage/ArrestConverter.cs` | 132 | pavlov-storage |
| `PavlovBot.Host/Storage/AtomicFile.cs` | 108 | pavlov-storage |
| `PavlovBot.Host/Storage/Datasets.cs` | 235 | pavlov-storage |
| `PavlovBot.Host/Storage/EpochTimeConverter.cs` | 104 | pavlov-storage |
| `PavlovBot.Host/Storage/FileKeyValueBackend.cs` | 130 | pavlov-storage |
| `PavlovBot.Host/Storage/GameDirectories.cs` | 31 | pavlov-storage |
| `PavlovBot.Host/Storage/GameFileGuard.cs` | 84 | pavlov-storage |
| `PavlovBot.Host/Storage/GameFiles.cs` | 97 | pavlov-storage |
| `PavlovBot.Host/Storage/ModSaveSync.cs` | 417 | pavlov-storage |
| `PavlovBot.Host/Storage/PavlovInstalls.cs` | 128 | pavlov-storage |
| `PavlovBot.Host/Storage/SqliteKeyValueBackend.cs` | 252 | pavlov-storage |
| `PavlovBot.Host/Storage/UnixFileOwnership.cs` | 81 | pavlov-storage |
| `PavlovBot.Host/Storage/WhitelistFile.cs` | 220 | pavlov-storage |
| `PavlovBot.Host/Verification/VerificationService.cs` | 357 | pavlov-services |
| `PavlovBot.Host/Vpn/Detectors.cs` | 375 | pavlov-services |
| `PavlovBot.Host/Vpn/ResilientJsonClient.cs` | 205 | pavlov-services |
| `PavlovBot.Host/Vpn/StaticMap.cs` | 92 | pavlov-services |
| `PavlovBot.Host/Vpn/VpnScreeningService.cs` | 513 | pavlov-services |
| `PavlovBot.Rcon/Protocol/RconReply.cs` | 196 | pavlov-rcon |
| `PavlovBot.Rcon/RconClient.cs` | 383 | pavlov-rcon |
| `PavlovBot.Rcon/RconConnection.cs` | 471 | pavlov-rcon |
| `PavlovBot.Rcon/RconOptions.cs` | 39 | pavlov-rcon |

### C# test files (145, 1,792 test methods)

| File | Tests | Rust |
|---|---|---|
| `AccessTests.cs` | 19 | 0/19 |
| `ArrestInteropTests.cs` | 5 | 0/5 |
| `AtomicFileTests.cs` | 8 | ported |
| `AuditLogTests.cs` | 9 | 0/9 |
| `AutoPostTests.cs` | 13 | 0/13 |
| `BanFileFormatTests.cs` | 7 | 0/7 |
| `BanFirewallTests.cs` | 14 | 0/14 |
| `BanIdentityAndAttachmentTests.cs` | 4 | 0/4 |
| `BanLiftTests.cs` | 12 | 0/12 |
| `BanResurrectionTests.cs` | 11 | 0/11 |
| `BanSpanOverflowTests.cs` | 8 | ported |
| `BanTests.cs` | 16 | ported |
| `BlacklistEndToEndTests.cs` | 4 | 0/4 |
| `BoardsTests.cs` | 15 | 0/15 |
| `BotSupervisorTests.cs` | 6 | 0/6 |
| `CashTableTests.cs` | 8 | 0/8 |
| `CheckBanHistoryTests.cs` | 9 | 0/9 |
| `CommandBlacklistTests.cs` | 20 | 1/20 |
| `CommandLogTests.cs` | 10 | 0/10 |
| `ComponentFlowTests.cs` | 9 | 0/9 |
| `ComponentIdTests.cs` | 11 | 0/11 |
| `ConfigurationTests.cs` | 31 | ported |
| `ConfirmedOnlineTests.cs` | 3 | 0/3 |
| `ConnectCardTests.cs` | 14 | 0/14 |
| `CrashRecoveryTests.cs` | 11 | 0/11 |
| `DataLayerTests.cs` | 24 | ported |
| `DebugPassRegressionTests.cs` | 8 | 0/8 |
| `DefaultSubclassTests.cs` | 10 | 0/10 |
| `DetectorFailureTests.cs` | 11 | 0/11 |
| `DomainTests.cs` | 62 | ported |
| `EvasionResponderTests.cs` | 16 | 0/16 |
| `EvasionScorerTests.cs` | 15 | ported |
| `EventRecorderTests.cs` | 6 | 0/6 |
| `EventStoreTests.cs` | 13 | 0/13 |
| `FactionBotTests.cs` | 12 | ported |
| `FactionRenameTests.cs` | 11 | 0/11 |
| `FactionRoleTests.cs` | 20 | 0/20 |
| `FactionSetTests.cs` | 12 | 0/12 |
| `FactionSourceTests.cs` | 10 | 0/10 |
| `FactionTests.cs` | 33 | ported |
| `FeedBridgeTests.cs` | 11 | 0/11 |
| `FeedWebhookTests.cs` | 11 | 0/11 |
| `FirewallTests.cs` | 6 | 0/6 |
| `GameFileGuardTests.cs` | 9 | 0/9 |
| `GeoLocatorTests.cs` | 10 | 0/10 |
| `GuildInvitesTests.cs` | 7 | 0/7 |
| `HealthErrorTests.cs` | 8 | 0/8 |
| `HoldAllRanksTests.cs` | 6 | 0/6 |
| `HoldRanksEndToEndTests.cs` | 8 | 0/8 |
| `HomeGuildAccessTests.cs` | 13 | 0/13 |
| `IntelligenceRulesTests.cs` | 6 | ported |
| `InteractionDispatcherTests.cs` | 3 | 0/3 |
| `IpFlagDatasetTests.cs` | 6 | 0/6 |
| `IpTrackingTests.cs` | 22 | 0/22 |
| `KdBoardTests.cs` | 5 | 0/5 |
| `KillDataBlockTests.cs` | 5 | 0/5 |
| `KillLineTests.cs` | 5 | 0/5 |
| `KillStatsTests.cs` | 14 | 0/14 |
| `LeaveTrackingTests.cs` | 9 | 0/9 |
| `LedgerConcurrencyTests.cs` | 8 | ported |
| `LedgerFileTests.cs` | 4 | ported |
| `LicenseTests.cs` | 26 | 10/26 |
| `LogParsingTests.cs` | 37 | 34/37 |
| `LogTailerLockingTests.cs` | 1 | 0/1 |
| `LogTailerTests.cs` | 16 | 0/16 |
| `LoreTests.cs` | 8 | 1/8 |
| `MasterAccessTests.cs` | 19 | 0/19 |
| `MasterNamesConfigTests.cs` | 3 | ported |
| `MasterPardonTests.cs` | 3 | 0/3 |
| `MasterServerListTests.cs` | 10 | 0/10 |
| `ModSaveSyncPlannerTests.cs` | 9 | ported |
| `ModSaveSyncTests.cs` | 15 | 0/15 |
| `MonitoringServerTests.cs` | 9 | 0/9 |
| `NodeCompatibilityTests.cs` | 8 | 0/8 |
| `ObservabilityTests.cs` | 15 | 0/15 |
| `OwnerActionsTests.cs` | 37 | 0/37 |
| `OwnerGuardTests.cs` | 14 | 7/14 |
| `PavlovServerDetectionTests.cs` | 7 | 3/7 |
| `PayrollTests.cs` | 26 | 0/26 |
| `PenalCodeTests.cs` | 21 | ported |
| `PlayerBoardTests.cs` | 13 | 0/13 |
| `PlayerCountChannelTests.cs` | 26 | 0/26 |
| `PoliceLogRoutingTests.cs` | 6 | 0/6 |
| `PreservedPlayerDataTests.cs` | 6 | 0/6 |
| `ProcessRunnerTests.cs` | 10 | 0/10 |
| `ProfileRedactionTests.cs` | 10 | ported |
| `ProvisioningTests.cs` | 84 | 49/84 |
| `RankRestorerTests.cs` | 6 | 0/6 |
| `RconBlankReplyTests.cs` | 3 | ported |
| `RconChunkedReplyTests.cs` | 3 | ported |
| `RconClientTests.cs` | 13 | ported |
| `RconConfirmationsTests.cs` | 6 | 0/6 |
| `RconCorrelationTests.cs` | 6 | 5/6 |
| `RconDeliveryTests.cs` | 6 | ported |
| `RconFailureTests.cs` | 4 | 0/4 |
| `RconHealthTests.cs` | 6 | 0/6 |
| `RconLogConfirmationTests.cs` | 8 | 0/8 |
| `RconMenuTests.cs` | 7 | 0/7 |
| `RconResilienceTests.cs` | 6 | ported |
| `RecentErrorsTests.cs` | 6 | 0/6 |
| `RejectedCommandTests.cs` | 4 | 0/4 |
| `RiskScoringTests.cs` | 13 | ported |
| `RoleMapLegacyShapeTests.cs` | 10 | 0/10 |
| `RoleMapRoundTripTests.cs` | 7 | 0/7 |
| `RosterAuditTests.cs` | 6 | 0/6 |
| `RosterCommandGateTests.cs` | 5 | 0/5 |
| `RosterFileCreationTests.cs` | 8 | 0/8 |
| `RosterProblemExcerptTests.cs` | 5 | 0/5 |
| `RosterRefreshFailureTests.cs` | 3 | 0/3 |
| `RosterServiceTests.cs` | 43 | 0/43 |
| `RotateMapTests.cs` | 49 | 9/49 |
| `SecurityAlertTests.cs` | 17 | 4/17 |
| `SelfTestGraphTests.cs` | 1 | 0/1 |
| `SentenceCapTests.cs` | 16 | 0/16 |
| `ServerBanFileTests.cs` | 20 | 0/20 |
| `ServerHealthTests.cs` | 22 | ported |
| `ServerListTests.cs` | 13 | ported |
| `ServerMonitorTests.cs` | 10 | 0/10 |
| `ServiceRegistryTests.cs` | 23 | 0/23 |
| `ServiceRestartBrakeTests.cs` | 3 | 0/3 |
| `SkinTests.cs` | 15 | 1/15 |
| `SlashCommandValidationTests.cs` | 10 | 0/10 |
| `SpawnFactionTests.cs` | 14 | 0/14 |
| `SqliteStorageTests.cs` | 10 | ported |
| `StaffFeedTests.cs` | 18 | 0/18 |
| `StaticMapTests.cs` | 8 | 0/8 |
| `StatsLogTests.cs` | 19 | 11/19 |
| `StorageAndProtocolTests.cs` | 14 | ported |
| `StoredShapeTests.cs` | 7 | 0/7 |
| `SubclassChoiceTests.cs` | 8 | 0/8 |
| `SubcommandFilterTests.cs` | 9 | 0/9 |
| `TempBanExpiryTests.cs` | 5 | 0/5 |
| `UserAppEphemeralTests.cs` | 6 | 0/6 |
| `UserAppTests.cs` | 4 | 0/4 |
| `VerificationServiceTests.cs` | 6 | 0/6 |
| `VpnAutoBanSwitchTests.cs` | 5 | 1/5 |
| `VpnAutoBanWiringTests.cs` | 8 | 0/8 |
| `VpnCacheSnapshotTests.cs` | 2 | 0/2 |
| `VpnResponderTests.cs` | 10 | 0/10 |
| `VpnTests.cs` | 46 | ported |
| `VpnVerdictTests.cs` | 13 | ported |
| `WarningServiceTests.cs` | 15 | 0/15 |
| `WhitelistEphemeralTests.cs` | 3 | 0/3 |
| `WhitelistListTests.cs` | 4 | 0/4 |
| `WhitelistTests.cs` | 20 | 0/20 |

<!-- END GENERATED -->
