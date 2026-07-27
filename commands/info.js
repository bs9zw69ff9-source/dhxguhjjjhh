/* ---------------- commands/info: /help /serverinfo ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  EmbedBuilder, GLYPH, MessageFlags, NV, brand,
  hasAdminRole, hasFactionLeaderRole, hasModRole,
  parseRcon, loadServerStats, extractPlayerNames, easternClock,
  sendRcon, serverLabel, ACTIVE_SERVERS,
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
          ["`/checkban <player>`", "Check whether a player is banned."],
          ["`/whitelist list <name>` / `/whitelist playtime <name>`", "Whitelist roster with ranks, or members ranked by playtime."],
          ["`/kick <player>` / `/flush <server>`", "Kick a player, or randomly kick one online player (mod)."],
          ["`/tempban <player> <reason> <duration>` / `/unban <player>`", "Ban for a chosen length of time, or lift a ban (mod)."],
          ["`/announce <message> <target>`", "Broadcast an RCON notice to a player or everyone (mod)."],
          ["`/givecaps <player> <amount>`", "Give dollars to a player (mod)."],
          ["`/whitelist add|remove <player> <name>`", "Add or remove a player from a whitelist (whitelist leader)."],
          ["`/promotion <player>` / `/demotion <player>`", "Move a whitelisted player up or down one rank (whitelist leader)."],
          ["`/subclass <player> <sub-class>`", "Assign or remove a sub-class like NYPD Detective / Vice Officer (whitelist leader)."],
          ["`/warrant give|remove|check <player>`", "Issue, clear, or look up a player's warrant (police officer)."],
          ["`/arrest <player>` / `/backgroundcheck <player>`", "Book a player on penal-code charges, or pull their record (police officer)."],
          ["`/suspendrank <player> <time>`", "Pull a member's whitelist rank for a set time; it auto-restores (mod)."],
          ["`/bail increase|decrease|reset|show <percent>`", "Scale every charge's bail price by a percentage (mod)."],
          ["`/permban <player> <reason>` / `/cleartempbans`", "Permanent ban, or clear every temporary ban (admin)."],
          ["`/staffactivity <staff>` / `/staffleaderboard [period]`", "Audit one staffer's actions, or rank staff by moderation actions (admin)."],
          ["`/givemenu <player>` / `/stripmenu <player>` / `/setrconroles`", "Grant or strip RCON menu access; map roles to menus (admin)."],
          ["`/donator add|remove|list <player>` / `/adjustcaps <player> <amount>`", "Manage the donator list; adjust a player's ledger (admin)."],
          ["`/setroles` / `/manual <command>`", "Set tier roles, or send a raw RCON command (admin)."],
          ["`/configure` / `/firewall block|unblock|status` / `/health`", "Owner control panel, manual OS-firewall (ufw) control, and bot health (owner)."],
          ["`/vpncheck [ip]`", "Probe every VPN detector: which are up and each one's role (owner)."],
          ["`/stats`", "Memory, CPU, hardware, and which datasets use the most memory (owner)."],
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

  };
};
