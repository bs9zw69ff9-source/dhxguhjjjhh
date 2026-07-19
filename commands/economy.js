/* ---------------- commands/economy: /givecaps /adjustcaps ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  EmbedBuilder, MessageFlags, NV,
  brand, emptyIdEmbed, errorEmbed,
  logAction, mutateBalance, randomQuote, sanitizeId,
  writeModLog,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         GIVECAPS
         ───────────────────────────────────────────────────── */
  "givecaps": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const amount   = interaction.options.getInteger("amount");
        const reason   = interaction.options.getString("reason") ?? "Cap gift";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        // Serialized through the casino ledger queue so a concurrent game can't race this write.
        const r = await mutateBalance(playerId, (bal) => bal + amount);
        if (!r.ok) return interaction.editReply({ embeds: [errorEmbed("Ledger Write Failed", "Check `MODSAVE_PATH`.")] });
        const newBal = r.after;
        writeModLog({ action: "givecaps", playerId, amount, reason, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Credits Given")
          .setDescription(`**${interaction.user.username}** gave **${playerId}** **+${amount.toLocaleString()} credits** - new balance **${newBal.toLocaleString()} credits**. ${reason}`)
          .setFooter({ text: randomQuote("caps") });
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         TRANSFERCAPS
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         ADJUSTCAPS
         ───────────────────────────────────────────────────── */
  "adjustcaps": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const amount   = interaction.options.getInteger("amount");
        const reason   = interaction.options.getString("reason") ?? "Manual adjustment";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        // Serialized through the casino ledger queue so a concurrent game can't race this write.
        const r = await mutateBalance(playerId, (bal) => Math.max(0, bal + amount));
        if (!r.ok) return interaction.editReply({ embeds: [errorEmbed("Write Failed", "Check `MODSAVE_PATH`.")] });
        const newBal = r.after;
        writeModLog({ action: "adjustcaps", playerId, amount, reason, by: interaction.user.tag });
        const pos = amount >= 0;
        const embed = new EmbedBuilder().setColor(pos ? NV.IRRAD_GREEN : NV.RUST_RED)
          .setTitle(`Credits ${pos ? "Credited" : "Debited"}`)
          .setDescription(`**${interaction.user.username}** ${pos ? "credited" : "debited"} **${playerId}** **${pos ? "+" : ""}${amount.toLocaleString()} credits** - new balance **${newBal.toLocaleString()} credits**. ${reason}`)
          .setFooter({ text: "Manual cap adjustment - logged" });
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },
  };
};
