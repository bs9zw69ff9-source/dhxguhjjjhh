/* ---------------- commands/info: /help /serverinfo /kd /stats ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  DIVIDER,
  EmbedBuilder, GLYPH, MessageFlags, NV,
  brand,
  discordIdForPavlov, emptyIdEmbed, factionKillBreakdown, formatPlaytime,
  getFactionRank, getFactionRankBadge, getLastSeen,
  getPlayerFactions, hasAdminRole, hasFactionLeaderRole, hasModRole,
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
        const access  = isAdmin ? "ADMIN" : isMod ? "MODERATOR" : isFLead ? "WHITELIST LEADER" : "PUBLIC";
        const color   = isAdmin ? NV.AMBER : isMod ? NV.NCR_TAN : isFLead ? NV.GOLD : NV.BLUE_VATS;

        // Clean help-menu style: one flat list - `command` chip line, then a plain
        // explanation line under it. Tier shown in parentheses; no fields, no footer.
        const rows = [
          ["`/serverinfo`", "Live server info - map, mode, players, and network peak."],
          ["`/checkban <player>` / `/stats <player>` / `/kd [player]`", "Ban status, full player dossier, or K/D stats and leaderboard."],
          ["`/link add`", "Request a Discord ↔ in-game name link (staff approve it)."],
          ["`/whitelist list <name>` / `/whitelist playtime <name>`", "Whitelist roster with ranks, or members ranked by playtime."],
          ["`/slots` `/coinflip` `/blackjack` `/roulette` `/cockfight` `/russianroulette` `/jackpot`", "Casino games - play with your credits."],
          ["`/kick <player>` / `/flush <server>`", "Kick a player, or randomly kick one online player (mod)."],
          ["`/tempban <player> <reason>` / `/unban <player>`", "Ban - the punishment preset sets the length - or lift a ban (mod)."],
          ["`/announce <message> <target>`", "Broadcast an RCON notice to a player or everyone (mod)."],
          ["`/givecaps <player> <amount>` / `/link remove|list`", "Give credits; manage name links (mod)."],
          ["`/whitelist add|remove <player> <name>`", "Whitelist management (whitelist leader)."],
          ["`/whitelist rank <name> <player> <rank>`", "Give or remove a member's rank - a member can hold several (whitelist leader)."],
          ["`/permban <player> <reason>` / `/cleartempbans`", "Permanent ban, or clear every temporary ban (admin)."],
          ["`/staffactivity <staff>` / `/staffleaderboard [period]`", "Audit one staffer's actions, or rank staff by moderation actions (admin)."],
          ["`/givemenu <player>` / `/stripmenu <player>` / `/setrconroles`", "Grant or strip RCON menu access; map roles to menus (admin)."],
          ["`/donator add|remove|list <player>` / `/adjustcaps <player> <amount>`", "Manage the donator whitelist; adjust a player's ledger (admin)."],
          ["`/setroles` / `/casino` / `/manual <command>`", "Set tier roles, casino config, or send raw RCON (admin)."],
          ["`/whitelist setrankcap <name> <rank> <cap>`", "Cap members per rank - 0 = unlimited (admin)."],
          ["`/configure` / `/firewall block|unblock|status` / `/health`", "Owner control panel, manual OS-firewall (ufw) control, and bot health (owner)."],
          ["`/stripmenuall` / `/clearallbans` / `/whitelist wipe [name]`", "Clear all menu access, unban everyone, or reset whitelists (owner)."],
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
        const server = "both"   /* server option removed - applies to all servers */;
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
          // Written like a person would say it, not a status table.
          const roster = [...(info.roster || [])].sort((a, b) => a.localeCompare(b));
          const shown  = roster.slice(0, 15).map(n => `\`${n}\``).join(" ");
          const lines = info.ok
            ? [`${GLYPH.up} **${info.serverName}** is online with **${info.players}/${maxN}** players, playing **${info.gameMode}** on **${info.mapLabel}**.`,
               roster.length ? `Online: ${shown}${roster.length > 15 ? ` and ${roster.length - 15} more` : ""}` : "Nobody is on right now."]
            : [`${GLYPH.down} **${info.serverName}** looks offline right now.`];
          const e = new EmbedBuilder()
            .setColor(info.ok ? NV.IRRAD_GREEN : NV.RUST_RED)
            .setTitle(info.serverName)
            .setDescription(lines.join("\n"));
          return brand(e);
        });
        // Network total across every server: X / (combined capacity), plus the all-time
        // and today's combined peak - one figure, not per-server. Also on the live dashboard.
        const liveTotal = infos.reduce((s, x) => s + (x.ok ? x.players : 0), 0);
        const totalMax  = infos.reduce((s, x) => s + (Number(x.maxPlayers) || 24), 0);
        const peakAll   = stats?.combined?.peak ?? 0;
        const today     = stats?.daily?.date === easternClock().date ? (stats.daily.combined?.peak ?? 0) : 0;
        embeds[0].addFields({ name: "All servers", value: `**${liveTotal}/${totalMax}** online right now. Peak: **${peakAll}** all time, **${today}** today.`, inline: false });
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
        const onServers = ACTIVE_SERVERS.filter(s => (playerCache[s] || []).some(n => n.toLowerCase() === playerId.toLowerCase()));
        const online    = onServers.length > 0;
        const balance  = readPlayerBalance(playerId);
        const tb       = loadBans().find(b => String(b.playerId).toLowerCase() === playerId.toLowerCase());
        const lastSeen = getLastSeen(playerId);
        const donator  = isDonator(playerId);

        const facStr = factions === null ? null
          : factions.map(f => `${getFactionRankBadge(f, getFactionRank(f, playerId))} **${f}** (${getFactionRank(f, playerId)})`).join(", ");

        const where = onServers.map(serverLabel).join(" and ");
        const color = tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;
        const linkedId = discordIdForPavlov(playerId);
        let ipRec = null; try { ipRec = ipBans.getRecord(playerId); } catch {}

        // One flowing, human-written summary instead of a field grid.
        const bits = [];
        bits.push(online
          ? `**${playerId}** is online on ${where} right now.`
          : lastSeen ? `**${playerId}** is offline, last seen <t:${Math.floor(lastSeen / 1000)}:R>.`
          : `**${playerId}** is offline and has not been seen yet.`);
        const facts = [];
        if (minutes !== null) facts.push(`**${formatPlaytime(minutes)}** of playtime`);
        if (balance !== null) facts.push(`**${balance.toLocaleString()} credits**`);
        let kd = null; try { kd = ipBans.getKD(playerId); } catch {}
        if (kd && (kd.kills + kd.deaths)) {
          const ratio = (kd.deaths ? kd.kills / kd.deaths : kd.kills).toFixed(2);
          facts.push(`a **${ratio}** K/D (${kd.kills}/${kd.deaths})`);
        }
        if (facts.length) bits.push(`They have ${facts.join(", ")}.`);
        bits.push(donator ? "They are a donator." : null);
        bits.push(linkedId ? `Discord: <@${linkedId}>.` : null);
        bits.push(facStr ? `Whitelists: ${facStr}.` : null);
        if (tb) bits.push(tb.permanent || !tb.expires
          ? `They are **permanently banned** (reason: \`${tb.reason}\`).`
          : `They are **banned** (reason: \`${tb.reason}\`), lifts <t:${Math.floor(tb.expires / 1000)}:R>.`);
        // Flag status is staff-only - telling an evader they are flagged tips them off.
        if (ipRec?.flagged && !tb && hasModRole(interaction.member)) bits.push("They are flagged and will be auto-banned next time they join.");
        const fkills = factionKillBreakdown(playerId);
        if (fkills && Object.keys(fkills).length) {
          const ordered = Object.entries(fkills).sort((a, b) => b[1].total - a[1].total);
          const grand   = ordered.reduce((a, [, d]) => a + d.total, 0);
          bits.push(`Whitelist kills: ${ordered.map(([f, d]) => `**${f}** ${d.total}`).join(", ")} (${grand} total).`);
        }

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`📋 Player Info - ${playerId}`)
          .setDescription(bits.filter(Boolean).join("\n"));

        brand(embed);
        return interaction.reply({ embeds: [embed] });
        },
  };
};
