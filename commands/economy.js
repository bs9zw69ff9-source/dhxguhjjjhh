/* ---------------- commands/economy: /addwage /removewage /wagelist /checkbalance /givecaps /adjustcaps ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  DIVIDER, EmbedBuilder, MessageFlags, NV, WAGE_INTERVAL_MS, WAGE_TIERS,
  brand, emptyIdEmbed, errorEmbed, fs, getPlayerFilePath, loadWages,
  logAction, paginate, randomQuote, readPlayerBalance, sanitizeId, saveWages,
  warningEmbed, writeModLog, writePlayerBalance,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         ADDWAGE
         ───────────────────────────────────────────────────── */
  "addwage": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const tierKey  = interaction.options.getString("tier");
        const tier     = WAGE_TIERS[tierKey];
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        if (!tier)     return interaction.reply({ embeds: [errorEmbed("Invalid Tier", "Unknown payment tier.")], flags: MessageFlags.Ephemeral });
        const wages    = loadWages();
        const existing = wages.find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        if (!tier.weekly) {
          const current = readPlayerBalance(playerId) ?? 0;
          const newBal  = current + tier.amount;
          if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Ledger Write Failed", `Could not deposit **${tier.amount} credits** to \`${playerId}\`. Check \`MODSAVE_PATH\`.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "givecaps", playerId, amount: tier.amount, reason: "Mercenary payment", by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Mercenary Payment Issued")
            .setDescription(`> *"Credits now. No strings attached."*\n\n${interaction.user} paid **${playerId}** **${tier.amount.toLocaleString()} credits** — new balance **${newBal.toLocaleString()} credits**.`)
            .setFooter({ text: randomQuote("caps") }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }
        if (existing?.tier === tierKey) {
          const ts = Math.floor(existing.addedAt / 1000);
          return interaction.reply({ embeds: [warningEmbed("Already on Payroll", `\`${playerId}\` is already enrolled as **${tier.label}** (+${tier.amount}/wk).\n**Enrolled:** <t:${ts}:F> by **${existing.addedBy}**\n\nUse \`/removewage\` first to change tier.`)], flags: MessageFlags.Ephemeral });
        }
        if (existing) {
          const old = WAGE_TIERS[existing.tier];
          existing.tier = tierKey; existing.updatedAt = Date.now(); existing.updatedBy = interaction.user.tag;
          saveWages(wages);
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Payroll Tier Updated")
            .setDescription(`${interaction.user} moved **${playerId}** from ${old?.label ?? "?"} (+${old?.amount ?? "?"}/wk) to **${tier.label}** (+${tier.amount}/wk).`)
            .setFooter({ text: "Payroll updated — takes effect next cycle" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }
        wages.push({ playerId, tier: tierKey, addedBy: interaction.user.tag, addedAt: Date.now(), lastPaidAt: null, updatedAt: null, updatedBy: null });
        saveWages(wages);
        const bal = readPlayerBalance(playerId);
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Player Added to Payroll")
          .setDescription(`> *"A fair day's work for a fair day's pay."*\n\n${interaction.user} enrolled **${playerId}** as **${tier.label}** (+${tier.amount} credits/week). Balance: ${bal !== null ? `${bal.toLocaleString()} credits` : "*no ledger*"}. First payout within 7 days.`)
          .setFooter({ text: randomQuote("wages") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         REMOVEWAGE
         ───────────────────────────────────────────────────── */
  "removewage": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const wages   = loadWages();
        const removed = wages.find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        if (!removed) return interaction.reply({ embeds: [warningEmbed("Not on Payroll", `\`${playerId}\` isn't enrolled.\nUse \`/wagelist\` to see who's on the books.`)], flags: MessageFlags.Ephemeral });
        saveWages(wages.filter(w => w.playerId.toLowerCase() !== playerId.toLowerCase()));
        const tier = WAGE_TIERS[removed.tier];
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Removed from Payroll").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Player",value: `\`${playerId}\``,                                          inline: true },
            { name: "Was",    value: `${tier?.label ?? removed.tier} (+${tier?.amount ?? "?"}/wk)`, inline: true },
            { name: "By",    value: `${interaction.user}`,                                       inline: true },
            { name: "Note",  value: "Existing balance unchanged. No further weekly payouts.",    inline: false },
          ).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         WAGELIST
         ───────────────────────────────────────────────────── */
  "wagelist": async (interaction, name) => {
        const wages = loadWages().filter(w => WAGE_TIERS[w.tier]?.weekly);
        if (!wages.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Payroll — Empty").setDescription('> *"No players on the books yet."*\n\nUse `/addwage` to enrol someone.').setTimestamp()], flags: MessageFlags.Ephemeral });
        const totalPay = wages.reduce((s, w) => s + (WAGE_TIERS[w.tier]?.amount ?? 0), 0);
        const tierSummary = Object.entries(WAGE_TIERS).filter(([, t]) => t.weekly)
          .map(([k, t]) => { const n = wages.filter(w => w.tier === k).length; return n ? `${t.label}: **${n}**` : null; }).filter(Boolean).join("  ·  ");
        const lines = wages.map((w, i) => {
          const tier  = WAGE_TIERS[w.tier] ?? { label: w.tier, amount: "?" };
          const bal   = readPlayerBalance(w.playerId);
          const next  = w.lastPaidAt ? Math.floor((w.lastPaidAt + WAGE_INTERVAL_MS) / 1000) : null;
          return `\`${String(i + 1).padStart(2, "0")}\`  **${w.playerId}**  ·  ${tier.label} *(+${tier.amount}/wk)*  ·  ${bal !== null ? `${bal.toLocaleString()} credits` : "*no ledger*"}${next ? `  ·  next <t:${next}:R>` : ""}`;
        });
        const header = `> *"The House always pays its debts."*\n\n${DIVIDER}\n**${wages.length}** enrolled  ·  ${tierSummary}  ·  **${totalPay.toLocaleString()} credits/week total**`;
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.GOLD).setTitle("Weekly Payroll — The House's Ledger")
            .setDescription(`${header}\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Wages disbursed automatically every 7 days" }).setTimestamp(),
          { perPage: 12, ephemeral: true });
        },

  /* ─────────────────────────────────────────────────────
         CHECKBALANCE
         ───────────────────────────────────────────────────── */
  "checkbalance": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const fp = getPlayerFilePath(playerId);
        if (!fp) return interaction.reply({ embeds: [errorEmbed("Economy Offline", "`MODSAVE_PATH` not set in `.env`.")], flags: MessageFlags.Ephemeral });
        if (!fs.existsSync(fp)) return interaction.reply({ embeds: [warningEmbed("No Ledger Found", `\`${playerId}\` has no ledger yet.\nThey must join the server first, or be assigned a wage with \`/addwage\`.`)], flags: MessageFlags.Ephemeral });
        const balance = readPlayerBalance(playerId);
        if (balance === null) return interaction.reply({ embeds: [errorEmbed("Ledger Corrupted", `Could not parse ledger for \`${playerId}\`.\nPath: \`${fp}\``)], flags: MessageFlags.Ephemeral });
        const wage   = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier  = wage ? (WAGE_TIERS[wage.tier] ?? { label: wage.tier, amount: "?", weekly: true }) : null;
        const nextTs = wage?.lastPaidAt ? Math.floor((wage.lastPaidAt + WAGE_INTERVAL_MS) / 1000) : null;
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Player Ledger")
          .setDescription(`**${playerId}** has **${balance.toLocaleString()} credits**. ${wTier ? `Payroll: ${wTier.label} (+${wTier.amount}/wk)${nextTs ? `, next <t:${nextTs}:R>` : ""}.` : "Not enrolled in payroll."}`)
          .setFooter({ text: randomQuote("caps") }).setTimestamp();
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         GIVECAPS
         ───────────────────────────────────────────────────── */
  "givecaps": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const amount   = interaction.options.getInteger("amount");
        const reason   = interaction.options.getString("reason") ?? "Cap gift";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const current = readPlayerBalance(playerId) ?? 0;
        const newBal  = current + amount;
        if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Ledger Write Failed", "Check `MODSAVE_PATH`.")], flags: MessageFlags.Ephemeral });
        writeModLog({ action: "givecaps", playerId, amount, reason, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Credits Given")
          .setDescription(`${interaction.user} gave **${playerId}** **+${amount.toLocaleString()} credits** — new balance **${newBal.toLocaleString()} credits**. ${reason}`)
          .setFooter({ text: randomQuote("caps") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
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
        const current = readPlayerBalance(playerId) ?? 0;
        const newBal  = Math.max(0, current + amount);
        if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Write Failed", "Check `MODSAVE_PATH`.")], flags: MessageFlags.Ephemeral });
        writeModLog({ action: "adjustcaps", playerId, amount, reason, by: interaction.user.tag });
        const pos = amount >= 0;
        const embed = new EmbedBuilder().setColor(pos ? NV.IRRAD_GREEN : NV.RUST_RED)
          .setTitle(`Credits ${pos ? "Credited" : "Debited"}`)
          .setDescription(`${interaction.user} ${pos ? "credited" : "debited"} **${playerId}** **${pos ? "+" : ""}${amount.toLocaleString()} credits** — new balance **${newBal.toLocaleString()} credits**. ${reason}`)
          .setFooter({ text: "Manual cap adjustment · logged" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
        },
  };
};
