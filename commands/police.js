/* ---------------- commands/police: /arrest /backgroundcheck /suspendrank ----------------
   RP police tools. /arrest is an interactive booking (pick a penal-code section,
   then charge(s), running total, confirm). /backgroundcheck shows warrants, arrests,
   and total jail time served. /suspendrank pulls a member's whitelist rank for a
   set time and auto-restores it. Split from commands/index.js - closes over ctx. */
const PENAL = require("../penal/codes");

module.exports = (ctx) => {
  const {
  EmbedBuilder, ActionRowBuilder, ButtonBuilder, ButtonStyle, StringSelectMenuBuilder, MessageFlags, NV,
  brand, emptyIdEmbed, errorEmbed, warningEmbed,
  sanitizeId, parseDuration, hasPoliceRole, hasModRole, policeOnlyEmbed, modOnlyEmbed,
  recordArrest, startSentence, logPolice, getArrests, totalJailServed, getWarrants,
  suspendRank, writeModLog,
  } = ctx;

  const chargeTime = (c) => c.untilSober ? "until sober" : `${c.min} min`;

  return {

  /* ── ARREST - interactive booking ── */
  "arrest": async (interaction, name) => {
        if (!hasPoliceRole(interaction.member)) return interaction.reply({ embeds: [policeOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const picked = [];   // charge objects, deduped by code

        const bookingEmbed = () => {
          const { minutes, untilSober } = PENAL.bookingTotal(picked.map(c => c.code));
          const lines = picked.length
            ? picked.map(c => `\`${c.code}\`  ${c.name} — ${c.cls} • ${chargeTime(c)}`).join("\n")
            : "*No charges yet.*";
          return brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`Booking: ${playerId}`)
            .setDescription(`Pick a section, then pick the charge(s). Add as many as needed, then confirm.\n\n**Charges so far (Total: ${PENAL.sentenceLabel(minutes, untilSober)})**\n${lines}`));
        };
        const sectionRow = () => new ActionRowBuilder().addComponents(
          new StringSelectMenuBuilder().setCustomId("arr_section").setPlaceholder("Choose a penal code section...")
            .addOptions(PENAL.sectionList().map(s => ({ label: `${s.num}. ${s.title}`.slice(0, 100), value: s.num, description: `${s.count} charges` }))));
        const chargeRow = (sec) => new ActionRowBuilder().addComponents(
          new StringSelectMenuBuilder().setCustomId("arr_charge").setPlaceholder(`Add charge(s) from section ${sec}...`)
            .setMinValues(1).setMaxValues(PENAL.SECTIONS[sec].length)
            .addOptions(PENAL.SECTIONS[sec].map(c => ({ label: `${c.code} ${c.name}`.slice(0, 100), value: c.code, description: `${c.cls} • ${chargeTime(c)}`.slice(0, 100) }))));
        const btnRow = () => new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("arr_confirm").setLabel("Confirm Arrest").setStyle(ButtonStyle.Danger).setDisabled(!picked.length),
          new ButtonBuilder().setCustomId("arr_cancel").setLabel("Cancel").setStyle(ButtonStyle.Secondary));

        await interaction.reply({ embeds: [bookingEmbed()], components: [sectionRow(), btnRow()], flags: MessageFlags.Ephemeral });
        const msg = await interaction.fetchReply();
        for (;;) {
          let i;
          try { i = await msg.awaitMessageComponent({ time: 180_000, filter: x => x.user.id === interaction.user.id }); }
          catch { return interaction.editReply({ components: [] }).catch(() => {}); }   // idle timeout

          if (i.isButton() && i.customId === "arr_cancel") {
            return i.update({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Arrest Cancelled").setDescription("No booking was recorded."))], components: [] }).catch(() => {});
          }
          if (i.isButton() && i.customId === "arr_confirm") {
            if (!picked.length) { await i.deferUpdate().catch(() => {}); continue; }
            await i.deferUpdate().catch(() => {});
            const entry = await recordArrest(playerId, picked, interaction.user.tag);
            const label = picked.length === 1 ? picked[0].name : `${picked.length} charges`;
            startSentence(playerId, label, entry.minutes, interaction.user.tag);
            writeModLog({ action: "arrest", playerId, charges: picked.map(c => c.code).join(","), minutes: entry.minutes, by: interaction.user.tag });
            const summary = brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle(`Arrest Confirmed: ${playerId}`)
              .setDescription(`Booked by **${interaction.user.username}** on **${picked.length}** charge${picked.length !== 1 ? "s" : ""}.\nSentence: **${PENAL.sentenceLabel(entry.minutes, entry.untilSober)}**.\n\n${picked.map(c => `\`${c.code}\` ${c.name}`).join("\n")}`));
            logPolice(summary);
            return interaction.editReply({ embeds: [summary], components: [] }).catch(() => {});
          }
          if (i.isStringSelectMenu() && i.customId === "arr_section") {
            const sec = i.values[0];
            await i.update({ embeds: [bookingEmbed()], components: [sectionRow(), chargeRow(sec), btnRow()] }).catch(() => {});
            continue;
          }
          if (i.isStringSelectMenu() && i.customId === "arr_charge") {
            for (const code of i.values) { const c = PENAL.getCharge(code); if (c && !picked.some(p => p.code === code)) picked.push(c); }
            await i.update({ embeds: [bookingEmbed()], components: [sectionRow(), btnRow()] }).catch(() => {});
            continue;
          }
        }
        },

  /* ── BACKGROUND CHECK ── */
  "backgroundcheck": async (interaction, name) => {
        if (!hasPoliceRole(interaction.member)) return interaction.reply({ embeds: [policeOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const warrants = getWarrants(playerId);
        const arrests  = getArrests(playerId);
        const served   = totalJailServed(playerId);
        const warrantStr = warrants.length ? warrants.map((w, i) => `\`${i + 1}\` ${w.reason} *(by ${w.by})*`).join("\n").slice(0, 1000) : "*None*";
        const arrestStr  = arrests.length
          ? arrests.slice(-10).reverse().map(a => `<t:${Math.floor(a.at / 1000)}:d> — ${a.charges.map(c => c.code).join(", ")} (${PENAL.sentenceLabel(a.minutes, a.untilSober)})`).join("\n").slice(0, 1000)
          : "*None*";
        const embed = brand(new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle(`Background Check: ${playerId}`)
          .addFields(
            { name: `Active Warrants (${warrants.length})`, value: warrantStr, inline: false },
            { name: `Arrests (${arrests.length})`,          value: arrestStr,  inline: false },
            { name: "Total jail time served",               value: `**${served} min**`, inline: true },
          ));
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ── SUSPEND RANK ── */
  "suspendrank": async (interaction, name) => {
        if (!hasModRole(interaction.member)) return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const timeStr  = interaction.options.getString("time");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const ms = parseDuration(timeStr);
        if (!ms || ms <= 0) return interaction.reply({ embeds: [errorEmbed("Bad Time", "Give a duration like `30m`, `2h`, or `1d`.")], flags: MessageFlags.Ephemeral });
        const minutes = Math.max(1, Math.round(ms / 60_000));
        const r = await suspendRank(playerId, minutes, interaction.user.tag);
        if (!r.ok) return interaction.reply({ embeds: [warningEmbed("No Rank to Suspend", `\`${playerId}\` isn't in any whitelist, so there's no rank to suspend.`)], flags: MessageFlags.Ephemeral });
        writeModLog({ action: "suspendrank", playerId, faction: r.faction, rank: r.rank, minutes, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`Rank Suspended: ${playerId}`)
          .setDescription(`**${interaction.user.username}** suspended **${playerId}**'s ${r.rank ? `**${r.rank}** ` : ""}rank in **${r.faction}** for **${minutes} min**. It restores automatically <t:${Math.floor((Date.now() + ms) / 1000)}:R>.`));
        logPolice(embed);
        return interaction.reply({ embeds: [embed] });
        },

  };
};
