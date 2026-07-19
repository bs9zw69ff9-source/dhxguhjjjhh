/* ---------------- commands/info: /help /serverinfo /kd /stats ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  DIVIDER,
  EmbedBuilder, GLYPH, MessageFlags, NV, bar,
  brand, cell,
  discordIdForPavlov, emptyIdEmbed, factionKillBreakdown, formatKD, formatPlaytime, formatTimeLeft,
  getFactionRank, getFactionRankBadge, getLastSeen,
  getPlayerFactions, getPlayerHistory, hasAdminRole, hasFactionLeaderRole, hasModRole, hero,
  ipBans, isDonator, loadBans, loadPlaytime,
  log, paginate, parseRcon, playerCache, readPlayerBalance,
  loadServerStats, extractPlayerNames, easternClock,
  sanitizeId, sendRcon, serverLabel, spawn, update,
  ACTIVE_SERVERS,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         HELP
         ───────────────────────────────────────────────────── */
  "help": async (interaction, name) => {
        const isAdmin = hasAdminRole(interaction.member);
        const isMod   = hasModRole(interaction.member);
        const isFLead = hasFactionLeaderRole(interaction.member);
        const access  = isAdmin ? "ADMIN" : isMod ? "MODERATOR" : isFLead ? "FACTION LEADER" : "PUBLIC";
        const color   = isAdmin ? NV.AMBER : isMod ? NV.NCR_TAN : isFLead ? NV.GOLD : NV.BLUE_VATS;

        // Clean help-menu style: one flat list - `command` chip line, then a plain
        // explanation line under it. Tier shown in parentheses; no fields, no footer.
        const rows = [
          ["`/serverinfo <server>`", "Live server info - map, mode, players, and network peak."],
          ["`/checkban <player>` / `/stats <player>` / `/kd [player]`", "Ban status, full player dossier, or K/D stats and leaderboard."],
          ["`/link add`", "Request a Discord ↔ in-game name link (staff approve it)."],
          ["`/faction list <faction>` / `/faction playtime <faction>`", "Faction roster with ranks, or members ranked by playtime."],
          ["`/slots` `/coinflip` `/blackjack` `/roulette` `/cockfight` `/russianroulette` `/jackpot`", "Casino games - play with your credits."],
          ["`/kick <player> <server>` / `/flush <server>`", "Kick a player, or randomly kick one online player (mod)."],
          ["`/tempban <player> <reason> <server>` / `/unban <player>`", "Ban - the punishment preset sets the length - or lift a ban (mod)."],
          ["`/announce <message> <server> <target>`", "Broadcast an RCON notice to a player or everyone (mod)."],
          ["`/givecaps <player> <amount>` / `/link remove|list`", "Give credits; manage name links (mod)."],
          ["`/faction add|remove <player> <faction>`", "Faction whitelist management (faction leader)."],
          ["`/permban <player> <server> <reason>` / `/cleartempbans`", "Permanent ban, or clear every temporary ban (admin)."],
          ["`/staffactivity <staff>` / `/staffleaderboard [period]`", "Audit one staffer's actions, or rank staff by moderation actions (admin)."],
          ["`/givemenu <player>` / `/stripmenu <player>` / `/setrconroles`", "Grant or strip RCON menu access; map roles to menus (admin)."],
          ["`/donator add|remove|list <player>` / `/adjustcaps <player> <amount>`", "Manage the donator whitelist; adjust a player's ledger (admin)."],
          ["`/setroles` / `/casino` / `/manual <command> <server>`", "Set tier roles, casino config, or send raw RCON (admin)."],
          ["`/faction setrankcap <faction> <rank> <cap>`", "Cap members per rank - 0 = unlimited (admin)."],
          ["`/configure` / `/firewall block|unblock|status`", "Owner control panel; manual OS-firewall (ufw) control (owner)."],
          ["`/stripmenuall` / `/clearallbans` / `/faction wipe [faction]`", "Clear all menu access, unban everyone, or reset faction whitelists (owner)."],
        ];
        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`📚 ${process.env.BOT_NAME || "Server"} - Help Menu`)
          .setDescription(
            `Here are the available commands - your access: **${access}**.\n` +
            `Autocomplete works in every player and rank field.\n\n` +
            rows.map(([cmd, desc]) => `${cmd}\n${desc}`).join("\n")
          );
        brand(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
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
            // Pavlov nests the ServerInfo fields under a `ServerInfo` key - reading them
            // top-level (the old code) is why map/mode/max all showed *Unknown* / ?.
            const sv = infoData?.ServerInfo ?? infoData ?? {};
            // Derive the roster from the SAME RefreshList we just fetched, so the count
            // and the names always agree and are live - the old code counted this fetch
            // but drew names from the separately-polled cache, so they could disagree.
            const roster = extractPlayerNames(listData);
            return {
              ok:         listData?.Successful ?? false,
              players:    roster.length,
              roster,
              mapLabel:   sv.MapLabel ?? sv.MapName ?? sv.ServerName ?? "*Unknown*",
              gameMode:   sv.GameMode ?? "*Unknown*",
              serverName: sv.ServerName ?? serverLabel(srv),
              maxPlayers: Number(sv.MaxPlayers) || 24,
            };
          } catch { return { ok: false, players: 0, roster: [], mapLabel: "*Unreachable*", gameMode: "*Unreachable*", serverName: serverLabel(srv), maxPlayers: 24 }; }
        };
        const servers = server === "both" ? ACTIVE_SERVERS : [server];
        const infos   = await Promise.all(servers.map(fetchInfo));
        const stats   = loadServerStats();
        const embeds  = infos.map((info, i) => {
          const srv = servers[i];
          const maxN = Number(info.maxPlayers) || 24;
          const lines = [
            `${cell("status", 8)} ${info.ok ? `${GLYPH.up} online` : `${GLYPH.down} offline`}`,
            `${cell("map", 8)} ${info.mapLabel}`,
            `${cell("mode", 8)} ${info.gameMode}`,
            `${cell("players", 8)} ${bar(info.players, maxN, 10)} ${info.players}/${maxN}`,
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
          return brand(e, { footer: { text: `${serverLabel(srv)} - live data` } });
        });
        // Network total across every server: X / (combined capacity), plus the all-time
        // and today's combined peak - one figure, not per-server. Also on the live dashboard.
        const liveTotal = infos.reduce((s, x) => s + (x.ok ? x.players : 0), 0);
        const totalMax  = infos.reduce((s, x) => s + (Number(x.maxPlayers) || 24), 0);
        const peakAll   = stats?.combined?.peak ?? 0;
        const today     = stats?.daily?.date === easternClock().date ? (stats.daily.combined?.peak ?? 0) : 0;
        embeds[0].addFields({ name: "Network", value: `**${liveTotal}/${totalMax}** online now  -  peak **${peakAll}** all-time  -  **${today}** today`, inline: false });
        return interaction.editReply({ embeds });
        },

  "kd": async (interaction, name) => {
        const raw = interaction.options.getString("playerid");
        if (raw && raw.trim()) {
          const playerId = sanitizeId(raw);
          let k; try { k = ipBans.getKD(playerId); } catch { k = { name: playerId, kills: 0, deaths: 0 }; }
          const ratio = (k.deaths ? k.kills / k.deaths : k.kills).toFixed(2);
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.AMBER).setTitle(`K/D - ${playerId}`)
              
              .addFields(
                { name: "Kills",  value: `**${k.kills}**`,  inline: true },
                { name: "Deaths", value: `**${k.deaths}**`, inline: true },
                { name: "Ratio",  value: `**${ratio}**`,    inline: true },
                { name: "Playtime", value: (() => { const pt = loadPlaytime(); const key = Object.keys(pt).find(x => x.toLowerCase() === playerId.toLowerCase()); return key !== undefined ? formatPlaytime(pt[key]) : "*no record*"; })(), inline: true },
                { name: "Faction",  value: (() => { const f = getPlayerFactions(playerId); return f && f.length ? f.join(", ") : "*none*"; })(), inline: true },
              ).setFooter({ text: "Tracked from live kill logs while the bot is running" })
          ]});
        }
        // no player -> leaderboard
        let top = []; try { top = ipBans.topKD(100); } catch {}
        if (!top.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("K/D Leaderboard").setDescription("No kill data tracked yet.")] });
        await interaction.deferReply();
        const lines = top.map((e, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${e.name}**  -  ${e.kills}/${e.deaths}  -  **${e.ratio.toFixed(2)}** K/D`);
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.AMBER).setTitle("K/D Leaderboard")
            .setDescription(`> *"Only the deadliest walk the server."*\n${pageLines.join("\n")}`)
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
        const linkedId = discordIdForPavlov(playerId);
        let ipRec = null; try { ipRec = ipBans.getRecord(playerId); } catch {}

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`Player Dossier - ${playerId}`)
          .setDescription(
            tb ? hero(tb.permanent || !tb.expires ? "Permanently banned." : `Currently banned - ${formatTimeLeft(tb.expires)} remaining.`) :
            online ? hero("Currently active on the server.") :
            hero("Offline - last tracked playtime shown.")
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
          embed.addFields({ name: "Balance", value: `**${balance.toLocaleString()} credits**`, inline: false });
        }
        if (tb) {
          embed.addFields({ name: "Active Ban", value: tb.permanent || !tb.expires
            ? `Permanent ban - *${tb.reason}*`
            : `Temp ban - *${tb.reason}*  -  expires <t:${Math.floor(tb.expires / 1000)}:R>`, inline: false });
        }
        if (history.length) {
          embed.addFields({ name: "Mod Actions", value: `**${history.length}** on record`, inline: false });
        }
        if (ipRec?.flagged && !tb) embed.addFields({ name: "Evasion Watch", value: "This account matches an active IP/EOS flag - next join is auto-banned.", inline: false });

        // Faction kills - how many times this player has killed members of each
        // faction, cross-referenced from the live kill log against the spawn files.
        const fkills = factionKillBreakdown(playerId);
        if (fkills && Object.keys(fkills).length) {
          const ordered = Object.entries(fkills).sort((a, b) => b[1].total - a[1].total);
          const grand   = ordered.reduce((a, [, d]) => a + d.total, 0);
          embed.addFields({
            name: `Faction Kills - ${grand} total`,
            value: ordered.map(([f, d]) => `${GLYPH.rank} **${f}** - ${d.total} kill${d.total !== 1 ? "s" : ""}`).join("\n"),
            inline: false,
          });
        }

        brand(embed, { thumb: true, footer: { text: "Playtime tracked every 60s since deployment" } });
        return interaction.reply({ embeds: [embed] });
        },
  };
};
