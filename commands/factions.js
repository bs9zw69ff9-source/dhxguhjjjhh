/* ---------------- commands/factions: /faction ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  ALL_FACTIONS, DIVIDER, EmbedBuilder, FACTION_BAK_DIR, MessageFlags, NV,
  SPAWN_FILE_MAP, addPlayerToRankFile, adminOnlyEmbed, bar, brand, confirmDialog,
  countFactionRank, emptyIdEmbed, errorEmbed, factionLeaderStrictEmbed, formatPlaytime, getFactionCap,
  getFactionDefaultRank, getFactionMembers, getFactionRank, getFactionRankBadge, getFactionRankCap, getFactionRankConfig,
  getFactionRankOrder, getPlayerFactions, getPlayerRanks, hasAdminRole, hasFactionLeaderRole, hasModRole,
  isOwner, loadFactionAudit, loadPlaytime, logAction, logger, meter,
  modOnlyEmbed, ownerOnlyEmbed, paginate, path, randomQuote, rankBadge,
  rankHasRoom, rankLabel, readFactionFile, removeFactionRank, removePlayerFromAllRankFiles, removePlayerFromRankFile,
  sanitizeId, setFactionCap, setFactionRank, setFactionRankCap, spawn, successEmbed,
  update, warningEmbed, wipeFaction, writeFactionAudit, writeFactionFile, writeModLog,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         FACTION — all subcommands
         ───────────────────────────────────────────────────── */
  "faction": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();

        /* ── setcap (admin only) ── */
        if (sub === "setcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("faction");
          const cap     = interaction.options.getInteger("cap");
          await setFactionCap(faction, cap);
          writeModLog({ action: "faction-setcap", faction, cap, by: interaction.user.tag });
          const spawn   = SPAWN_FILE_MAP[faction];
          const current = spawn ? (readFactionFile(spawn)?.length ?? 0) : 0;
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("Faction Size Cap Updated")
            .setDescription(`${interaction.user} set **${faction}**'s cap to **${cap}** members (currently ${current}/${cap}${current > cap ? " — over cap!" : ""}).`)
            .setFooter({ text: "Cap enforced on /faction add" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        /* ── setrankcap (admin only) ── */
        if (sub === "setrankcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("faction");
          const rank    = interaction.options.getString("rank");
          const cap     = interaction.options.getInteger("cap");
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          await setFactionRankCap(faction, rank, cap);
          writeModLog({ action: "faction-setrankcap", faction, rank, cap, by: interaction.user.tag });
          const current = countFactionRank(faction, rank);
          const capStr  = cap > 0 ? `**${cap}**` : "**Unlimited**";
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("Rank Cap Updated")
            .setDescription(`${interaction.user} set **${faction}** ${rankBadge(faction, rank)}'s cap to ${capStr} (currently ${current}${cap > 0 ? `/${cap}${current > cap ? " — over cap!" : ""}` : ""}).`)
            .setFooter({ text: cap > 0 ? "Cap enforced on add / rank / transfer" : "Rank is now uncapped" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        /* ── wipe (owner only) — reset one faction's whitelist, or every faction ── */
        if (sub === "wipe") {
          if (!isOwner(interaction.user.id)) {
            return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("faction");
          const targets = faction ? [faction] : ALL_FACTIONS;
          const counts  = targets.map(f => ({ faction: f, count: (readFactionFile(SPAWN_FILE_MAP[f]) ?? []).length }));
          const total   = counts.reduce((s, c) => s + c.count, 0);
          if (!total) {
            return interaction.reply({ embeds: [successEmbed("Nothing to Wipe",
              faction ? `**${faction}** has no members.` : "No faction has any members.")], flags: MessageFlags.Ephemeral });
          }
          const preview = counts.filter(c => c.count).map(c => `- **${c.faction}**: ${c.count} member${c.count !== 1 ? "s" : ""}`).join("\n");
          const go = await confirmDialog(interaction, {
            title: faction ? `Wipe ${faction}'s whitelist?` : "Wipe ALL faction whitelists?",
            body: `Clears membership and every rank file${faction ? "" : ", for every faction"} — **${total}** player${total !== 1 ? "s" : ""} total.\nA pre-wipe snapshot of each file is kept in \`${FACTION_BAK_DIR}\`.\n\n${preview}`,
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
            faction ? `${faction} Whitelist Wiped` : "All Faction Whitelists Wiped",
            `Cleared **${wipedMembers}** player${wipedMembers !== 1 ? "s" : ""} across **${wipedFactions}** faction${wipedFactions !== 1 ? "s" : ""}.`);
          await logAction(embed);
          return interaction.editReply({ embeds: [embed], components: [], keepEmbeds: true });
        }

        /* ── list (public, paginated) ── */
        if (sub === "list") {
          const faction  = interaction.options.getString("faction");
          const members  = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], flags: MessageFlags.Ephemeral });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} — Empty Roster`)
                .setDescription("No players are currently whitelisted for this faction.\n\nUse `/faction add` to enlist someone.")
                .setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          const cap = getFactionCap(faction);
          const summary = getFactionRankOrder(faction).slice().reverse().map(r => {
            const n = countFactionRank(faction, r);   // file-based: counts every holder (a member may hold several ranks)
            const rcap = getFactionRankCap(faction, r);
            if (!n && !rcap) return null;
            const count = rcap ? `${n}/${rcap}` : `${n}`;
            return `${getFactionRankBadge(faction, r)} ${r}: **${count}**`;
          }).filter(Boolean).join("  ·  ");
          const lines = members.map((m, i) =>
            `\`${String(i + 1).padStart(2, "0")}\`  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  ·  *${(m.ranks || [m.rank]).join(", ")}*`);
          const header = `**${members.length}/${cap}** members${members.length > cap ? " over cap" : ""}  ·  ${summary}`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`${faction} — Roster`)
              .setDescription(`${header}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
              .setFooter({ text: SPAWN_FILE_MAP[faction] }),
            { perPage: 20 });
        }

        /* ── playtime (public, paginated) — roster ranked by time served ── */
        if (sub === "playtime") {
          const faction = interaction.options.getString("faction");
          const members = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], flags: MessageFlags.Ephemeral });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} — Empty Roster`)
                .setDescription("No players are currently whitelisted for this faction.\n\nUse `/faction add` to enlist someone.")
                .setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          // Playtime keys are display-cased names — match members case-insensitively.
          const byName = new Map(Object.entries(loadPlaytime()).map(([n, m]) => [n.toLowerCase(), Number(m) || 0]));
          const ranked = members
            .map(m => ({ ...m, minutes: byName.get(m.playerId.toLowerCase()) ?? null }))
            .sort((a, b) => (b.minutes ?? -1) - (a.minutes ?? -1));
          const top   = ranked[0]?.minutes || 1;
          const total = ranked.reduce((s, m) => s + (m.minutes ?? 0), 0);
          const lines = ranked.map((m, i) => {
            const time  = m.minutes !== null ? formatPlaytime(m.minutes) : "*No record*";
            const meter = m.minutes !== null && i < 5 ? `  \`${bar(m.minutes, top, 8)}\`` : "";
            return `${rankLabel(i)}  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  ·  ${time}${meter}`;
          });
          const header = `**${members.length}** member${members.length !== 1 ? "s" : ""}  ·  **${formatPlaytime(total)}** combined`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`${faction} — Playtime`)
              .setDescription(`${header}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
              .setFooter({ text: "Playtime sampled every 60s while online" }),
            { perPage: 20 });
        }

        /* ── audit (public, paginated) ── */
        if (sub === "audit") {
          const faction   = interaction.options.getString("faction");
          const allAudit  = loadFactionAudit().filter(e => e.faction === faction).reverse();
          if (!allAudit.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} — Audit Log`)
                .setDescription("No faction changes recorded yet for this faction.")
                .setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          const ACTION_ICONS = { "add": "", "remove": "", "rank": "", "transfer-in": "", "transfer-out": "", "wipe": "" };
          const lines = allAudit.map(e => {
            const ts      = Math.floor(e.at / 1000);
            const icon    = ACTION_ICONS[e.action] ?? "";
            const subject = e.action === "wipe" ? `${e.count} member${e.count !== 1 ? "s" : ""}` : `**${e.playerId}**`;
            const detail  = e.rank ? ` → **${e.rank}**` : e.oldRank ? ` *(was ${e.oldRank})*` : "";
            return `${icon}  \`${e.action}\`  ${subject}${detail}  ·  by *${e.by}*  ·  <t:${ts}:R>`;
          });
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.AMBER)
              .setTitle(`${faction} — Audit Log`)
              .setDescription(`**${allAudit.length}** total changes *(newest first)*\n\n${DIVIDER}\n${pageLines.join("\n")}`),
            { perPage: 15, ephemeral: true });
        }

        /* ── rank (Faction Leader ONLY) ── */
        if (sub === "rank") {
          if (!hasFactionLeaderRole(interaction.member)) {
            return interaction.reply({ embeds: [factionLeaderStrictEmbed()], flags: MessageFlags.Ephemeral });
          }
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const rank     = interaction.options.getString("rank");
          const removing = interaction.options.getBoolean("remove") === true;
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          const spawn = SPAWN_FILE_MAP[faction];
          const lines = readFactionFile(spawn);
          if (!lines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**.`)], flags: MessageFlags.Ephemeral });
          if (!lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Not a Member", `\`${playerId}\` is not whitelisted in **${faction}**.\n\nUse \`/faction add\` first.`)], flags: MessageFlags.Ephemeral });
          }
          const had = getPlayerRanks(faction, playerId);
          if (removing) {
            if (!had.includes(rank)) {
              return interaction.reply({ embeds: [warningEmbed("Rank Not Held", `\`${playerId}\` doesn't hold **${rank}** in **${faction}**.\n\nThey hold: ${had.join(", ") || "none"}.`)], flags: MessageFlags.Ephemeral });
            }
            if (!removePlayerFromRankFile(faction, playerId, rank)) {
              return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not update the **${rank}** file for **${faction}** — check the server path/permissions. Nothing was changed.`)], flags: MessageFlags.Ephemeral });
            }
          } else {
            if (had.includes(rank)) {
              return interaction.reply({ embeds: [warningEmbed("Already Holds Rank", `\`${playerId}\` already holds **${rank}** in **${faction}**.\n\nThey hold: ${had.join(", ")}.`)], flags: MessageFlags.Ephemeral });
            }
            const room = rankHasRoom(faction, rank);   // a member can hold MULTIPLE ranks; cap is per rank file
            if (!room.ok) {
              return interaction.reply({ embeds: [errorEmbed("Rank Full",
                `**${rank}** in **${faction}** is at its cap (**${room.count}/${room.cap}**).\n\nRaise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
            }
            if (!addPlayerToRankFile(faction, playerId, rank)) {
              return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not update the **${rank}** file for **${faction}** — check the server path/permissions. Nothing was changed.`)], flags: MessageFlags.Ephemeral });
            }
          }
          const now = getPlayerRanks(faction, playerId);
          await setFactionRank(faction, playerId, now[now.length - 1] ?? getFactionDefaultRank(faction));   // track highest as primary
          writeFactionAudit({ action: "rank", faction, playerId, rank: removing ? `-${rank}` : rank, by: interaction.user.tag });
          writeModLog({ action: removing ? "faction-unrank" : "faction-rank", playerId, faction, rank, by: interaction.user.tag });
          const rankFile = getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a";
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(removing ? "Faction Rank Removed" : "Faction Rank Added")
            .setDescription(`> *${randomQuote("faction")}*\n\n${interaction.user} ${removing ? "removed" : "added"} **${playerId}**'s ${rankBadge(faction, rank)} rank in **${faction}** (${removing ? "removed from" : "added to"} \`${rankFile}\`). They now hold: ${now.length ? now.map(r => `**${r}**`).join(", ") : "*no ranks*"}.`)
            .setFooter({ text: "Members can hold multiple ranks · rank files updated on disk" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── transfer (Mod+) ── */
        if (sub === "transfer") {
          if (!hasModRole(interaction.member)) {
            return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const playerId    = sanitizeId(interaction.options.getString("playerid"));
          const fromFaction = interaction.options.getString("from_faction");
          const toFaction   = interaction.options.getString("to_faction");
          const rawRank     = interaction.options.getString("rank");
          const newRank     = rawRank ?? getFactionDefaultRank(toFaction);
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          if (fromFaction === toFaction) {
            return interaction.reply({ embeds: [errorEmbed("Same Faction", "Source and destination factions must be different.")], flags: MessageFlags.Ephemeral });
          }
          const toValidRanks = getFactionRankOrder(toFaction);
          if (!toValidRanks.includes(newRank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${newRank}** is not a valid rank for **${toFaction}**.\n\nValid ranks: ${toValidRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          const fromSpawn = SPAWN_FILE_MAP[fromFaction];
          const fromLines = readFactionFile(fromSpawn);
          if (!fromLines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${fromFaction}**.`)], flags: MessageFlags.Ephemeral });
          if (!fromLines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Not a Member", `\`${playerId}\` is not whitelisted in **${fromFaction}**.`)], flags: MessageFlags.Ephemeral });
          }
          const toSpawn = SPAWN_FILE_MAP[toFaction];
          const toLines = readFactionFile(toSpawn);
          if (toLines === null) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${toFaction}**. Transfer aborted to protect the roster.`)], flags: MessageFlags.Ephemeral });
          const toCap   = getFactionCap(toFaction);
          if (toLines.length >= toCap) {
            return interaction.reply({ embeds: [errorEmbed("Faction Full", `**${toFaction}** is at capacity (**${toLines.length}/${toCap}** members).\n\nIncrease the cap with \`/faction setcap\` or remove a member first.`)], flags: MessageFlags.Ephemeral });
          }
          if (toLines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Already a Member", `\`${playerId}\` is already whitelisted in **${toFaction}**.`)], flags: MessageFlags.Ephemeral });
          }
          const toRoom = rankHasRoom(toFaction, newRank);
          if (!toRoom.ok) {
            return interaction.reply({ embeds: [errorEmbed("Rank Full",
              `**${newRank}** in **${toFaction}** is at its cap (**${toRoom.count}/${toRoom.cap}**).\n\nChoose a different rank, or raise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
          }
          const oldRank = getFactionRank(fromFaction, playerId);
          const updatedFrom = fromLines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
          if (!writeFactionFile(fromSpawn, updatedFrom)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${fromSpawn}\`. Check file permissions.`)], flags: MessageFlags.Ephemeral });
          }
          removePlayerFromAllRankFiles(fromFaction, playerId);
          await removeFactionRank(fromFaction, playerId);
          // Re-read the destination roster NOW: the await above yielded the event loop,
          // and a concurrent /faction add or whitelist-panel claim landing in between
          // would be silently clobbered if we wrote back the stale pre-await copy.
          const toNow = readFactionFile(toSpawn) ?? toLines;
          if (!toNow.some(l => l.toLowerCase() === playerId.toLowerCase())) toNow.push(playerId);
          if (!writeFactionFile(toSpawn, toNow)) {
            // Re-read fromSpawn fresh: the awaited removeFactionRank above yielded the
            // event loop, so a concurrent faction edit may have changed it. Restore the
            // player without clobbering that change (mirror of the toSpawn re-read).
            const fromBack = readFactionFile(fromSpawn) ?? updatedFrom;
            if (!fromBack.some(l => l.toLowerCase() === playerId.toLowerCase())) fromBack.push(playerId);
            writeFactionFile(fromSpawn, fromBack);
            addPlayerToRankFile(fromFaction, playerId, oldRank);
            await setFactionRank(fromFaction, playerId, oldRank);
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${toSpawn}\`. Transfer rolled back.`)], flags: MessageFlags.Ephemeral });
          }
          const rankFileOk = addPlayerToRankFile(toFaction, playerId, newRank);
          if (!rankFileOk) logger.warn("Faction", `Transfer: membership moved but rank file write failed for ${playerId} -> ${toFaction}/${newRank}`);
          await setFactionRank(toFaction, playerId, newRank);
          writeFactionAudit({ action: "transfer-out", faction: fromFaction, playerId, oldRank, by: interaction.user.tag });
          writeFactionAudit({ action: "transfer-in",  faction: toFaction,   playerId, rank: newRank, by: interaction.user.tag });
          writeModLog({ action: "faction-transfer", playerId, fromFaction, toFaction, oldRank, newRank, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Faction Transfer Complete")
            .setDescription(`> *${randomQuote("faction")}*\n\n${interaction.user} moved **${playerId}** from **${fromFaction}** (${rankBadge(fromFaction, oldRank)}) to **${toFaction}** (${rankBadge(toFaction, newRank)}) — ${toNow.length}/${toCap} in the new roster.\n${rankFileOk ? "Rank files updated on both ends." : `FAILED to write \`${getFactionRankConfig(toFaction)?.rankFiles[newRank] ?? "n/a"}\` — re-run /faction rank.`}`)
            .setFooter({ text: "Both faction files updated · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── add ── */
        if (sub === "add") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const rawRank  = interaction.options.getString("rank");
          const rank     = rawRank ?? getFactionDefaultRank(faction);
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Faction", `Faction \`${faction}\` has no configured spawn file.`)], flags: MessageFlags.Ephemeral });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          // One faction per player - block if they're already in a different faction.
          const otherFactions = (getPlayerFactions(playerId) || []).filter(f => f !== faction);
          if (otherFactions.length) {
            return interaction.reply({ embeds: [errorEmbed("Already in a Faction",
              `\`${playerId}\` already belongs to **${otherFactions.join(", ")}**. A player can only be in one faction.\n\nUse \`/faction transfer\` to move them, or \`/faction remove\` first.`)], flags: MessageFlags.Ephemeral });
          }
          const lines = readFactionFile(spawn);
          if (lines === null) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Add aborted to protect the roster.`)], flags: MessageFlags.Ephemeral });
          if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Already Whitelisted", `\`${playerId}\` is already in **${faction}** (ranks: ${(getPlayerRanks(faction, playerId).join(", ") || "none")}).\n\nUse \`/faction rank\` to add or remove ranks — a member can hold several.`)], flags: MessageFlags.Ephemeral });
          }
          const cap = getFactionCap(faction);
          if (lines.length >= cap) {
            return interaction.reply({ embeds: [errorEmbed("Faction Full", `**${faction}** is at capacity (**${lines.length}/${cap}** members).\n\nUse \`/faction setcap\` to increase the limit, or remove a member first.`)], flags: MessageFlags.Ephemeral });
          }
          const addRoom = rankHasRoom(faction, rank);
          if (!addRoom.ok) {
            return interaction.reply({ embeds: [errorEmbed("Rank Full",
              `**${rank}** in **${faction}** is at its cap (**${addRoom.count}/${addRoom.cap}**).\n\nAdd them at a different rank, or raise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
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
            .setDescription(`> *${randomQuote("faction")}*\n\n${interaction.user} added **${playerId}** to **${faction}** as ${rankBadge(faction, rank)} — ${lines.length}/${cap} in the roster.`)
            .setFooter({ text: "Main spawn file + rank file updated · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove ── */
        if (sub === "remove") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Faction", `Faction \`${faction}\` has no configured spawn file.`)], flags: MessageFlags.Ephemeral });
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
          await removeFactionRank(faction, playerId);
          writeFactionAudit({ action: "remove", faction, playerId, oldRank, by: interaction.user.tag });
          writeModLog({ action: "faction-remove", playerId, faction, oldRank, by: interaction.user.tag });
          const cap = getFactionCap(faction);
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle(`Removed from ${faction}`)
            .setDescription(`${interaction.user} removed **${playerId}** from **${faction}** (was ${rankBadge(faction, oldRank)}) — ${lines.length}/${cap} in the roster.`)
            .setFooter({ text: "Removed from spawn file and all rank files · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        return;
        },
  };
};
