/* ---------------- commands/factions: /whitelist ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  ALL_FACTIONS, DIVIDER, EmbedBuilder, FACTION_BAK_DIR, MessageFlags, NV,
  SPAWN_FILE_MAP, addPlayerToRankFile, bar, brand, confirmDialog,
  countFactionRank, emptyIdEmbed, errorEmbed, factionLeaderOnlyEmbed, formatPlaytime, getFactionCap,
  getFactionDefaultRank, getFactionMembers, getFactionRank, getFactionRankBadge, getFactionRankConfig,
  getFactionRankOrder, getFactionSubclasses, getPlayerSubclasses, getPlayerFactions,
  addPlayerToSubclassFile, removePlayerFromSubclassFile, removePlayerFromAllSubclassFiles, hasModRole, hasWhitelistManageRole,
  isOwner, loadPlaytime, logAction, logger, meter,
  ownerOnlyEmbed, paginate, path, randomQuote, rankBadge,
  rankLabel, readFactionFile, removeFactionRank, removePlayerFromAllRankFiles,
  sanitizeId, setFactionRank, spawn, successEmbed,
  update, warningEmbed, wipeFaction, writeFactionAudit, writeFactionFile, writeModLog,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         FACTION - all subcommands
         ───────────────────────────────────────────────────── */
  "whitelist": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();

        /* ── wipe (owner only) - reset one whitelist, or every whitelist ── */
        if (sub === "wipe") {
          if (!isOwner(interaction.user.id)) {
            return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("whitelist");
          const targets = faction ? [faction] : ALL_FACTIONS;
          const counts  = targets.map(f => ({ faction: f, count: (readFactionFile(SPAWN_FILE_MAP[f]) ?? []).length }));
          const total   = counts.reduce((s, c) => s + c.count, 0);
          if (!total) {
            return interaction.reply({ embeds: [successEmbed("Nothing to Wipe",
              faction ? `**${faction}** has no members.` : "No whitelist has any members.")], flags: MessageFlags.Ephemeral });
          }
          const preview = counts.filter(c => c.count).map(c => `- **${c.faction}**: ${c.count} member${c.count !== 1 ? "s" : ""}`).join("\n");
          const go = await confirmDialog(interaction, {
            title: faction ? `Wipe ${faction}'s whitelist?` : "Wipe ALL whitelists?",
            body: `Clears membership and every rank file${faction ? "" : ", for every whitelist"} - **${total}** player${total !== 1 ? "s" : ""} total.\nA pre-wipe snapshot of each file is kept in \`${FACTION_BAK_DIR}\`.\n\n${preview}`,
            confirmLabel: faction ? `Wipe ${faction}` : "Wipe ALL",
          });
          if (!go) return;
          let wipedFactions = 0, wipedMembers = 0;
          for (const f of targets) {
            const r = await wipeFaction(f);
            if (!r.ok) continue;
            wipedFactions++; wipedMembers += r.count;
            writeFactionAudit({ action: "wipe", faction: f, count: r.count, by: interaction.user.tag });
          }
          writeModLog({ action: "faction-wipe", faction: faction || "ALL", count: wipedMembers, by: interaction.user.tag });
          const embed = successEmbed(
            faction ? `${faction} Whitelist Wiped` : "All Whitelists Wiped",
            `Cleared **${wipedMembers}** player${wipedMembers !== 1 ? "s" : ""} across **${wipedFactions}** whitelist${wipedFactions !== 1 ? "s" : ""}.`);
          await logAction(embed);
          return interaction.editReply({ embeds: [embed], components: [], keepEmbeds: true });
        }

        /* ── list (public, paginated) ── */
        if (sub === "list") {
          const faction  = interaction.options.getString("whitelist");
          const members  = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], flags: MessageFlags.Ephemeral });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} - Empty Roster`)
                .setDescription("No players are currently whitelisted here.\n\nUse `/whitelist add` to enlist someone.")
                
            ], flags: MessageFlags.Ephemeral });
          }
          const cap = getFactionCap(faction);
          const summary = getFactionRankOrder(faction).slice().reverse().map(r => {
            const n = countFactionRank(faction, r);   // file-based: counts every holder (a member may hold several ranks)
            if (!n) return null;
            return `${getFactionRankBadge(faction, r)} ${r}: **${n}**`;
          }).filter(Boolean).join("  -  ");
          const lines = members.map((m, i) =>
            `\`${String(i + 1).padStart(2, "0")}\`  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  -  *${(m.ranks || [m.rank]).join(", ")}*`);
          const header = `**${members.length}/${cap}** members${members.length > cap ? " over cap" : ""}  -  ${summary}`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`${faction} - Roster`)
              .setDescription(`${header}\n\n${pageLines.join("\n")}`)
              .setFooter({ text: SPAWN_FILE_MAP[faction] }),
            { perPage: 20 });
        }

        /* ── playtime (public, paginated) - roster ranked by time served ── */
        if (sub === "playtime") {
          const faction = interaction.options.getString("whitelist");
          const members = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], flags: MessageFlags.Ephemeral });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} - Empty Roster`)
                .setDescription("No players are currently whitelisted here.\n\nUse `/whitelist add` to enlist someone.")
                
            ], flags: MessageFlags.Ephemeral });
          }
          // Playtime keys are display-cased names - match members case-insensitively.
          const byName = new Map(Object.entries(loadPlaytime()).map(([n, m]) => [n.toLowerCase(), Number(m) || 0]));
          const ranked = members
            .map(m => ({ ...m, minutes: byName.get(m.playerId.toLowerCase()) ?? null }))
            .sort((a, b) => (b.minutes ?? -1) - (a.minutes ?? -1));
          const top   = ranked[0]?.minutes || 1;
          const total = ranked.reduce((s, m) => s + (m.minutes ?? 0), 0);
          const lines = ranked.map((m, i) => {
            const time  = m.minutes !== null ? formatPlaytime(m.minutes) : "*No record*";
            const meter = m.minutes !== null && i < 5 ? `  \`${bar(m.minutes, top, 8)}\`` : "";
            return `${rankLabel(i)}  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  -  ${time}${meter}`;
          });
          const header = `**${members.length}** member${members.length !== 1 ? "s" : ""}  -  **${formatPlaytime(total)}** combined`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`${faction} - Playtime`)
              .setDescription(`${header}\n\n${pageLines.join("\n")}`)
              .setFooter({ text: "Playtime sampled every 60s while online" }),
            { perPage: 20 });
        }

        /* ── add ── */
        if (sub === "add") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("whitelist");
          const rank     = getFactionDefaultRank(faction);   // always the lowest rank - rank is not chosen at add time
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Whitelist", `Whitelist \`${faction}\` has no configured spawn file.`)], flags: MessageFlags.Ephemeral });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          // One faction per player - block if they're already in a different faction.
          const otherFactions = (getPlayerFactions(playerId) || []).filter(f => f !== faction);
          if (otherFactions.length) {
            return interaction.reply({ embeds: [errorEmbed("Already in a Whitelist",
              `\`${playerId}\` already belongs to **${otherFactions.join(", ")}**. A player can only be in one whitelist.\n\nUse \`/whitelist transfer\` to move them, or \`/whitelist remove\` first.`)], flags: MessageFlags.Ephemeral });
          }
          const lines = readFactionFile(spawn);
          if (lines === null) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Add aborted to protect the roster.`)], flags: MessageFlags.Ephemeral });
          if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Already Whitelisted", `\`${playerId}\` is already in **${faction}** (rank: ${getFactionRank(faction, playerId) || "none"}).`)], flags: MessageFlags.Ephemeral });
          }
          const cap = getFactionCap(faction);
          if (lines.length >= cap) {
            return interaction.reply({ embeds: [errorEmbed("Whitelist Full", `**${faction}** is at capacity (**${lines.length}/${cap}** members).\n\nUse \`/whitelist setcap\` to increase the limit, or remove a member first.`)], flags: MessageFlags.Ephemeral });
          }
          lines.push(playerId);
          if (!writeFactionFile(spawn, lines)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to \`${spawn}\`. Check file permissions.`)], flags: MessageFlags.Ephemeral });
          }
          if (!addPlayerToRankFile(faction, playerId, rank)) {
            writeFactionFile(spawn, lines.filter(l => l.toLowerCase() !== playerId.toLowerCase()));
            return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not write to rank file for **${rank}**. Check file permissions.`)], flags: MessageFlags.Ephemeral });
          }
          await setFactionRank(faction, playerId, rank);
          writeFactionAudit({ action: "add", faction, playerId, rank, by: interaction.user.tag });
          writeModLog({ action: "faction-add", playerId, faction, rank, by: interaction.user.tag });
          const rankFile = getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a";
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(`Added to ${faction}`)
            .setDescription(`**${interaction.user.username}** added **${playerId}** to **${faction}** as ${rankBadge(faction, rank)} - ${lines.length}/${cap} in the roster.`)
            .setFooter({ text: "Main spawn file + rank file updated - audit logged" });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove ── */
        if (sub === "remove") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("whitelist");
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Whitelist", `Whitelist \`${faction}\` has no configured spawn file.`)], flags: MessageFlags.Ephemeral });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const lines = readFactionFile(spawn);
          if (!lines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read \`${spawn}\`.`)], flags: MessageFlags.Ephemeral });
          const idx = lines.findIndex(l => l.toLowerCase() === playerId.toLowerCase());
          if (idx === -1) {
            return interaction.reply({ embeds: [warningEmbed("Not Whitelisted", `\`${playerId}\` is not in the **${faction}** spawn list.`)], flags: MessageFlags.Ephemeral });
          }
          const oldRank = getFactionRank(faction, playerId);
          lines.splice(idx, 1);
          if (!writeFactionFile(spawn, lines)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to \`${spawn}\`.`)], flags: MessageFlags.Ephemeral });
          }
          removePlayerFromAllRankFiles(faction, playerId);
          removePlayerFromAllSubclassFiles(faction, playerId);   // leaving the faction clears sub-classes too
          await removeFactionRank(faction, playerId);
          writeFactionAudit({ action: "remove", faction, playerId, oldRank, by: interaction.user.tag });
          writeModLog({ action: "faction-remove", playerId, faction, oldRank, by: interaction.user.tag });
          const cap = getFactionCap(faction);
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle(`Removed from ${faction}`)
            .setDescription(`**${interaction.user.username}** removed **${playerId}** from **${faction}** (was ${rankBadge(faction, oldRank)}) - ${lines.length}/${cap} in the roster.`)
            .setFooter({ text: "Removed from spawn file and all rank files - audit logged" });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        return;
        },

  /* ─────────────────────────────────────────────────────
         PROMOTION / DEMOTION - move a member one rank up or down
         ───────────────────────────────────────────────────── */
  "promotion": (interaction, name) => changeRank(interaction, +1),
  "demotion":  (interaction, name) => changeRank(interaction, -1),

  /* ─────────────────────────────────────────────────────
         SUBCLASS - assign or remove a sub-class (not a rank)
         ───────────────────────────────────────────────────── */
  "subclass": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const subclass = interaction.options.getString("subclass");
        const removing = interaction.options.getBoolean("remove") === true;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const faction = (getPlayerFactions(playerId) || [])[0];
        if (!faction) return interaction.reply({ embeds: [warningEmbed("Not Whitelisted", `\`${playerId}\` is not in any whitelist. Add them with \`/whitelist add\` first.`)], flags: MessageFlags.Ephemeral });
        if (!hasModRole(interaction.member) && !hasWhitelistManageRole(interaction.member, faction)) {
          return interaction.reply({ embeds: [factionLeaderOnlyEmbed()], flags: MessageFlags.Ephemeral });
        }
        const subs = getFactionSubclasses(faction);
        if (!subs[subclass]) {
          const list = Object.keys(subs);
          return interaction.reply({ embeds: [errorEmbed("No Such Sub-class", `**${faction}** has ${list.length ? `these sub-classes: ${list.map(s => `**${s}**`).join(", ")}` : "no sub-classes"}.`)], flags: MessageFlags.Ephemeral });
        }
        const has = getPlayerSubclasses(faction, playerId).includes(subclass);
        if (removing && !has) return interaction.reply({ embeds: [warningEmbed("Not Assigned", `\`${playerId}\` does not hold the **${subclass}** sub-class.`)], flags: MessageFlags.Ephemeral });
        if (!removing && has) return interaction.reply({ embeds: [warningEmbed("Already Assigned", `\`${playerId}\` already holds the **${subclass}** sub-class.`)], flags: MessageFlags.Ephemeral });
        const ok = removing ? removePlayerFromSubclassFile(faction, playerId, subclass) : addPlayerToSubclassFile(faction, playerId, subclass);
        if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update the **${subclass}** file for **${faction}**. Nothing was changed.`)], flags: MessageFlags.Ephemeral });
        writeFactionAudit({ action: removing ? "subclass-remove" : "subclass-add", faction, playerId, subclass, by: interaction.user.tag });
        writeModLog({ action: removing ? "faction-subclass-remove" : "faction-subclass-add", playerId, faction, subclass, by: interaction.user.tag });
        const held = getPlayerSubclasses(faction, playerId);
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(removing ? "Sub-class Removed" : "Sub-class Assigned")
          .setDescription(`**${interaction.user.username}** ${removing ? "removed" : "assigned"} the **${subclass}** sub-class ${removing ? "from" : "to"} **${playerId}** in **${faction}**.\nSub-classes held: ${held.length ? held.map(s => `**${s}**`).join(", ") : "none"}.`);
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
        },
  };

  /* Move a member up (dir=+1) or down (dir=-1) one rank in whichever whitelist
     they belong to. Enforces the same per-faction manage gate as /whitelist. */
  async function changeRank(interaction, dir) {
    const playerId = sanitizeId(interaction.options.getString("playerid"));
    if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
    const faction = (getPlayerFactions(playerId) || [])[0];
    if (!faction) return interaction.reply({ embeds: [warningEmbed("Not Whitelisted", `\`${playerId}\` is not in any whitelist. Add them with \`/whitelist add\` first.`)], flags: MessageFlags.Ephemeral });
    if (!hasModRole(interaction.member) && !hasWhitelistManageRole(interaction.member, faction)) {
      return interaction.reply({ embeds: [factionLeaderOnlyEmbed()], flags: MessageFlags.Ephemeral });
    }
    const order   = getFactionRankOrder(faction);
    const current = getFactionRank(faction, playerId);
    const idx     = order.indexOf(current);
    const nidx    = (idx < 0 ? 0 : idx) + dir;
    if (nidx < 0)            return interaction.reply({ embeds: [warningEmbed("Lowest Rank", `\`${playerId}\` is already at the lowest rank in **${faction}** (**${order[0]}**).`)], flags: MessageFlags.Ephemeral });
    if (nidx >= order.length) return interaction.reply({ embeds: [warningEmbed("Highest Rank", `\`${playerId}\` is already at the highest rank in **${faction}** (**${order[order.length - 1]}**).`)], flags: MessageFlags.Ephemeral });
    const newRank = order[nidx];
    removePlayerFromAllRankFiles(faction, playerId);        // one rank at a time - clear old rank files (sub-classes are untouched)
    if (!addPlayerToRankFile(faction, playerId, newRank)) {
      return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update the rank files for **${faction}**. Nothing was changed.`)], flags: MessageFlags.Ephemeral });
    }
    await setFactionRank(faction, playerId, newRank);
    const up = dir > 0;
    writeFactionAudit({ action: up ? "promote" : "demote", faction, playerId, from: current, to: newRank, by: interaction.user.tag });
    writeModLog({ action: up ? "faction-promote" : "faction-demote", playerId, faction, from: current, to: newRank, by: interaction.user.tag });
    const embed = new EmbedBuilder().setColor(up ? NV.IRRAD_GREEN : NV.AMBER).setTitle(up ? "Promoted" : "Demoted")
      .setDescription(`**${interaction.user.username}** ${up ? "promoted" : "demoted"} **${playerId}** in **${faction}**: ${rankBadge(faction, current)} **${current}** → ${rankBadge(faction, newRank)} **${newRank}**.`);
    brand(embed); await logAction(embed);
    return interaction.reply({ embeds: [embed] });
  }
};
