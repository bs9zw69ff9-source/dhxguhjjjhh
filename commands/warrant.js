/* ---------------- commands/warrant: /warrant check|give|remove ----------------
   A police warrant board. Gated to the Police Officer role (admins/owners pass
   too). `give` requires a reason and STACKS - a player can hold several warrants.
   `check` shows one player's stack (numbered) or lists everyone with warrants.
   `remove` clears one warrant by number, or all of a player's when no number is
   given. Split from commands/index.js - receives (interaction, name) and closes
   over the shared ctx injected from index.js via the dispatcher. */
module.exports = (ctx) => {
  const {
  EmbedBuilder, MessageFlags, NV, brand,
  emptyIdEmbed, errorEmbed, successEmbed, warningEmbed,
  sanitizeId, hasPoliceRole, policeOnlyEmbed,
  loadWarrants, getWarrants, addWarrant, removeWarrant,
  writeModLog, logAction, paginate,
  } = ctx;

  const fmtWhen = (at) => `<t:${Math.floor((at || Date.now()) / 1000)}:R>`;

  return {

  "warrant": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();

        // Every subcommand is police-only (hasPoliceRole lets owners/admins through).
        if (!hasPoliceRole(interaction.member)) {
          return interaction.reply({ embeds: [policeOnlyEmbed()], flags: MessageFlags.Ephemeral });
        }

        /* ── give (reason required; warrants stack) ── */
        if (sub === "give") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const reason   = (interaction.options.getString("reason") || "").trim();
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          if (!reason)   return interaction.reply({ embeds: [errorEmbed("Reason Required", "You have to give a reason for the warrant.")], flags: MessageFlags.Ephemeral });
          const count = await addWarrant(playerId, reason, interaction.user.tag, interaction.user.id);
          writeModLog({ action: "warrant-give", playerId, reason, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.RUST_RED)
            .setTitle(`Warrant Issued - ${playerId}`)
            .setDescription(`**${interaction.user.username}** put out a warrant on **${playerId}**.\nReason: ${reason}\nThey now have **${count}** active warrant${count !== 1 ? "s" : ""}.`)
            .setFooter({ text: "Police warrant" });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove (one by number, or all) ── */
        if (sub === "remove") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const number   = interaction.options.getInteger("number");
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const current = getWarrants(playerId);
          if (!current.length) return interaction.reply({ embeds: [warningEmbed("No Warrant", `\`${playerId}\` has no active warrants.`)], flags: MessageFlags.Ephemeral });
          if (number != null && (number < 1 || number > current.length)) {
            return interaction.reply({ embeds: [errorEmbed("No Such Warrant", `\`${playerId}\` has **${current.length}** warrant${current.length !== 1 ? "s" : ""} (1-${current.length}). Pick a number in range, or omit it to clear them all.`)], flags: MessageFlags.Ephemeral });
          }
          const { removed, remaining } = await removeWarrant(playerId, number ?? null);
          writeModLog({ action: "warrant-remove", playerId, removed: removed.length, by: interaction.user.tag });
          const what = number != null
            ? `warrant #${number} (${removed[0]?.reason ?? "?"})`
            : `all **${removed.length}** warrant${removed.length !== 1 ? "s" : ""}`;
          const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN)
            .setTitle(`Warrant Cleared - ${playerId}`)
            .setDescription(`**${interaction.user.username}** cleared ${what} for **${playerId}**.\n**${remaining}** warrant${remaining !== 1 ? "s" : ""} left.`)
            .setFooter({ text: "Police warrant" });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── check (one player's stack, or everyone when blank) ── */
        if (sub === "check") {
          const raw = interaction.options.getString("playerid");
          if (raw && raw.trim()) {
            const playerId = sanitizeId(raw);
            const list = getWarrants(playerId);
            if (!list.length) return interaction.reply({ embeds: [successEmbed(`No Warrant - ${playerId}`, `\`${playerId}\` has no active warrants.`)], flags: MessageFlags.Ephemeral });
            const lines = list.map((w, i) => `\`#${i + 1}\`  ${w.reason}  *(by ${w.by}, ${fmtWhen(w.at)})*`);
            const embed = brand(new EmbedBuilder().setColor(NV.RUST_RED)
              .setTitle(`Active Warrants - ${playerId} (${list.length})`)
              .setDescription(lines.join("\n"))
              .setFooter({ text: "Use /warrant remove <player> [number] to clear one" }));
            return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
          }
          // list everyone who has warrants, most stacked first
          const all = loadWarrants();
          const rows = Object.values(all)
            .map(v => (Array.isArray(v) ? v : (v ? [v] : [])))
            .filter(l => l.length)
            .map(l => ({ playerId: l[0].playerId, count: l.length, latest: Math.max(...l.map(w => w.at || 0)), reason: l[l.length - 1]?.reason }))
            .sort((a, b) => b.count - a.count || b.latest - a.latest);
          if (!rows.length) return interaction.reply({ embeds: [successEmbed("No Active Warrants", "There are no active warrants right now.")], flags: MessageFlags.Ephemeral });
          const total = rows.reduce((s, r) => s + r.count, 0);
          const lines = rows.map((r, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${r.playerId}**  -  **${r.count}** warrant${r.count !== 1 ? "s" : ""}  *(latest: ${r.reason})*`);
          return paginate(interaction, lines, (pageLines) =>
            brand(new EmbedBuilder().setColor(NV.RUST_RED)
              .setTitle(`Active Warrants - ${total} across ${rows.length} player${rows.length !== 1 ? "s" : ""}`)
              .setDescription(pageLines.join("\n"))
              .setFooter({ text: "Police warrant board - /warrant check <player> for details" })),
            { perPage: 20 });
        }

        return;
        },
  };
};
