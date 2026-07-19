/* ---------------- commands/info: /help /dashboard /ping /serverinfo /find /kd /stats ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  ALL_FACTIONS, BLACKLIST_IDS, BOT_COPYRIGHT, BOT_START_MS, DASHBOARD_INTERVAL_MS, DIVIDER,
  EmbedBuilder, GLYPH, MessageFlags, NV, WAGE_TIERS, bar,
  brand, buildDashboardEmbed, buildFactionMembershipIndex, cell, client, dashboardSnapshots,
  discordIdForPavlov, emptyIdEmbed, factionKillBreakdown, formatKD, formatPlaytime, formatTimeLeft,
  formatUptime, getFactionRank, getFactionRankBadge, getFactionRankConfig, getLastSeen, getMute,
  getPlayerFactions, getPlayerHistory, hasAdminRole, hasFactionLeaderRole, hasModRole, hero,
  ipBans, isDonator, loadBans, loadPlaytime, loadRoles, loadWages,
  log, paginate, parseRcon, playerCache, readPlayerBalance, refreshPlayerCache,
  loadServerStats, extractPlayerNames,
  sanitizeId, sendRcon, serverLabel, spawn, update,
  ACTIVE_SERVERS,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         HELP
         ───────────────────────────────────────────────────── */
  "help": async (interaction, name) => {
        const { modRoleId, adminRoleId, factionLeaderRoleId } = loadRoles();
        const isAdmin = hasAdminRole(interaction.member);
        const isMod   = hasModRole(interaction.member);
        const isFLead = hasFactionLeaderRole(interaction.member);

        let badge, color;
        if (isAdmin)      { badge = "**ADMIN**";          color = NV.AMBER;     }
        else if (isMod)   { badge = "**MODERATOR**";      color = NV.NCR_TAN;   }
        else if (isFLead) { badge = "**FACTION LEADER**"; color = NV.GOLD;      }
        else              { badge = "**PUBLIC ACCESS**";  color = NV.BLUE_VATS; }

        const mStr = modRoleId           ? `<@&${modRoleId}>`           : "`not set`";
        const aStr = adminRoleId         ? `<@&${adminRoleId}>`         : "`not set`";
        const fStr = factionLeaderRoleId ? `<@&${factionLeaderRoleId}>` : "`not set`";

        const rankSummaryLines = ALL_FACTIONS.map(f => {
          const cfg = getFactionRankConfig(f);
          const rankStr = cfg ? cfg.order.map(r => `${cfg.badges[r]} ${r}`).join(" → ") : "*no ranks*";
          return `**${f}:** ${rankStr}`;
        }).join("\n");

        const embed = new EmbedBuilder().setColor(color)
          .setTitle("Command Roster")
          .setDescription(
            `> *War never changes — but the rules of the server, those we enforce.*\n\n` +
            `### ${GLYPH.rank}  Your Access\n${badge}\n` +
            `-# Mod ${mStr}  ${GLYPH.dot}  Admin ${aStr}  ${GLYPH.dot}  Faction ${fStr}\n` +
            `-# Autocomplete works in every Player ID and Rank field.`
          )
          .addFields(
            { name: "Public",
              value: "`/help` `/ping` `/dashboard` `/serverinfo` `/find` `/checkban` `/banlist` `/stats` `/kd` `/checkbalance` `/wagelist` `/link add`\n`/faction list` `/faction audit` `/faction playtime`" },
            { name: "Moderator",
              value: [
                "`/kick <id> <server> [reason]` — Eject",
                "`/mute <id> <duration> [reason]` — In-game mute (re-applied every join until it expires)",
                "`/unmute <id>` — Lift a mute now",
                "`/flush <server>` — Randomly kick one online player (staff & donators immune)",
                "`/tempban <id> <duration> <server> <reason>` — Temporary exile",
                "`/unban <id> <server>` — Lift exile",
                "`/announce <msg> <server> <target>` — RCON Notify a player or All",
                "`/givecaps <id> <amount> [reason]` — Give credits to a player",
                "`/faction transfer <id> <from> <to> [rank]` — Move player between factions",
              ].join("\n") },
            { name: "Faction Leader",
              value: [
                "`/faction add <id> <faction> [rank]` — Whitelist player (optional starting rank)",
                "`/faction remove <id> <faction>` — Remove from whitelist",
                "`/faction rank <id> <faction> <rank>` — Set member rank *(FL only)*",
                "`/faction list <faction>` — Roster with ranks (pages)",
                "`/faction audit <faction>` — Add/remove/rank change log (pages)",
                "`/addwage <id> <tier>` — Enrol in payroll or issue mercenary pay",
                "`/removewage <id>` — Remove from payroll",
              ].join("\n") },
            { name: "Admin",
              value: [
                "`/permban <id> <server> <reason>` — Permanent ban",
                "`/cleartempbans` `/setroles`",
                "`/staffactivity <staff>` — All mod actions by a staff member",
                "`/givemenu` `/stripmenu` `/adjustcaps`",
                "`/manual`",
                "`/donator add|remove|list <id>` — Manage the donator whitelist file",
                "`/stripmenuall` — *Owner only* — clear ALL menu access from everyone",
                "`/configure` — *Owner only* — hidden control panel (IP tracker management)",
                "`/setrconroles [high_staff] [staff] [faction]` — *Admin* — set which roles grant each RCON menu (self-service panel)",
                "`/link remove|list` — *Mod* — manage Discord ↔ Pavlov links (adds are public requests)",
                "`/autorotate set|off|status` — *Owner* — daily map rotation at a set Eastern time",
                "`/clearallbans` — *Owner only* — unban everyone (clears blacklist.txt)",
                "`/faction setcap <faction> <cap>` — Set faction size limit",
                "`/faction setrankcap <faction> <rank> <cap>` — Cap members per rank (0 = unlimited)",
              ].join("\n") },
            { name: "Faction Ranks (per faction)",
              value: rankSummaryLines },
            { name: "Automation",
              value: [
                "Temp bans auto-lifted every **60s**",
                "Leaderboards refreshed every **30s**",
                "Wages disbursed every **7 days**",
                "RCON health check every **5 min**",
                "Rank changes update both the rank registry and the rank-specific spawn files automatically",
                "`/kick` `/tempban` `/permban` accept an optional **discord_user** — the bot DMs them their punishment details",
                "Command blacklist is set via **`BLACKLIST_IDS`** in `.env` (restart to apply)",
              ].join("\n") },
          )
          .setFooter({ text: BOT_COPYRIGHT });
        brand(embed, { thumb: true });
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         PING
         ───────────────────────────────────────────────────── */
  "dashboard": async (interaction, name) => {
        await interaction.deferReply();
        const until = Date.now() + 5 * 60 * 1000;   // live-refresh for 5 minutes, then freeze
        for (;;) {
          await interaction.editReply({ embeds: [buildDashboardEmbed(await dashboardSnapshots())], keepEmbeds: true });
          if (Date.now() >= until) break;
          await new Promise(r => setTimeout(r, DASHBOARD_INTERVAL_MS));
        }
        return;
        },

  "ping": async (interaction, name) => {
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        const start = Date.now();
        const pings = await Promise.allSettled(ACTIVE_SERVERS.map(s => sendRcon("RefreshList", s, 2000, 0)));
        const rtt   = Date.now() - start;
        const okBy  = ACTIVE_SERVERS.map((s, i) => pings[i].status === "fulfilled" && !!parseRcon(pings[i].value)?.Successful);
        const okCount = okBy.filter(Boolean).length;
        const color = okCount === ACTIVE_SERVERS.length ? NV.IRRAD_GREEN : okCount > 0 ? NV.AMBER : NV.RUST_RED;
        const headline = okCount === ACTIVE_SERVERS.length ? "All systems nominal — monitoring active."
          : okCount > 0 ? "Partial connectivity — a server is unreachable."
          : "All servers unreachable — check RCON config.";
        const wsPing = Math.max(0, client.ws.ping);
        const nodes = ACTIVE_SERVERS.length + 1;      // servers + the bot gateway
        const online = 1 + okCount;
        const stat = (ok) => ok ? `${GLYPH.up} up` : `${GLYPH.down} down`;
        const lines = [
          "SYSTEM DIAGNOSTICS",
          "──────────────────────────",
          `${cell("gateway", 9)} ${GLYPH.up} up  ${wsPing}ms`,
          ...ACTIVE_SERVERS.map((s, i) => `${cell(`server ${i + 1}`, 9)} ${stat(okBy[i])}`),
          "──────────────────────────",
          `${cell("nodes", 9)} ${bar(online, nodes, 8)} ${online}/${nodes}`,
          `${cell("rtt", 9)} ${rtt}ms`,
          `${cell("uptime", 9)} ${formatUptime(Date.now() - BOT_START_MS)}`,
          `${cell("cached", 9)} ${ACTIVE_SERVERS.map(s => playerCache[s].length).join("+")} players`,
          `${cell("bans", 9)} ${loadBans().length} active`,
        ];
        const embed = new EmbedBuilder().setColor(color)
          .setTitle("System Status")
          .setDescription(`${hero(headline)}\n\`\`\`\n${lines.join("\n")}\n\`\`\``);
        brand(embed, { thumb: true });
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         SERVERINFO
         ───────────────────────────────────────────────────── */
  "serverinfo": async (interaction, name) => {
        const server = interaction.options.getString("server");
        await interaction.deferReply();
        const fetchInfo = async (srv) => {
          try {
            const [listRaw, infoRaw] = await Promise.all([
              sendRcon("RefreshList", srv, 3000, 1),
              sendRcon("ServerInfo",  srv, 3000, 1),
            ]);
            const listData = parseRcon(listRaw);
            const infoData = parseRcon(infoRaw);
            // Derive the roster from the SAME RefreshList we just fetched, so the count
            // and the names always agree and are live — the old code counted this fetch
            // but drew names from the separately-polled cache, so they could disagree.
            const roster = extractPlayerNames(listData);
            return {
              ok:         listData?.Successful ?? false,
              players:    roster.length,
              roster,
              mapLabel:   infoData?.MapLabel    ?? infoData?.ServerName ?? "*Unknown*",
              gameMode:   infoData?.GameMode    ?? "*Unknown*",
              serverName: infoData?.ServerName  ?? serverLabel(srv),
              maxPlayers: infoData?.MaxPlayers  ?? "?",
            };
          } catch { return { ok: false, players: 0, roster: [], mapLabel: "*Unreachable*", gameMode: "*Unreachable*", serverName: serverLabel(srv), maxPlayers: "?" }; }
        };
        const servers = server === "both" ? ACTIVE_SERVERS : [server];
        const infos   = await Promise.all(servers.map(fetchInfo));
        const stats   = loadServerStats();
        const embeds  = infos.map((info, i) => {
          const srv = servers[i];
          const maxN = Number(info.maxPlayers) || info.players || 1;
          const peak = stats?.perServer?.[srv]?.peak ?? 0;
          const lines = [
            `${cell("status", 8)} ${info.ok ? `${GLYPH.up} online` : `${GLYPH.down} offline`}`,
            `${cell("map", 8)} ${info.mapLabel}`,
            `${cell("mode", 8)} ${info.gameMode}`,
            `${cell("players", 8)} ${bar(info.players, maxN, 10)} ${info.players}/${info.maxPlayers}`,
            `${cell("peak", 8)} ${peak} all-time`,
          ];
          const e = new EmbedBuilder()
            .setColor(info.ok ? NV.IRRAD_GREEN : NV.RUST_RED)
            .setTitle(info.serverName)
            .setDescription(`\`\`\`\n${lines.join("\n")}\n\`\`\``);
          const roster = [...(info.roster || [])].sort((a, b) => a.localeCompare(b));
          if (info.ok && roster.length) {
            const shown = roster.slice(0, 15).map(n => `\`${n}\``).join("  ");
            e.addFields({ name: `Online (${roster.length})`, value: (shown + (roster.length > 15 ? `  *+${roster.length - 15} more*` : "")).slice(0, 1024), inline: false });
          }
          return brand(e, { footer: { text: `${serverLabel(srv)} · live data` } });
        });
        // Across-all-servers summary when showing more than one server.
        if (servers.length > 1) {
          const liveTotal = infos.reduce((s, x) => s + (x.ok ? x.players : 0), 0);
          const peakAll   = stats?.combined?.peak ?? 0;
          embeds[0].addFields({ name: "All Servers", value: `**${liveTotal}** online now  ·  **${peakAll}** all-time peak (combined)`, inline: false });
        }
        return interaction.editReply({ embeds });
        },

  /* ─────────────────────────────────────────────────────
         FIND
         ───────────────────────────────────────────────────── */
  "find": async (interaction, name) => {
        const query = interaction.options.getString("name").toLowerCase();
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        await Promise.all(ACTIVE_SERVERS.map(refreshPlayerCache));
        const _findMembership = buildFactionMembershipIndex();   // one read for faction tags
        const matches = [];
        const seen    = new Set();
        for (const srv of ACTIVE_SERVERS) {
          for (const name of playerCache[srv]) {
            if (!name.toLowerCase().includes(query)) continue;
            const key = name.toLowerCase();
            if (seen.has(key)) {
              const m = matches.find(x => x.name.toLowerCase() === key);
              if (m) m.servers.push(srv);
            } else {
              seen.add(key);
              matches.push({ name, servers: [srv] });
            }
          }
        }
        if (!matches.length) {
          return interaction.editReply({ embeds: [
            brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("No Matches Found")
              .setDescription(`${hero(`No players matching "${query}" are online.`)}\n*Try a shorter search term.*`))
          ]});
        }
        const lines = matches.map((m) => {
          const srvStr = m.servers.map(s => "S" + s.replace("server", "")).join("+");
          const facs = _findMembership?.get(m.name.toLowerCase());
          return `\`[${srvStr}]\`  **${m.name}**${facs?.length ? `  —  ${facs.join(" / ")}` : ""}`;
        });
        return interaction.editReply({ embeds: [
          brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`Search Results — "${query}"`)
            .setDescription(`${hero(`**${matches.length}** match${matches.length !== 1 ? "es" : ""} found.`)}\n${lines.join("\n")}`))
        ]});
        },

  "kd": async (interaction, name) => {
        const raw = interaction.options.getString("playerid");
        if (raw && raw.trim()) {
          const playerId = sanitizeId(raw);
          let k; try { k = ipBans.getKD(playerId); } catch { k = { name: playerId, kills: 0, deaths: 0 }; }
          const ratio = (k.deaths ? k.kills / k.deaths : k.kills).toFixed(2);
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.AMBER).setTitle(`K/D — ${playerId}`)
              .setDescription(`${DIVIDER}`)
              .addFields(
                { name: "Kills",  value: `**${k.kills}**`,  inline: true },
                { name: "Deaths", value: `**${k.deaths}**`, inline: true },
                { name: "Ratio",  value: `**${ratio}**`,    inline: true },
                { name: "Playtime", value: (() => { const pt = loadPlaytime(); const key = Object.keys(pt).find(x => x.toLowerCase() === playerId.toLowerCase()); return key !== undefined ? formatPlaytime(pt[key]) : "*no record*"; })(), inline: true },
                { name: "Faction",  value: (() => { const f = getPlayerFactions(playerId); return f && f.length ? f.join(", ") : "*none*"; })(), inline: true },
              ).setFooter({ text: "Tracked from live kill logs while the bot is running" }).setTimestamp()
          ]});
        }
        // no player -> leaderboard
        let top = []; try { top = ipBans.topKD(100); } catch {}
        if (!top.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("K/D Leaderboard").setDescription("No kill data tracked yet.").setTimestamp()] });
        await interaction.deferReply();
        const lines = top.map((e, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${e.name}**  ·  ${e.kills}/${e.deaths}  ·  **${e.ratio.toFixed(2)}** K/D`);
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.AMBER).setTitle("K/D Leaderboard")
            .setDescription(`> *"Only the deadliest walk the server."*\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Sorted by K/D ratio" }), { perPage: 20 });
        },

  "stats": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const playtime = loadPlaytime();
        const ptKey    = Object.keys(playtime).find(k => k.toLowerCase() === playerId.toLowerCase());
        const minutes  = ptKey !== undefined ? playtime[ptKey] : null;
        const factions = getPlayerFactions(playerId);
        const onS1     = playerCache.server1.some(n => n.toLowerCase() === playerId.toLowerCase());
        const onS2     = playerCache.server2.some(n => n.toLowerCase() === playerId.toLowerCase());
        const onS3     = playerCache.server3.some(n => n.toLowerCase() === playerId.toLowerCase());
        const online   = onS1 || onS2 || onS3;
        const balance  = readPlayerBalance(playerId);
        const wage     = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier    = wage ? WAGE_TIERS[wage.tier] : null;
        const tb       = loadBans().find(b => String(b.playerId).toLowerCase() === playerId.toLowerCase());
        const history  = getPlayerHistory(playerId);
        const lastSeen = getLastSeen(playerId);
        const donator  = isDonator(playerId);

        const fStr = factions === null ? "Folder unreadable"
          : !factions.length ? "*No faction access*"
          : factions.map(f => {
              const rank = getFactionRank(f, playerId);
              return `${getFactionRankBadge(f, rank)}  **${f}** *(${rank})*`;
            }).join("\n");

        const statusStr = !online ? "Offline" : [onS1 && "Server 1", onS2 && "Server 2", onS3 && "Server 3"].filter(Boolean).join("  +  ");
        const color = tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;
        const muteRec  = getMute(playerId);
        const muteStr  = muteRec && (!muteRec.expires || muteRec.expires > Date.now())
          ? `Muted — lifts <t:${Math.floor(muteRec.expires / 1000)}:R>` : null;
        const linkedId = discordIdForPavlov(playerId);
        let ipRec = null; try { ipRec = ipBans.getRecord(playerId); } catch {}

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`Player Dossier — ${playerId}`)
          .setDescription(
            tb ? hero(tb.permanent || !tb.expires ? "Permanently exiled from the server." : `Currently serving exile — ${formatTimeLeft(tb.expires)} remaining.`) :
            online ? hero("Currently active on the server.") :
            hero("Offline — last tracked playtime shown.")
          )
          .addFields(
            { name: "Status",        value: statusStr,                                                          inline: true },
            { name: "Playtime",      value: minutes !== null ? `**${formatPlaytime(minutes)}**` : "*No record*", inline: true },
            { name: "Last Seen",     value: online ? "Online now" : lastSeen ? `<t:${Math.floor(lastSeen / 1000)}:R>` : "*No record*", inline: true },
            { name: "Donator",       value: donator ? "Yes" : "No",                                       inline: true },
            { name: "K / D",         value: formatKD(playerId),                                                 inline: true },
            { name: "Sessions",      value: String(ipRec?.logins ?? 0),                                          inline: true },
            { name: "Discord",       value: linkedId ? `<@${linkedId}>` : "*not linked*",                        inline: true },
            { name: "Faction Ranks", value: fStr,                                                               inline: false },
          );

        if (balance !== null) {
          embed.addFields({ name: "Balance", value: `**${balance.toLocaleString()} credits**${wTier ? `  ·  Payroll: ${wTier.label} (+${wTier.amount}/wk)` : "  ·  Not on payroll"}`, inline: false });
        }
        if (tb) {
          embed.addFields({ name: "Active Exile", value: tb.permanent || !tb.expires
            ? `Permanent ban — *${tb.reason}*`
            : `Temp ban — *${tb.reason}*  ·  expires <t:${Math.floor(tb.expires / 1000)}:R>`, inline: false });
        }
        if (history.length) {
          embed.addFields({ name: "Mod Actions", value: `**${history.length}** on record`, inline: false });
        }
        if (muteStr) embed.addFields({ name: "In-Game Mute", value: muteStr, inline: false });
        if (ipRec?.flagged && !tb) embed.addFields({ name: "Evasion Watch", value: "This account matches an active IP/EOS flag — next join is auto-banned.", inline: false });

        // Faction kills — how many times this player has killed members of each
        // faction, cross-referenced from the live kill log against the spawn files.
        const fkills = factionKillBreakdown(playerId);
        if (fkills && Object.keys(fkills).length) {
          const ordered = Object.entries(fkills).sort((a, b) => b[1].total - a[1].total);
          const grand   = ordered.reduce((a, [, d]) => a + d.total, 0);
          embed.addFields({
            name: `Faction Kills — ${grand} total`,
            value: ordered.map(([f, d]) => `${GLYPH.rank} **${f}** — ${d.total} kill${d.total !== 1 ? "s" : ""}`).join("\n"),
            inline: false,
          });
        }

        brand(embed, { thumb: true, footer: { text: "Playtime tracked every 60s since deployment" } });
        return interaction.reply({ embeds: [embed] });
        },
  };
};
