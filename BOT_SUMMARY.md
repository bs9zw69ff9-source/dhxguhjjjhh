PAVLOV VR DISCORD BOT: SUMMARY (as of 2026-11-16)

WHAT IT IS
- Pavlov VR Discord bot written in C# on .NET 9. Source is in dotnet/. It is the only bot in the repo (the Node bot was removed).
- One binary, two deployments:
  - Police bot: systemd service pavlov-bot, /root/pavlov-bot, THEME=police (dollars, warrants, the GWPD).
  - Fallout bot: pm2, THEME=fallout (caps, the Mojave). Same build, not touched by any recent work.
- Layout: PavlovBot.Core (pure domain logic, portable), PavlovBot.Rcon (RCON client), PavlovBot.Host (Discord, storage, log tailing, Linux-only systemd/ufw control).
- Build state: 2175 tests pass, TreatWarningsAsErrors on. Tests include a fake RCON server that records the exact bytes on the wire.
- Data files: still the JSON files the Node bot wrote.
- Not verified against a live Pavlov server or Discord from this environment.

HOW IT TALKS TO THE GAME
- RCON: one persistent serialised session per server, commands paced 100 ms apart, reads coalesced, failures never cached. 1 to 3 servers, RCON triples in .env.
- Server control: game servers are systemd units, listed positionally in PAVLOV_UNITS (entry N = server N).
- Docker is removed. A leftover "docker:" prefix in PAVLOV_UNITS is stripped in place so slot numbers do not shift.

FEATURES
- Moderation: native bans and kicks with reason and duration; staff ladder Super Owner > Owner > Admin > Mod (a lower tier cannot undo a higher tier); warnings; rank suspensions that auto-restore; ban-evasion scoring and alts lookup; VPN/proxy screening across six detectors; optional ufw blocking of banned IPs; audit log and staff activity reports.
- Factions and RP: whitelist rosters with ranks and sub-classes; faction stats; payroll and wages; economy ledger; donators; penal code, arrests, bail, warrants, background checks; licences; in-game menu grants with permanent name binding; verification.
- Boards and feeds: auto-posted leaderboards, K/D and playtime boards, player list, player-count voice channels; webhook feeds for joins, connects, kills and money; connect cards with geolocation.
- Monitoring: server monitoring with alerts, /health, crash recovery, metrics endpoint (/health, /healthz, /ready; off unless METRICS_PORT is set).
- Plugins: plugin host with per-plugin permissions.

SLASH COMMANDS (58)
- Public: /help /stats /players /serverinfo /server /checkban /banlist
- Mod: /kick /tempban /unban /warn /announce /flush /rotatemap /alts /counts /feeds /suspendrank /monitor
- Admin: /permban /givemenu /stripmenu /donator /staff /staffactivity /staffleaderboard /unlinkname
- Police and RP: /arrest /warrant /backgroundcheck /bail /license /wages /whitelist /subclass /factionstats
- Owner, servers: /provisionserver /deleteserver /serverswitch /setpin /removepin /manual /configure
- Owner, diagnostics: /inspect /iplookup /vpncheck /health /errors /botstats /plugins /testalert
- Owner, access: /firewall /setroles /setrconroles /guildinvites /testmode /tester
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
- README.md, dotnet/README.md and ARCHITECTURE.md are out of date (still describe the Node bot).
