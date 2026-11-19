PAVLOV VR DISCORD BOT: SUMMARY (as of 2026-11-19)

WHAT IT IS
- Pavlov VR Discord bot written in Rust. Source is in rust/, one binary built to rust-out/pavlov-bot. It is the only bot in the repo: the Node bot and then the C#/.NET bot were removed (C# on 2026-11-19).
- The bot: systemd service pavlov-bot, /root/pavlov-bot. One theme, police (dollars, warrants, the GWPD). THEME=fallout or FACTION_SET=fallout stops startup with a message.
- Layout: eight crates. pavlov-core (pure domain logic, portable), pavlov-config, pavlov-rcon (RCON client), pavlov-storage, pavlov-host (Linux-only systemd/ufw control and the services), pavlov-discord, pavlov-gateway (serenity adapter), pavlov-bot (the binary).
- Build state: 2100 tests pass, clippy with warnings as errors. Tests include a fake RCON server that records the exact bytes on the wire.
- Game files: the ServerRoles folder (GWPD rank files and the menu tier files owner/manager/srstaff/staff/jrstaff) and <playername>.json saves in ModSave. No RCON+.
- Data files: still the JSON shapes the Node bot wrote.

HOW IT TALKS TO THE GAME
- RCON: one persistent serialised session per server, commands paced 100 ms apart, reads coalesced, failures never cached. 1 to 3 servers, RCON triples in .env.
- Server control: game servers are systemd units, listed positionally in PAVLOV_UNITS (entry N = server N).
- Docker is removed. A leftover "docker:" prefix in PAVLOV_UNITS is stripped in place so slot numbers do not shift.

FEATURES
- Moderation: native bans and kicks with reason and duration; staff ladder Super Owner > Owner > Admin > Mod (a lower tier cannot undo a higher tier); warnings; rank suspensions that auto-restore; ban-evasion scoring and alts lookup; VPN/proxy screening across six detectors; optional ufw blocking of banned IPs; audit log and staff activity reports.
- Factions and RP: whitelist rosters with ranks and sub-classes; faction stats; payroll and wages; economy ledger; donators; penal code, arrests, bail, warrants, background checks; licences; in-game menu grants with permanent name binding; verification.
- Boards and feeds: auto-posted leaderboards, K/D and playtime boards, player list, player-count voice channels; webhook feeds for joins, connects, kills and money; connect cards with geolocation.
- Monitoring: server monitoring with alerts, /health, crash recovery, metrics endpoint (/health, /healthz, /ready; off unless METRICS_PORT is set).

SLASH COMMANDS (58, from --selftest)
/adjustmoney /alts /announce /arrest /backgroundcheck /bail /banlist /botstats /checkban /configure /counts /deleteserver /demotion /donator /errors /factionstats /feeds /firewall /flush /givemenu /givemoney /guildinvites /health /help /inspect /iplookup /kick /license /manual /monitor /permban /players /promotion /provisionserver /removepin /rotatemap /server /serverinfo /serverswitch /setpin /setroles /staff /staffactivity /staffleaderboard /stats /stripmenu /suspendrank /tempban /testalert /tester /testmode /unban /unlinkname /vpncheck /wages /warn /warrant /whitelist
- Destructive bulk actions live under /configure and need the operation name typed back exactly.

CHANGES FROM THIS SESSION (merged #237 to #243, NOT DEPLOYED)
- Anti-DDoS removed: DDoS monitor, /antiddos and /packettools are gone.
- CrowdSec kept as a host service (outside the bot) for banning repeated SSH attempts. scripts/crowdsec/install.sh, verify.sh, remove-antiddos.sh; docs in CROWDSEC.md.
- systemd only: Docker removed from provisioning, switching, status, stats and delete. scripts/migrate-from-docker.sh moves old containers over and leaves Saved in place.
- /deleteserver stops and removes the systemd unit and also removes a legacy Docker container of the same name if one exists.
- /provisionserver follows the Pavlov wiki step for step:
  1. dependencies (gdb curl lib32gcc-s1 libc++-dev unzip; lib32gcc1 before Ubuntu 22.10; Rocky/RHEL skipped)
  2. locked steam user, SteamCMD
  3. steamclient.so via app 1007 (right after SteamCMD is located), copied to ~/.steam/sdk64 and Pavlov/Binaries/Linux
  4. game download (app 622970, -beta shack), libc++ link, chmod +x PavlovServer.sh
  5. RconSettings.txt, Game.ini, Logs and Config/LinuxServer folders, empty mods.txt/whitelist.txt/blacklist.txt
  6. your exact systemd unit (only install folder and -PORT change per server; every server has -PORT), daemon-reload, enable --now
  7. ufw allow for game, query and RCON ports; RCON reachability check; wire into .env
  8. final: net.MaxPlayersOverride=32 added to Engine.ini ONLY if that file exists (never created), server restarted; then the bot restarts
- Game.ini: an existing one is kept. ServerKey= is the only addition, and only when given and absent. API key ignored.
- Server list default version is Pavlov 1.0.32. /provisionserver has an optional serverkey option.

KNOWN COSTS AND OPEN ITEMS
- The wiki's bVerboseLogging=false means a freshly written Game.ini has no kill feed until set to true.
- Plain "ufw allow <port>" also opens the RCON port to the internet.
- Rocky and RHEL are not supported by provisioning.
- Engine.ini is often missing at provision time, so that step usually reports it did nothing.
- VPS still to do: deploy, run remove-antiddos.sh, install CrowdSec, run migrate-from-docker.sh, remove the old pavlov-shack-server container and image, then /provisionserver for slots 1, 2, 3.
- ARCHITECTURE.md describes the removed Node bot and is kept only as history.
