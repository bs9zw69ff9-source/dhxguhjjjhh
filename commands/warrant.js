/* ---------------- commands/warrant: /warrant check|give|remove ----------------
   A police warrant board. Gated to the Police Officer role (admins/owners pass
   too). `give` requires a reason; one active warrant per player. Split from
   commands/index.js - receives (interaction, name) and closes over the shared
   ctx injected from index.js via the dispatcher. */
module.exports = (ctx) => {
  const {
  EmbedBuilder, MessageFlags, NV, brand,
  emptyIdEmbed, errorEmbed, successEmbed, warningEmbed,
  sanitizeId, hasPoliceRole, policeOnlyEmbed,
  loadWarrants, getWarrant, setWarrant, removeWarrant,
  writeModLog, logAction, paginate,
  } = ctx;

  return {

  "warrant": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();

        // Every subcommand is police-only (hasPoliceRole lets owners/admins through).
        if (!hasPoliceRole(interaction.member)) {
          return interaction.reply({ embeds: [policeOnlyEmbed()], flags: MessageFlags.Ephemeral });
        }

        /* ── give (reason required) ── */
        if (sub === "give") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const reason   = (interaction.options.getString("reason") || "").trim();
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          if (!reason)   return interaction.reply({ embeds: [errorEmbed("Reason Required", "You have to give a reason for the warrant.")], flags: MessageFlags.Ephemeral });
          const existing = getWarrant(playerId);
          await setWarrant(playerId, reason, interaction.user.tag, interaction.user.id);
          writeModLog({ action: "warrant-give", playerId, reason, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.RUST_RED)
            .setTitle(existing ? `Warrant Updated - ${playerId}` : `Warrant Issued - ${playerId}`)
            .setDescription(`**${interaction.user.username}** ${existing ? "updated the warrant for" : "put out a warrant for"} **${playerId}**.\nReason: ${reason}`)
            .setFooter({ text: "Police warrant" });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove ── */
        if (sub === "remove") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const existing = getWarrant(playerId);
          if (!existing) return interaction.reply({ embeds: [warningEmbed("No Warrant", `\`${playerId}\` has no active warrant.`)], flags: MessageFlags.Ephemeral });
          await removeWarrant(playerId);
          writeModLog({ action: "warrant-remove", playerId, oldReason: existing.reason, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN)
            .setTitle(`Warrant Cleared - ${playerId}`)
            .setDescription(`**${interaction.user.username}** cleared the warrant for **${playerId}** (was: ${existing.reason}).`)
            .setFooter({ text: "Police warrant" });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── check (one player, or list all when blank) ── */
        if (sub === "check") {
          const raw = interaction.options.getString("playerid");
          if (raw && raw.trim()) {
            const playerId = sanitizeId(raw);
            const w = getWarrant(playerId);
            if (!w) return interaction.reply({ embeds: [successEmbed(`No Warrant - ${playerId}`, `\`${playerId}\` has no active warrant.`)], flags: MessageFlags.Ephemeral });
            const embed = brand(new EmbedBuilder().setColor(NV.RUST_RED)
              .setTitle(`Active Warrant - ${playerId}`)
              .setDescription(`Reason: ${w.reason}\nIssued by ${w.by} <t:${Math.floor(w.at / 1000)}:R>.`)
              .setFooter({ text: "Police warrant" }));
            return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
          }
          const all = Object.values(loadWarrants());
          if (!all.length) return interaction.reply({ embeds: [successEmbed("No Active Warrants", "There are no active warrants right now.")], flags: MessageFlags.Ephemeral });
          const lines = all.sort((a, b) => b.at - a.at)
            .map((w, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${w.playerId}**  -  ${w.reason}  *(by ${w.by})*`);
          return paginate(interaction, lines, (pageLines) =>
            brand(new EmbedBuilder().setColor(NV.RUST_RED)
              .setTitle(`Active Warrants (${all.length})`)
              .setDescription(pageLines.join("\n"))
              .setFooter({ text: "Police warrant board" })),
            { perPage: 20 });
        }

        return;
        },
  };
};
