/* ---------------- commands/casino: /slots /coinflip /blackjack /roulette /cockfight /russianroulette /jackpot /casino ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  ActionRowBuilder, ButtonBuilder, ButtonStyle, DIVIDER, EmbedBuilder, GAMBLE_QUOTA_MAX,
  GAMBLE_QUOTA_WINDOW_MS, GAME_ICON, JACKPOT_MIN_BALANCE, JACKPOT_WIN_CHANCE, MessageFlags, NV,
  ROULETTE_COLOR_EMOJI, RUSSIAN_ROULETTE_MULTS, addToPot, awaitOwnedComponent, brand, casinoIntake,
  casinoResultEmbed, checkGambleQuota, checkRateLimit, confirmDialog, creditCaps, currentPot,
  debitCaps, drainPot, errorEmbed, formatHand, freshDeck, gambleQuotaLimitEmbed,
  handValue, isBlackjack, loadCasinoConfig, loadDiscordLinks, logAction, randomQuote,
  rateLimitEmbed, readPlayerBalance, saveCasinoConfig, spinRoulette, spinSlots, successEmbed,
  warningEmbed, writeModLog,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         STATS
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         CASINO - slots, coinflip, blackjack, roulette,
         cockfight, russian roulette, admin config
         ───────────────────────────────────────────────────── */
  "slots": async (interaction, name) => {
        const intake = await casinoIntake(interaction);
        if (!intake) return;
        const { playerId, bet } = intake;
        await debitCaps(playerId, bet);
        const { reels, mult } = spinSlots();
        const payout = mult ? bet * mult : 0;
        if (payout) await creditCaps(playerId, payout); else await addToPot(bet);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        writeModLog({ action: "slots", playerId, bet, reels: reels.map(r => r.key), payout, by: interaction.user.tag });
        const embed = casinoResultEmbed({
          icon: GAME_ICON.slots, title: payout ? "Jackpot!" : "No Match", color: payout ? NV.IRRAD_GREEN : NV.RUST_RED,
          body: `### [ ${reels.map(r => r.emoji).join("  |  ")} ]`,
          bet, balance: newBalance,
          resultLabel: payout ? "Payout" : "Result",
          resultValue: payout ? `**+${payout.toLocaleString()} credits** (${mult}x)` : "**Lost the wager**",
        });
        return interaction.reply({ embeds: [embed] });
        },

  "coinflip": async (interaction, name) => {
        const intake = await casinoIntake(interaction);
        if (!intake) return;
        const { playerId, bet } = intake;
        const call = interaction.options.getString("call");
        await debitCaps(playerId, bet);
        const result = Math.random() < 0.5 ? "heads" : "tails";
        const win    = result === call;
        const payout = win ? Math.floor(bet * 1.9) : 0;
        if (payout) await creditCaps(playerId, payout); else await addToPot(bet);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        writeModLog({ action: "coinflip", playerId, bet, call, result, payout, by: interaction.user.tag });
        const embed = casinoResultEmbed({
          icon: GAME_ICON.coinflip, title: win ? "You Called It" : "Wrong Call", color: win ? NV.IRRAD_GREEN : NV.RUST_RED,
          body: `🪙 The coin lands on **${result === "heads" ? "🦅 Heads" : "🔢 Tails"}**. You called **${call}**.`,
          bet, balance: newBalance,
          resultLabel: win ? "Payout" : "Result",
          resultValue: win ? `**+${payout.toLocaleString()} credits**` : "**Lost the wager**",
        });
        return interaction.reply({ embeds: [embed] });
        },

  "blackjack": async (interaction, name) => {
        const intake = await casinoIntake(interaction);
        if (!intake) return;
        const { playerId } = intake;
        let bet = intake.bet;
        await debitCaps(playerId, bet);

        const deck    = freshDeck();
        const draw    = () => deck.pop();
        const player  = [draw(), draw()];
        const dealer  = [draw(), draw()];
        const pNatural = isBlackjack(player), dNatural = isBlackjack(dealer);

        const renderRow = (canAct) => new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("bj_hit").setLabel("Hit").setStyle(ButtonStyle.Primary).setDisabled(!canAct),
          new ButtonBuilder().setCustomId("bj_stand").setLabel("Stand").setStyle(ButtonStyle.Secondary).setDisabled(!canAct),
          new ButtonBuilder().setCustomId("bj_double").setLabel("Double Down").setStyle(ButtonStyle.Danger)
            .setDisabled(!canAct || player.length !== 2 || intake.balance < bet * 2),
        );
        const renderEmbed = (title, footer, reveal) => {
          const pv = handValue(player), dv = handValue(dealer);
          const playerValue = pNatural ? "Blackjack" : `${pv.total}${pv.soft ? " (soft)" : ""}`;
          const dealerValue = reveal ? (dNatural ? "Blackjack" : `${dv.total}`) : "??";
          return brand(new EmbedBuilder().setColor(NV.GOLD).setTitle(`${GAME_ICON.blackjack}  ${title}`)
            .addFields(
              { name: "Your Hand",   value: `${formatHand(player)}\nValue: **${playerValue}**`,                     inline: false },
              { name: "Dealer Hand", value: `${formatHand(dealer, reveal ? Infinity : 1)}\nValue: **${dealerValue}**`, inline: false },
              { name: "Wager",       value: `**${bet.toLocaleString()} credits**`,                                     inline: true },
            ).setFooter({ text: footer }));
        };

        let bust = false;
        if (!pNatural && !dNatural) {
          await interaction.reply({ embeds: [renderEmbed("Blackjack", "Hit, Stand, or Double Down", false)], components: [renderRow(true)] });
          const msg = await interaction.fetchReply();
          for (;;) {
            // Fresh 60s per decision (matches the old per-call timeout) - but a
            // bystander mashing the buttons can't extend THIS decision's window,
            // since the deadline is fixed for the whole awaitOwnedComponent() call.
            const btn = await awaitOwnedComponent(msg, interaction.user.id, Date.now() + 60_000, "This isn't your hand - you can't play someone else's blackjack.");
            if (!btn) break;   // idle timeout -> auto-stand on whatever hand stands now
            if (btn.customId === "bj_hit") {
              player.push(draw());
              if (handValue(player).total > 21) { bust = true; await btn.deferUpdate(); break; }
              await btn.update({ embeds: [renderEmbed("Blackjack", "Hit, Stand, or Double Down", false)], components: [renderRow(true)] });
              continue;
            }
            if (btn.customId === "bj_double") {
              const d = await debitCaps(playerId, bet);
              if (!d.ok) { await btn.update({ embeds: [renderEmbed("Blackjack", "Not enough credits to double - Hit or Stand.", false)], components: [renderRow(true)] }); continue; }
              bet *= 2;
              player.push(draw());
              if (handValue(player).total > 21) bust = true;
              await btn.deferUpdate();
              break;   // double forces a stand either way
            }
            await btn.deferUpdate();   // stand
            break;
          }
        }

        let outcome;
        if (pNatural || dNatural) outcome = pNatural && dNatural ? "push" : pNatural ? "blackjack" : "lose";
        else if (bust) outcome = "lose";
        else {
          while (handValue(dealer).total < 17) dealer.push(draw());
          const pv = handValue(player).total, dv = handValue(dealer).total;
          outcome = dv > 21 || pv > dv ? "win" : pv === dv ? "push" : "lose";
        }

        const payout = outcome === "blackjack" ? Math.floor(bet * 2.5)
                     : outcome === "win"       ? bet * 2
                     : outcome === "push"      ? bet
                     : 0;
        if (payout) await creditCaps(playerId, payout); else await addToPot(bet);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        writeModLog({ action: "blackjack", playerId, bet, outcome, payout, by: interaction.user.tag });

        const title      = { blackjack: "Blackjack!", win: "You Win", push: "Push", lose: "Dealer Wins" }[outcome];
        const resultLine = outcome === "push" ? "**Bet refunded**" : payout ? `**+${(payout - bet).toLocaleString()} credits**` : "**Lost the wager**";
        const finalEmbed = renderEmbed(title, randomQuote("casino"), true);
        finalEmbed.addFields({ name: "Result", value: resultLine, inline: true }, { name: "Balance", value: `**${newBalance.toLocaleString()} credits**`, inline: true });
        if (pNatural || dNatural) return interaction.reply({ embeds: [finalEmbed] });
        return interaction.editReply({ embeds: [finalEmbed], components: [] });
        },

  "roulette": async (interaction, name) => {
        const space  = interaction.options.getString("space");
        const number = interaction.options.getInteger("number");
        if (number === null && !space) {
          return interaction.reply({ embeds: [errorEmbed("No Bet Placed", "Pick a `space` or a straight-up `number` (0-36).")], flags: MessageFlags.Ephemeral });
        }
        const intake = await casinoIntake(interaction);
        if (!intake) return;
        const { playerId, bet } = intake;
        await debitCaps(playerId, bet);
        const result = spinRoulette(space, number);
        const payout = result.win ? bet * result.mult : 0;
        if (payout) await creditCaps(playerId, payout); else await addToPot(bet);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        const betLabel   = number !== null ? `Straight #${number}` : space;
        writeModLog({ action: "roulette", playerId, bet, betLabel, landed: result.landed, payout, by: interaction.user.tag });
        const colorWord = { red: "Red", black: "Black", green: "Green" }[result.color];
        const embed = casinoResultEmbed({
          icon: GAME_ICON.roulette, title: result.win ? "You Win" : "House Wins", color: result.win ? NV.IRRAD_GREEN : NV.RUST_RED,
          body: `### Ball lands on ${ROULETTE_COLOR_EMOJI[result.color]} ${result.landed} (${colorWord})\nYour bet: **${betLabel}**`,
          bet, balance: newBalance,
          resultLabel: result.win ? "Payout" : "Result",
          resultValue: result.win ? `**+${payout.toLocaleString()} credits** (${result.mult}x)` : "**Lost the wager**",
        });
        return interaction.reply({ embeds: [embed] });
        },

  "cockfight": async (interaction, name) => {
        const opponent = interaction.options.getUser("opponent");
        if (opponent && opponent.id === interaction.user.id) {
          return interaction.reply({ embeds: [errorEmbed("Nice Try", "You can't cockfight yourself.")], flags: MessageFlags.Ephemeral });
        }
        if (opponent?.bot) {
          return interaction.reply({ embeds: [errorEmbed("Invalid Opponent", "Bots don't gamble.")], flags: MessageFlags.Ephemeral });
        }
        const intake = await casinoIntake(interaction);
        if (!intake) return;
        const { playerId, bet } = intake;

        if (!opponent) {
          await debitCaps(playerId, bet);
          const win    = Math.random() < 0.5;
          const payout = win ? Math.floor(bet * 1.9) : 0;
          if (payout) await creditCaps(playerId, payout); else await addToPot(bet);
          const newBalance = readPlayerBalance(playerId) ?? 0;
          writeModLog({ action: "cockfight-house", playerId, bet, win, payout, by: interaction.user.tag });
          const embed = casinoResultEmbed({
            icon: GAME_ICON.cockfight, title: win ? "Your Bird Wins" : "Your Bird Loses", color: win ? NV.IRRAD_GREEN : NV.RUST_RED,
            body: "🐓 You threw your rooster against the house's champion.",
            bet, balance: newBalance,
            resultLabel: win ? "Payout" : "Result",
            resultValue: win ? `**+${payout.toLocaleString()} credits**` : "**Lost the wager**",
          });
          return interaction.reply({ embeds: [embed] });
        }

        const oppName = loadDiscordLinks()[opponent.id]?.name;
        if (!oppName) {
          return interaction.reply({ embeds: [errorEmbed("Opponent Not Linked", `${opponent} hasn't linked a Pavlov account with \`/link add\`.`)], flags: MessageFlags.Ephemeral });
        }
        const oppBalance = readPlayerBalance(oppName) ?? 0;
        if (oppBalance < bet) {
          return interaction.reply({ embeds: [errorEmbed("Opponent Can't Cover It", `${opponent} doesn't have **${bet.toLocaleString()}** credits.`)], flags: MessageFlags.Ephemeral });
        }

        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("cf_accept").setLabel("Accept").setStyle(ButtonStyle.Success),
          new ButtonBuilder().setCustomId("cf_decline").setLabel("Decline").setStyle(ButtonStyle.Secondary),
        );
        await interaction.reply({ embeds: [brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`${GAME_ICON.cockfight}  Cockfight Challenge`)
          .setDescription(`${interaction.user} challenges ${opponent} to a cockfight for **${bet.toLocaleString()} credits** each.\n-# Expires in 60s.`))], components: [row] });
        const msg = await interaction.fetchReply();
        const accept = await awaitOwnedComponent(msg, opponent.id, Date.now() + 60_000, "This challenge isn't addressed to you.");
        if (!accept) return interaction.editReply({ embeds: [brand(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle(`${GAME_ICON.cockfight}  Challenge Expired`).setDescription(`${opponent} didn't respond in time.`))], components: [] });
        if (accept.customId === "cf_decline") {
          return accept.update({ embeds: [brand(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle(`${GAME_ICON.cockfight}  Challenge Declined`).setDescription(`${opponent} backed out.`))], components: [] });
        }
        await accept.deferUpdate();

        // Re-validate now that we're actually committing - either side could have spent caps meanwhile.
        const [d1, d2] = await Promise.all([debitCaps(playerId, bet), debitCaps(oppName, bet)]);
        if (!d1.ok || !d2.ok) {
          if (d1.ok) await creditCaps(playerId, bet);
          if (d2.ok) await creditCaps(oppName, bet);
          return interaction.editReply({ embeds: [errorEmbed("Fight Cancelled", "One of you no longer has enough credits.")], components: [] });
        }
        const challengerWins = Math.random() < 0.5;
        const rake   = Math.ceil(bet * 2 * 0.05);   // 5% house cut of the pot - goes to the jackpot, not nowhere
        const prize  = bet * 2 - rake;
        const winnerName = challengerWins ? playerId : oppName;
        await creditCaps(winnerName, prize);
        await addToPot(rake);
        writeModLog({ action: "cockfight-pvp", challenger: playerId, opponent: oppName, bet, winner: winnerName, rake, by: interaction.user.tag });
        const winnerMention = challengerWins ? interaction.user : opponent;
        const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${GAME_ICON.cockfight}  The Fight Is Over`)
          .setDescription(`🐓 ${winnerMention}'s bird wins the pit!`)
          .addFields(
            { name: "Prize Pool",       value: `**${(bet * 2).toLocaleString()} credits**`, inline: true },
            { name: "House Cut → 🎉 Jackpot", value: `**${rake.toLocaleString()} credits**`,      inline: true },
            { name: "Winner Takes",     value: `**${prize.toLocaleString()} credits**`,     inline: true },
          ).setFooter({ text: randomQuote("casino") }));
        return interaction.editReply({ embeds: [embed], components: [] });
        },

  "russianroulette": async (interaction, name) => {
        const intake = await casinoIntake(interaction);
        if (!intake) return;
        const { playerId, bet } = intake;
        await debitCaps(playerId, bet);

        let pull = 0;
        const renderRow = (canAct) => new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("rr_pull").setLabel(`Pull Trigger  (next ${RUSSIAN_ROULETTE_MULTS[pull]}x)`).setStyle(ButtonStyle.Danger).setDisabled(!canAct),
          new ButtonBuilder().setCustomId("rr_cashout").setLabel(pull > 0 ? `Cash Out (${RUSSIAN_ROULETTE_MULTS[pull - 1]}x)` : "Cash Out").setStyle(ButtonStyle.Secondary).setDisabled(!canAct || pull === 0),
        );
        const renderEmbed = (title, footer) => brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle(`${GAME_ICON.russianroulette}  ${title}`)
          .setDescription(`🔫 1-in-6 chance each pull. Surviving all six pulls cashes out automatically.`)
          .addFields(
            { name: "Wager",              value: `**${bet.toLocaleString()} credits**`, inline: true },
            { name: "Pulls Survived",     value: `**${pull}** / ${RUSSIAN_ROULETTE_MULTS.length}`, inline: true },
            { name: "Current Multiplier", value: pull ? `**${RUSSIAN_ROULETTE_MULTS[pull - 1]}x**` : "—", inline: true },
          ).setFooter({ text: footer }));

        await interaction.reply({ embeds: [renderEmbed("Russian Roulette", "Pull the trigger, or cash out")], components: [renderRow(true)] });
        const msg = await interaction.fetchReply();

        let died = false;
        for (;;) {
          const btn = await awaitOwnedComponent(msg, interaction.user.id, Date.now() + 60_000, "This isn't your revolver - you can't pull someone else's trigger.");
          if (!btn) break;   // idle timeout -> banks whatever's been survived so far
          if (btn.customId === "rr_cashout") { await btn.deferUpdate(); break; }
          await btn.deferUpdate();
          if (Math.random() < 1 / 6) { died = true; break; }
          pull++;
          if (pull >= RUSSIAN_ROULETTE_MULTS.length) break;   // out of chambers -> forced cash-out
          await interaction.editReply({ embeds: [renderEmbed("Click.", "Pull again, or cash out")], components: [renderRow(true)] });
        }

        const payout = died ? 0 : pull > 0 ? Math.floor(bet * RUSSIAN_ROULETTE_MULTS[pull - 1]) : bet;
        if (payout) await creditCaps(playerId, payout); else await addToPot(bet);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        writeModLog({ action: "russianroulette", playerId, bet, pulls: pull, died, payout, by: interaction.user.tag });

        const title      = died ? "💥 BANG." : pull === 0 ? "Walked Away" : "Cashed Out";
        const resultLine = died ? "**Lost the wager**" : payout > bet ? `**+${(payout - bet).toLocaleString()} credits**` : "**Bet refunded**";
        const finalEmbed = renderEmbed(title, randomQuote("casino"));
        finalEmbed.addFields({ name: "Result", value: resultLine, inline: true }, { name: "Balance", value: `**${newBalance.toLocaleString()} credits**`, inline: true });
        return interaction.editReply({ embeds: [finalEmbed], components: [] });
        },

  "jackpot": async (interaction, name) => {
        const cfg = loadCasinoConfig();
        if (!cfg.enabled) {
          return interaction.reply({ embeds: [warningEmbed("The House Is Closed", "Gambling is currently disabled.")], flags: MessageFlags.Ephemeral });
        }
        const playerId = loadDiscordLinks()[interaction.user.id]?.name;
        if (!playerId) {
          return interaction.reply({ embeds: [warningEmbed("Not Linked", "Link your Discord to your Pavlov username first - use `/link add`.")], flags: MessageFlags.Ephemeral });
        }
        if (!checkRateLimit(interaction.user.id, "casino", cfg.cooldownMs)) {
          return interaction.reply({ embeds: [rateLimitEmbed()], flags: MessageFlags.Ephemeral });
        }
        const quota = checkGambleQuota(playerId);
        if (!quota.ok) {
          return interaction.reply({ embeds: [gambleQuotaLimitEmbed(quota.resetAt)], flags: MessageFlags.Ephemeral });
        }
        const balance = readPlayerBalance(playerId) ?? 0;
        if (balance < JACKPOT_MIN_BALANCE) {
          return interaction.reply({ embeds: [errorEmbed("Not Enough Credits", `You need at least **${JACKPOT_MIN_BALANCE.toLocaleString()}** credits to shoot for the jackpot. You have **${balance.toLocaleString()}**.`)], flags: MessageFlags.Ephemeral });
        }
        const pot = currentPot();
        if (pot <= 0) {
          return interaction.reply({ embeds: [warningEmbed("Jackpot Is Empty", "There's nothing in the pot right now - check back after a few more losses across the casino.")], flags: MessageFlags.Ephemeral });
        }

        const go = await confirmDialog(interaction, {
          title: "Bet your ENTIRE bank on the jackpot?",
          body: `You have **${balance.toLocaleString()}** credits. Win, and you take the **${pot.toLocaleString()}**-cap pot plus your bet back. Lose, and your entire balance goes into the pot for the next challenger.\n\nWin chance: **${Math.round(JACKPOT_WIN_CHANCE * 100)}%**`,
          confirmLabel: "Bet it all",
        });
        if (!go) return;

        const debit = await debitCaps(playerId, balance);
        if (!debit.ok) {
          return interaction.editReply({ embeds: [errorEmbed("Balance Changed", "Your balance changed before this went through - nothing was wagered.")], components: [] });
        }
        const wager = balance;   // the exact amount debitCaps subtracted, not debit.before (the live pre-debit balance)
        const win = Math.random() < JACKPOT_WIN_CHANCE;

        if (win) {
          const won = await drainPot();
          const payout = wager + won;
          await creditCaps(playerId, payout);
          const newBalance = readPlayerBalance(playerId) ?? 0;
          writeModLog({ action: "jackpot-win", playerId, wager, won, by: interaction.user.tag });
          const embed = brand(new EmbedBuilder().setColor(NV.GOLD).setTitle(`${GAME_ICON.jackpot}  JACKPOT!`)
            .setDescription(`${interaction.user} just won the entire pot!`)
            .addFields(
              { name: "Wagered",     value: `**${wager.toLocaleString()} credits**`,     inline: true },
              { name: "Pot Won",     value: `**${won.toLocaleString()} credits**`,       inline: true },
              { name: "New Balance", value: `**${newBalance.toLocaleString()} credits**`, inline: true },
            ).setFooter({ text: randomQuote("casino") }));
          await logAction(embed);
          return interaction.editReply({ embeds: [embed], components: [] });
        }

        await addToPot(wager);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        const newPot = currentPot();
        writeModLog({ action: "jackpot-lose", playerId, wager, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle(`${GAME_ICON.jackpot}  Busted`)
          .setDescription(`The house takes it all. Your **${wager.toLocaleString()}** credits are added to the pot.`)
          .addFields(
            { name: "Lost",    value: `**${wager.toLocaleString()} credits**`, inline: true },
            { name: "New Pot", value: `**${newPot.toLocaleString()} credits**`, inline: true },
            { name: "Balance", value: `**${newBalance.toLocaleString()} credits**`, inline: true },
          ).setFooter({ text: randomQuote("casino") }));
        return interaction.editReply({ embeds: [embed], components: [] });
        },

  "casino": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();
        const cfg = loadCasinoConfig();
        if (sub === "status") {
          const embed = brand(new EmbedBuilder().setColor(cfg.enabled ? NV.IRRAD_GREEN : NV.DEAD_GREY).setTitle("🎰  Casino Config")
            
            .addFields(
              { name: "Status",     value: cfg.enabled ? "**Open**" : "**Closed**",              inline: true },
              { name: "Min Bet",    value: `**${cfg.minBet.toLocaleString()}** credits`,             inline: true },
              { name: "Max Bet",    value: `**${cfg.maxBet.toLocaleString()}** credits`,             inline: true },
              { name: "Cooldown",   value: `**${(cfg.cooldownMs / 1000).toFixed(1)}s** between gambles`, inline: true },
              { name: "Gamble Cap", value: `**${GAMBLE_QUOTA_MAX}** per **${GAMBLE_QUOTA_WINDOW_MS / 3_600_000}h**`, inline: true },
              { name: "🎉 Jackpot Pot", value: `**${currentPot().toLocaleString()}** credits`,       inline: true },
            ));
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }
        if (sub === "toggle") {
          const enabled = interaction.options.getBoolean("enabled");
          saveCasinoConfig({ ...cfg, enabled });
          writeModLog({ action: "casino-toggle", enabled, by: interaction.user.tag });
          const embed = successEmbed("Casino Updated", `Gambling is now **${enabled ? "open" : "closed"}**.`);
          await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }
        if (sub === "setlimits") {
          const min = interaction.options.getInteger("min");
          const max = interaction.options.getInteger("max");
          if (min > max) return interaction.reply({ embeds: [errorEmbed("Invalid Limits", "Min bet can't exceed max bet.")], flags: MessageFlags.Ephemeral });
          saveCasinoConfig({ ...cfg, minBet: min, maxBet: max });
          writeModLog({ action: "casino-setlimits", min, max, by: interaction.user.tag });
          const embed = successEmbed("Casino Updated", `Bet range set to **${min.toLocaleString()}**–**${max.toLocaleString()}** credits.`);
          await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }
        return;
        },
  };
};
