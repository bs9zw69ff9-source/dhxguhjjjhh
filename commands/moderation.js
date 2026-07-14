/* ---------------- commands/moderation: /kick /mute /unmute /flush /staffactivity /tempban /unban /banlist /checkban /permban /cleartempbans /clearallbans /manual /inspect /firewall ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  BAN_REASON_LABELS, CLIN, DAY_MS, DIVIDER, EmbedBuilder, FILES,
  IPHUB_API_KEY, MessageFlags, NV, PUNISH_BY_VALUE, UFW_BLOCK, _IPV4_RE,
  addAutobanExempt, banWithIp, blacklistHas, brand, checkVpn, clearMute,
  clinical, confirmDialog, discordIdForPavlov, dmPunishmentNotice, dmStatusField, dmUserForPavlov,
  easternNoonUTC, emptyIdEmbed, enforceBansSweep, errorEmbed, firewallBlockIps, firewallField,
  firewallUnblockIps, formatTimeLeft, gagEverywhere, getMute, getOnlinePlayers, hasModRole,
  hero, ipBans, isAutobanExempt, isDonator, isMasterName, isMasterIp,
  isOwner, isProtectedPlayer, loadBans, loadModLog, loadVpnChecks, log,
  logAction, logBan, logger, modOnlyEmbed, ownerOnlyEmbed, paginate,
  parseDuration, preserveBalanceAcrossKick, punishDurationLabel, randomQuote, removeBans, sanitizeBanName,
  sanitizeId, sendRcon, serverLabel, setMute, successEmbed, suspendDonator,
  unbanEverywhere, ungagEverywhere, update, upsertPermBan, upsertTempBan, warningEmbed,
  writeModLog,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         KICK  ← deferReply added
         ───────────────────────────────────────────────────── */
  "kick": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const server   = interaction.options.getString("server");
        const reason   = interaction.options.getString("reason") ?? "No reason provided";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        preserveBalanceAcrossKick(playerId);                     // don't let the kick wipe their caps
        // Kick by USERNAME (this gamemode matches names).
        for (const srv of (server === "both" ? ACTIVE_SERVERS : [server])) {
          try { await sendRcon(`Kick ${playerId}`, srv, 2500, 1); } catch {}
        }
        writeModLog({ action: "kick", playerId, reason, by: interaction.user.tag, server });
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Player Ejected from the server")
          .setDescription(`> *${randomQuote("kick")}*\n\n${interaction.user} kicked **${playerId}** from ${serverLabel(server)} — ${reason}`)
          .setFooter({ text: "Kick logged — no ban issued" }).setTimestamp();
        const kTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId, interaction.guild);
        const kDm = await dmPunishmentNotice(kTarget, {
          action: "Kick", color: NV.NCR_TAN, playerId, reason,
          fields: [{ name: "Server", value: serverLabel(server), inline: true }],
        });
        const kDmField = dmStatusField(kDm, kTarget);
        if (kDmField) embed.addFields(kDmField);
        brand(embed); await logAction(embed);
        enforceBansSweep().catch(() => {});     // player sweep after the punishment
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         MUTE — in-game gag for a set time, re-applied every join
         ───────────────────────────────────────────────────── */
  "mute": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const durStr   = interaction.options.getString("duration");
        const reason   = interaction.options.getString("reason") ?? "No reason provided";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        if (isMasterName(playerId)) return interaction.reply({ embeds: [warningEmbed("Protected Name", `\`${playerId}\` is a master name and cannot be muted.`)], flags: MessageFlags.Ephemeral });
        const durMs = parseDuration(durStr);
        if (!durMs) return interaction.reply({ embeds: [errorEmbed("Invalid Duration", "Use `30s`, `10m`, `2h`, or `1d` (a bare number = minutes).")], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        const expires = Date.now() + durMs;
        // If they're already under an active mute, they should already be gagged
        // natively - Gag is a bare toggle, so re-sending it here (e.g. a mod re-running
        // /mute to extend the duration) would flip it back OFF instead of extending it.
        const alreadyMuted = getMute(playerId);
        const stillActive  = alreadyMuted && alreadyMuted.expires > Date.now();
        await setMute(playerId, { name: playerId, expires, reason, moderator: interaction.user.tag, at: Date.now() });
        if (!stillActive) gagEverywhere(playerId);   // gag now if they're online and not already gagged
        enforceBansSweep().catch(() => {});      // player sweep after the punishment
        writeModLog({ action: "mute", playerId, reason, duration: durStr, by: interaction.user.tag });
        const ts = Math.floor(expires / 1000);
        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Player Silenced")
          .setDescription(`${interaction.user} muted **${playerId}** for **${durStr}** (expires <t:${ts}:R>) — ${reason}`)
          .setFooter({ text: "Re-gagged every join until it expires — then unmuted on their next join" }).setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  "unmute": async (interaction, name) => {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const had = getMute(playerId);
        await clearMute(playerId);
        // Only toggle if we believe they're actually gagged right now - Gag is a bare
        // toggle with no True/False, so calling it on someone who isn't muted would
        // incorrectly mute them instead of harmlessly no-op'ing like the old form did.
        if (had) ungagEverywhere(playerId);
        writeModLog({ action: "unmute", playerId, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Player Unsilenced")
          .setDescription(had ? `${interaction.user} lifted the mute on \`${playerId}\` and ungagged them.` : `\`${playerId}\` had no active mute — nothing to lift.`)
          .setTimestamp());
        await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         FLUSH — randomly kick one online player from a server
         ───────────────────────────────────────────────────── */
  "flush": async (interaction, name) => {
        if (!hasModRole(interaction.member)) return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const server = interaction.options.getString("server");
        await interaction.deferReply();
        const servers = server === "both" ? ACTIVE_SERVERS : [server];
        const pool = [];
        for (const srv of servers) {
          try { for (const p of await getOnlinePlayers(srv)) if (p.name) pool.push({ ...p, srv }); } catch {}
        }
        if (!pool.length) {
          return interaction.editReply({ embeds: [warningEmbed("Nothing to Flush", "No players are currently online on the selected server.")] });
        }
        // Staff (Staff/High Staff menu on record — NOT the Faction menu), donators,
        // and master names are immune to the random kick.
        const candidates = pool.filter(p => !isProtectedPlayer(p.name));
        if (!candidates.length) {
          return interaction.editReply({ embeds: [warningEmbed("Nothing to Flush", `All **${pool.length}** online player(s) are flush-immune (staff, donator, or master).`)] });
        }
        const pick   = candidates[Math.floor(Math.random() * candidates.length)];
        const target = sanitizeId(pick.name);             // kick by USERNAME
        preserveBalanceAcrossKick(pick.name);                   // don't let the kick wipe their caps
        let kicked = false;
        try { await sendRcon(`Kick ${target}`, pick.srv, 2500, 1); kicked = true; } catch {}
        writeModLog({ action: "flush-kick", playerId: pick.name, server: pick.srv, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(kicked ? NV.AMBER : NV.NCR_TAN).setTitle("Flush — Random Kick")
          .setDescription(`${interaction.user} flushed **${pick.name}** from ${serverLabel(pick.srv)} — picked at random from ${candidates.length} eligible of ${pool.length} online (staff & donators immune).`)
          .setFooter({ text: kicked ? "Random kick — no ban issued" : "Kick command sent (no RCON confirmation)" }).setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         WARN  ← deferReply added
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         WARNINGS
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         CLEARWARNINGS
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         DELWARN  (remove one warning by number)
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         STAFFACTIVITY — all mod actions taken BY a staff member
         ───────────────────────────────────────────────────── */
  "staffactivity": async (interaction, name) => {
        const staff = interaction.options.getUser("staff");
        const tag   = (staff.tag || "").toLowerCase();
        const uname = (staff.username || "").toLowerCase();
        const matches = loadModLog().filter(e => {
          const by = String(e.by ?? "").toLowerCase();
          return by && (by === tag || by === uname);   // exact match (mod-log stores user.tag) - no substring false positives
        });
        if (!matches.length) {
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Staff Activity — None")
              .setDescription(`No moderation actions on record for ${staff}.`).setTimestamp()
          ], flags: MessageFlags.Ephemeral });
        }
        // tally by action type
        const counts = {};
        for (const e of matches) counts[e.action] = (counts[e.action] ?? 0) + 1;
        const summary = Object.entries(counts).sort((a, b) => b[1] - a[1]).map(([a, n]) => `${a}: ${n}`).join(" · ");
        const lines = matches.slice().reverse().map(e => {
          const ts     = Math.floor(e.at / 1000);
          const detail = e.reason ? ` — ${e.reason}` : e.amount ? ` — ${e.amount > 0 ? "+" : ""}${e.amount} credits` : e.faction ? ` — ${e.faction}` : "";
          const who    = e.playerId ? ` · ${e.playerId}` : "";
          return `\`${e.action}\`${who}${detail} · <t:${ts}:R>`;
        });
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.AMBER)
            .setTitle(`Staff Activity — ${staff.tag}`)
            .setDescription(`${matches.length} action${matches.length !== 1 ? "s" : ""} total *(newest first)*\n${summary}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Mod log" }).setTimestamp(),
          { perPage: 12, ephemeral: true });
        },

  /* ─────────────────────────────────────────────────────
         TEMPBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
  "tempban": async (interaction, name) => {
        const playerId  = sanitizeBanName(interaction.options.getString("playerid"));
        const server    = interaction.options.getString("server");
        const reasonKey = interaction.options.getString("reason");
        const punish    = PUNISH_BY_VALUE[reasonKey];
        const reason    = punish?.name ?? BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        if (isMasterName(playerId)) return interaction.reply({ embeds: [warningEmbed("Protected Name", `\`${playerId}\` is a master name and cannot be banned.`)], flags: MessageFlags.Ephemeral });

        // The punishment sets the sentence; "Other" takes a manual unban date.
        let expires = null, permanent = false, label = "";
        if (punish?.permanent) {
          permanent = true; label = "Permanent";
        } else if (punish?.custom) {
          const dateStr = interaction.options.getString("date");
          if (!dateStr) return interaction.reply({ embeds: [errorEmbed("Date Required",
            "For **Other**, add the `date` option (`YYYY-MM-DD`) — the ban lifts at 12pm Eastern that day.")], flags: MessageFlags.Ephemeral });
          expires = easternNoonUTC(dateStr);
          if (!expires || expires <= Date.now()) return interaction.reply({ embeds: [errorEmbed("Invalid Unban Date",
            `Enter a **future** date as \`YYYY-MM-DD\` (e.g. \`${new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10)}\`). The ban lifts at **12pm Eastern** that day.`)], flags: MessageFlags.Ephemeral });
          label = `until ${new Date(expires).toISOString().slice(0, 10)}`;
        } else if (punish?.ms) {
          expires = Date.now() + punish.ms; label = punishDurationLabel(punish);
        } else {
          return interaction.reply({ embeds: [errorEmbed("Unknown Punishment", "Pick a punishment from the list.")], flags: MessageFlags.Ephemeral });
        }

        await interaction.deferReply();
        const replaced = loadBans().find(b => String(b.playerId).toLowerCase() === playerId.toLowerCase());
        const ipEnf = await banWithIp(playerId, server, permanent ? { permanent: true } : {});
        enforceBansSweep().catch(() => {});   // player sweep after the punishment
        if (permanent) {
          await upsertPermBan({ playerId, reason, moderator: interaction.user.tag, server });
          writeModLog({ action: "permban", playerId, reason, by: interaction.user.tag, server });
        } else {
          await upsertTempBan({ playerId, reason, expires, durationLabel: label, moderator: interaction.user.tag, server });
          writeModLog({ action: "tempban", playerId, reason, duration: label, by: interaction.user.tag, server });
        }

        const ts       = expires ? Math.floor(expires / 1000) : null;
        const sentence = permanent ? "**permanently**" : `for **${label}**`;
        const liftLine = permanent ? "" : `\nLifts <t:${ts}:F> (<t:${ts}:R>)`;
        const embed = clinical(new EmbedBuilder().setColor(CLIN.red)
          .setTitle(permanent ? "Permanent Exile Issued" : "Player Exiled from the server")
          .setDescription(`> *${randomQuote("ban")}*\n\n${interaction.user} banned **${playerId}** from ${serverLabel(server)} ${sentence} — ${reason}${liftLine}`),
          replaced ? `Replaced earlier exile: ${replaced.reason}` : (permanent ? undefined : "Auto-lifted when timer expires"));
        if (punish?.note) embed.addFields({ name: "Reminder", value: punish.note });

        // Timed donator-perk suspension (e.g. Donator Abuse): pull perks now, auto-restore later.
        if (punish?.donatorSuspendMs) {
          const sus   = await suspendDonator(playerId, punish.donatorSuspendMs, interaction.user.tag);
          const weeks = Math.round(punish.donatorSuspendMs / (7 * DAY_MS));
          const rTs   = Math.floor(sus.restoreAt / 1000);
          embed.addFields({ name: "Donator Perks", value: sus.wasDonator
            ? `Removed — auto-restored <t:${rTs}:R> (${weeks} week${weeks !== 1 ? "s" : ""}).`
            : "Player wasn't a donator — nothing to remove." });
          if (sus.wasDonator) writeModLog({ action: "donator-suspend", playerId, by: interaction.user.tag, restoreAt: sus.restoreAt });
        }

        { const _fwf = firewallField(ipEnf?.firewall); if (_fwf) embed.addFields(_fwf); }
        const tbTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId, interaction.guild);
        const tbDm = await dmPunishmentNotice(tbTarget, {
          action: permanent ? "Permanent Ban" : "Temporary Ban", color: permanent ? NV.LEGION_RED : NV.RUST_RED, playerId, reason,
          fields: permanent
            ? [ { name: "Sentence", value: "**Permanent**",    inline: true }, { name: "Server", value: serverLabel(server), inline: true } ]
            : [ { name: "Duration", value: `**${label}**`,     inline: true }, { name: "Server", value: serverLabel(server), inline: true },
                { name: "Expires",  value: `<t:${ts}:F>  ·  <t:${ts}:R>`, inline: false } ],
        });
        const tbDmField = dmStatusField(tbDm, tbTarget);
        if (tbDmField) embed.addFields(tbDmField);
        if (ipEnf?.field) embed.addFields(ipEnf.field);
        brand(embed); await logBan(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         UNBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
  "unban": async (interaction, name) => {
        const playerId = sanitizeBanName(interaction.options.getString("playerid"));
        const server   = interaction.options.getString("server");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        const removed = loadBans().some(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        await addAutobanExempt(playerId, interaction.user.tag);            // exempt FIRST so no sweep can fire mid-unban
        await removeBans(playerId);
        const { blacklist: bl, cleared: c } = unbanEverywhere(playerId);   // blacklist.txt (both installs) + IP flags
        writeModLog({ action: "unban", playerId, by: interaction.user.tag, server });
        const ipLifted = c && (c.ips + c.names) > 0
          ? `Cleared ${c.ips} IP(s) and ${c.names} username flag(s).`
          : "Nothing was flagged for this player.";
        const embed = clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Exile Lifted — Welcome Back to the server")
          .setDescription(`> *${randomQuote("unban")}*\n\n${interaction.user} pardoned **${playerId}**. ${removed ? "Temp ban record cleared." : "No temp ban record."} ${bl.removed ? `Removed from blacklist.txt on ${bl.removed} install(s).` : "Was not on blacklist.txt."} ${ipLifted}`));
        await logBan(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         BANLIST — all active bans
         ───────────────────────────────────────────────────── */
  "banlist": async (interaction, name) => {
        const now  = Date.now();
        const bans = loadBans()
          .filter(b => b.permanent || (b.expires && b.expires > now))
          .sort((a, b) => (b.permanent ? 1 : 0) - (a.permanent ? 1 : 0) || (a.expires ?? 0) - (b.expires ?? 0));   // perms first, then soonest-expiring
        if (!bans.length) {
          return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.green)
            .setTitle("Ban List — Empty").setDescription("No active bans."))], flags: MessageFlags.Ephemeral });
        }
        const lines = bans.map((b, i) => {
          const when = (b.permanent || !b.expires) ? "**PERM**" : `until <t:${Math.floor(b.expires / 1000)}:d>`;
          return `\`${String(i + 1).padStart(2, "0")}\`  **${b.playerId}**  ·  ${when}  ·  *${(b.reason || "no reason").slice(0, 60)}*  ·  ${b.moderator || "?"}`;
        });
        const perm = bans.filter(b => b.permanent || !b.expires).length;
        return paginate(interaction, lines, (pageLines) =>
          clinical(new EmbedBuilder().setColor(CLIN.red)
            .setTitle(`Ban List — ${bans.length} active`)
            .setDescription(`**${perm}** permanent · **${bans.length - perm}** temporary\n${DIVIDER}\n${pageLines.join("\n")}`)),
          { perPage: 15, ephemeral: true });
        },

  /* ─────────────────────────────────────────────────────
         CHECKBAN
         ───────────────────────────────────────────────────── */
  "checkban": async (interaction, name) => {
        const playerId = sanitizeBanName(interaction.options.getString("playerid"));
        const server   = interaction.options.getString("server");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        // Prefer an active TEMP entry if the registry ever holds duplicates for a name.
        const _entries = loadBans().filter(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        const tb = _entries.find(b => !b.permanent && b.expires) ?? _entries[0];
        // Cross-referenced context shown on every branch.
        const _cbLink  = discordIdForPavlov(playerId);
        const _cbMute  = getMute(playerId);
        const _cbMuted = _cbMute && (!_cbMute.expires || _cbMute.expires > Date.now());
        let _cbRec = null; try { _cbRec = ipBans.getRecord(playerId); } catch {}
        const _cbCtx = [];
        if (_cbLink)  _cbCtx.push({ name: "Discord", value: `<@${_cbLink}>`, inline: true });
        if (_cbMuted) _cbCtx.push({ name: "In-Game Mute", value: `Active — lifts <t:${Math.floor(_cbMute.expires / 1000)}:R>`, inline: true });
        if (tb && !tb.permanent && tb.expires) {
          const ts = Math.floor(tb.expires / 1000);
          return interaction.reply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Temporary Exile Active")
              .setDescription(`**${playerId}** is banned from ${serverLabel(server)} for **${tb.durationLabel ?? "?"}** — ${tb.reason}\nBanned by ${tb.moderator} · **${formatTimeLeft(tb.expires)}** left · lifts <t:${ts}:F> (<t:${ts}:R>)`)
              .addFields(..._cbCtx), "Auto-lifted when timer expires")
          ]});
        }
        const hits = blacklistHas(playerId);   // which installs list this name in blacklist.txt
        if (tb && tb.permanent) {   // permanent ban recorded in the ban JSON
          return interaction.reply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Active")
              .setDescription(`**${playerId}** is permanently banned — ${tb.reason ?? "Permanent ban"}\nBanned by ${tb.moderator ?? "?"} · on file: ${hits.length ? hits.map(n => `Server ${n}`).join(" + ") : "ban JSON"}`)
              .addFields(..._cbCtx), "Permanent — use /unban to lift")
          ]});
        }
        if (!hits.length) {
          const cleanE = clinical(new EmbedBuilder().setColor(_cbRec?.flagged ? CLIN.grey : CLIN.green).setTitle("No Exile Found")
            .setDescription(`${hero("This player walks free.")}\n\`${playerId}\` has no active ban.`));
          if (_cbRec?.flagged) cleanE.addFields({ name: "Evasion Watch", value: "Matches an active IP/EOS flag — next join is auto-banned.", inline: false });
          if (isAutobanExempt(playerId)) cleanE.addFields({ name: "Unban Protection", value: "Explicitly unbanned — auto-bans will never re-catch this name.", inline: false });
          if (_cbCtx.length) cleanE.addFields(..._cbCtx);
          return interaction.reply({ embeds: [cleanE] });
        }
        return interaction.reply({ embeds: [
          clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Active")
            .setDescription(`**${playerId}** is on the blacklist — banned on ${hits.map(n => `**Server ${n}**`).join(" + ")}.`), "Blacklisted — use /unban to lift")
        ]});
        },

  /* ─────────────────────────────────────────────────────
         BANLIST
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         PERMBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
  "permban": async (interaction, name) => {
        const playerId  = sanitizeBanName(interaction.options.getString("playerid"));
        const server    = interaction.options.getString("server");
        const reasonKey = interaction.options.getString("reason");
        const notes     = interaction.options.getString("notes") ?? null;
        const reason    = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        if (isMasterName(playerId)) return interaction.reply({ embeds: [warningEmbed("Protected Name", `\`${playerId}\` is a master name and cannot be banned.`)], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        const ipEnf = await banWithIp(playerId, server, { permanent: true });
        enforceBansSweep().catch(() => {});   // player sweep after the punishment
        await upsertPermBan({ playerId, reason, moderator: interaction.user.tag, server });   // record in the ban JSON (supersedes any temp)
        writeModLog({ action: "permban", playerId, reason, by: interaction.user.tag, server });
        const embed = clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Issued")
          .setDescription(`> *${randomQuote("ban")}*\n\n${interaction.user} permanently banned **${playerId}** from ${serverLabel(server)} — ${reason}`));
        if (notes) embed.addFields({ name: "Notes", value: notes });
        { const _fwf = firewallField(ipEnf?.firewall); if (_fwf) embed.addFields(_fwf); }
        const pbTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId, interaction.guild);
        const pbDm = await dmPunishmentNotice(pbTarget, {
          action: "Permanent Ban", color: NV.LEGION_RED, playerId, reason,
          fields: [
            { name: "Sentence", value: "**Permanent**",      inline: true },
            { name: "Server",   value: serverLabel(server),  inline: true },
          ],
        });
        const pbDmField = dmStatusField(pbDm, pbTarget);
        if (pbDmField) embed.addFields(pbDmField);
        if (ipEnf?.field) embed.addFields(ipEnf.field);
        brand(embed); await logBan(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         CLEARTEMPBANS
         ───────────────────────────────────────────────────── */
  "cleartempbans": async (interaction, name) => {
        const bans = loadBans().filter(b => !b.permanent && b.expires);   // temp bans only, leave permanent bans in place
        if (!bans.length) return interaction.reply({ embeds: [successEmbed("Registry Clear", "No active temporary exiles to remove.")], flags: MessageFlags.Ephemeral });
        const preview = bans.map(b => `- \`${b.playerId}\` - *${b.reason}*`).join("\n").slice(0, 3500);
        const go = await confirmDialog(interaction, {
          title: "Clear all temporary bans?",
          body: `This lifts **${bans.length}** temp exile${bans.length !== 1 ? "s" : ""} and unbans on both servers.\n\n${preview}`,
          confirmLabel: `Clear ${bans.length}`,
        });
        if (!go) return;
        // Exempt every name FIRST (like /unban) so enforceBansSweep can't re-ban a
        // player mid-clear before their record is removed by removeBans() below.
        await update(FILES.AUTOBAN_EXEMPT, {}, (m) => {
          for (const b of bans) m[String(b.playerId).toLowerCase()] = { name: b.playerId, at: Date.now(), by: interaction.user.tag };
          return m;
        });
        const ok = [], fail = [];
        for (const ban of bans) { try { unbanEverywhere(ban.playerId); ok.push(ban.playerId); } catch { fail.push(ban.playerId); } }
        await removeBans(...ok);
        writeModLog({ action: "cleartempbans", count: ok.length, by: interaction.user.tag });
        const lines = [...ok.map(id => `\`${id}\``), ...fail.map(id => `\`${id}\` - failed, kept`)];
        await logBan(clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Temp Bans Cleared")
          .setDescription(`**${ok.length}** released${fail.length ? `, **${fail.length}** failed` : ""}\n\n${lines.join("\n")}`.slice(0, 4000))
          .addFields({ name: "By", value: `${interaction.user}`, inline: false })));
        return interaction.editReply({ embeds: [successEmbed("Temp Bans Cleared", `Released **${ok.length}**${fail.length ? `, **${fail.length}** failed` : ""}.`)], components: [], keepEmbeds: true });
        },

  /* ─────────────────────────────────────────────────────
         CLEARALLBANS — owner only: Unban every banned player
         ───────────────────────────────────────────────────── */
  "clearallbans": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        // gather every banned name: bot temp bans + blacklist.txt on both installs
        const names = [...new Set([...loadBans().map(b => b.playerId), ...blacklistAll()].map(s => String(s).trim()).filter(Boolean))];
        if (!names.length) {
          return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("No Exiles on Record").setDescription(`${hero("The server is at peace.")}\nNothing to clear — no bans on record.`))] });
        }

        const preview = names.slice(0, 30).map(n => `- \`${n}\``).join("\n") + (names.length > 30 ? `\n...and ${names.length - 30} more` : "");
        const go = await confirmDialog(interaction, {
          title: "Unban EVERYONE?",
          body: `Removes **${names.length}** player(s) from blacklist.txt on both servers and lifts their IP/username flags. This cannot be undone.\n\n${preview}`,
          confirmLabel: `Unban all ${names.length}`,
        });
        if (!go) return;
        // Exempt every name FIRST (like /unban) so enforceBansSweep can't re-ban a
        // player mid-clear before their record is removed by removeBans() below.
        await update(FILES.AUTOBAN_EXEMPT, {}, (m) => {
          for (const n of names) m[String(n).toLowerCase()] = { name: n, at: Date.now(), by: interaction.user.tag };
          return m;
        });
        let ok = 0, failed = 0;
        for (const n of names) { try { unbanEverywhere(n); ok++; } catch (e) { failed++; logger.warn("ClearAllBans", `Unban ${n} failed: ${e.message}`); } }
        await removeBans(...names);
        writeModLog({ action: "clearallbans", count: ok, by: interaction.user.tag });
        await logBan(clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("All Exiles Pardoned")
          .setDescription(`${interaction.user} unbanned **${ok}**${failed ? `, ${failed} failed` : ""} — removed from blacklist.txt on both servers and lifted their flags.`)));
        return interaction.editReply({ embeds: [successEmbed("All Exiles Pardoned", `Unbanned **${ok}**${failed ? `, **${failed}** failed` : ""}.`)], components: [], keepEmbeds: true });
        },

  /* ─────────────────────────────────────────────────────
         MANUAL
         ───────────────────────────────────────────────────── */
  "manual": async (interaction, name) => {
        const command = interaction.options.getString("command");
        const server  = interaction.options.getString("server");
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        try {
          if (server === "both") {
            // allSettled so one unreachable server doesn't fail the whole command
            const results = await Promise.allSettled(ACTIVE_SERVERS.map(s => sendRcon(command, s)));
            const fmt = (r) => r.status === "fulfilled" ? ((r.value.trim() || "no response").slice(0, 900)) : `unreachable: ${r.reason?.message || r.reason}`;
            writeModLog({ action: "manual-rcon", command, server, by: interaction.user.tag });
            return interaction.editReply({ embeds: [
              new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("Raw RCON — All Servers").setDescription(`${DIVIDER}`)
                .addFields(
                  { name: "Signal", value: `\`\`\`${command}\`\`\``, inline: false },
                  ...ACTIVE_SERVERS.map((s, i) => ({ name: `${serverLabel(s)} Response`, value: `\`\`\`${fmt(results[i])}\`\`\``, inline: false })),
                  { name: "By", value: `${interaction.user}`, inline: false },
                ).setTimestamp()
            ]});
          }
          const result = await sendRcon(command, server);
          writeModLog({ action: "manual-rcon", command, server, by: interaction.user.tag });
          await logAction(new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("Manual RCON")
            .setDescription(`${interaction.user} sent \`${command}\` to ${serverLabel(server)}.`).setTimestamp());
          return interaction.editReply({ embeds: [
            new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("RCON Transmission Complete")
              .setDescription(`${interaction.user} sent this to ${serverLabel(server)}:\n\`\`\`${command}\`\`\`\n\`\`\`${(result.trim() || "no response").slice(0, 1000)}\`\`\``)
              .setTimestamp()
          ]});
        } catch (err) {
          return interaction.editReply({ embeds: [errorEmbed("RCON Failed", `Cannot reach **${serverLabel(server)}**.\n\`\`\`${err.message}\`\`\`\nCheck \`/ping\` for server status.`)] });
        }
        },

  /* ─────────────────────────────────────────────────────
         INSPECT — owner-only deep dossier (IPs, VPN detection,
         alts, EOS id, enforcement flags). Ephemeral: sensitive.
         ───────────────────────────────────────────────────── */
  "inspect": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });   // exposes IPs — never public

        let rec = null; try { rec = ipBans.getRecord(playerId); } catch {}
        const allIps = rec?.ips  ?? [];
        const cIps   = rec?.cips ?? [];
        const alts   = rec?.alts ?? [];
        // VPN/proxy verdict per known IP (confirmed IPs preferred, else all). Actively run
        // any missing checks now — owner command, worth the lookups. checkVpn caches, so
        // already-checked IPs cost nothing, and it's a no-op when IPHUB_API_KEY is unset.
        const ipsToShow = (cIps.length ? cIps : allIps).slice(0, 12);
        if (IPHUB_API_KEY) { try { await Promise.all(ipsToShow.map(ip => checkVpn(ip).catch(() => null))); } catch {} }
        const vpn    = loadVpnChecks();
        const vpnLines = ipsToShow.map(ip => {
          const v = vpn[ip];
          if (!v) return `\`${ip}\` — *not checked*`;
          const verdict = v.confirmed === true ? "**VPN/proxy** (IPHub+IPQS agree)"
            : v.confirmed === false            ? "disputed (IPHub flagged, IPQS clean)"
            : v.flagged                        ? "flagged by IPHub"
            :                                    "clean";
          const q = v.ipqs ? ` · vpn:${v.ipqs.vpn} proxy:${v.ipqs.proxy} tor:${v.ipqs.tor} fraud:${v.ipqs.fraudScore}` : "";
          return `\`${ip}\` — ${verdict}${v.isp ? ` · ${v.isp}` : ""}${q}`;
        });

        const tb       = loadBans().find(b => String(b.playerId).toLowerCase() === playerId.toLowerCase());
        const linkedId = discordIdForPavlov(playerId);
        const flags = [];
        if (rec?.flagged)            flags.push("IP/EOS **flagged** — next join is auto-banned");
        if (isMasterName(playerId))  flags.push("**MASTER** — bypasses all enforcement");
        if (isDonator(playerId))     flags.push("Donator — flush-immune (NOT ban-immune)");
        if (isAutobanExempt(playerId)) flags.push("Unban-exempt — auto-ban won't re-catch");
        if (rec?.bypass)             flags.push("Untracked (ignore-list) — no IP logging/auto-ban");

        const joinCap = (arr) => arr.length ? arr.map(x => `\`${x}\``).join("  ·  ").slice(0, 1000) : null;
        const embed = new EmbedBuilder().setColor(rec?.flagged ? NV.LEGION_RED : NV.BLUE_VATS)
          .setTitle(`Inspect — ${rec?.name || playerId}`)
          .addFields(
            { name: "EOS / Unique ID", value: rec?.id ? `\`${rec.id}\`` : "*unknown (no confirmed disconnect yet)*", inline: false },
            { name: `All IPs (${allIps.length})`,        value: joinCap(allIps) ?? "*none on record*",   inline: false },
            { name: `Confirmed IPs (${cIps.length})`,    value: joinCap(cIps)   ?? "*none confirmed yet*", inline: false },
            { name: "VPN / Proxy detection",             value: vpnLines.length ? vpnLines.join("\n").slice(0, 1000) : "*no IPs to check (or IPHUB_API_KEY unset)*", inline: false },
            { name: "Known alts (shared confirmed IP)",  value: joinCap(alts) ?? "*none*",                inline: false },
            { name: "Sessions",   value: String(rec?.logins ?? 0),                                              inline: true },
            { name: "First seen", value: rec?.firstSeen ? `<t:${Math.floor(rec.firstSeen / 1000)}:R>` : "*n/a*", inline: true },
            { name: "Last seen",  value: rec?.lastSeen  ? `<t:${Math.floor(rec.lastSeen / 1000)}:R>`  : "*n/a*", inline: true },
            { name: "Discord",    value: linkedId ? `<@${linkedId}> \`${linkedId}\`` : "*not linked*",           inline: true },
            { name: "Ban",        value: tb ? (tb.permanent || !tb.expires ? `Permanent — ${tb.reason}` : `Temp — ${tb.reason} · until <t:${Math.floor(tb.expires / 1000)}:R>`) : "*none*", inline: false },
            { name: "Flags / status", value: flags.length ? flags.map(f => `• ${f}`).join("\n") : "*none*",     inline: false },
          )
          .setFooter({ text: "Owner inspection · sensitive — do not share" }).setTimestamp();
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         FIREWALL — owner-only manual ufw block/unblock of an IP,
         independent of any ban. Needs UFW_BLOCK=1 (root/sudo ufw).
         ───────────────────────────────────────────────────── */
  "firewall": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const sub = interaction.options.getSubcommand();
        const ip  = String(interaction.options.getString("ip") ?? "").trim();
        if (!_IPV4_RE.test(ip)) return interaction.reply({ embeds: [warningEmbed("Invalid IP", `\`${ip || "(empty)"}\` is not a valid IPv4 address.`)], flags: MessageFlags.Ephemeral });
        if (!UFW_BLOCK) return interaction.reply({ embeds: [warningEmbed("Firewall Disabled", "OS firewall blocking is off. Set **UFW_BLOCK=1** (and run the bot as root, or give it passwordless `sudo ufw`) to enable it.")], flags: MessageFlags.Ephemeral });
        if (sub === "block" && isMasterIp(ip)) return interaction.reply({ embeds: [warningEmbed("Protected IP", `\`${ip}\` is a **master IP** and is never blocked. The periodic firewall check keeps it unblocked.`)], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });   // sensitive: exposes an IP

        if (sub === "block") {
          let res; try { res = await firewallBlockIps([ip]); } catch (err) { res = { blocked: 0, error: err.message }; }
          writeModLog({ action: "firewall-block", playerId: ip, reason: "manual firewall block", by: interaction.user.tag });
          logger.info("Firewall", `Manual block of ${ip} by ${interaction.user.tag} — blocked ${res.blocked}`);
          const ok = res.blocked > 0;
          const embed = clinical(new EmbedBuilder().setColor(ok ? CLIN.red : NV.AMBER)
            .setTitle(ok ? "Firewall — IP Blocked" : "Firewall — Block Not Applied")
            .setDescription(`${DIVIDER}\n${ok ? `\`${ip}\` is now denied at the OS firewall.` : `Could not block \`${ip}\`.${res.error ? ` (${res.error})` : ""}`}\n${DIVIDER}`)
            .addFields(
              { name: "IP",     value: `\`${ip}\``, inline: true },
              { name: "Rule",   value: "`ufw insert 1 deny from <ip>`", inline: true },
              { name: "Result", value: ok ? `Blocked **${res.blocked}** rule(s)` : "No rule added", inline: true },
            ), "Manual firewall control · owner");
          return interaction.editReply({ embeds: [embed] });
        }

        // sub === "unblock"
        let res; try { res = await firewallUnblockIps([ip]); } catch (err) { res = { unblocked: 0, error: err.message }; }
        writeModLog({ action: "firewall-unblock", playerId: ip, reason: "manual firewall unblock", by: interaction.user.tag });
        logger.info("Firewall", `Manual unblock of ${ip} by ${interaction.user.tag} — removed ${res.unblocked}`);
        const ok = res.unblocked > 0;
        const embed = clinical(new EmbedBuilder().setColor(ok ? CLIN.green : NV.AMBER)
          .setTitle(ok ? "Firewall — Block Removed" : "Firewall — No Block Found")
          .setDescription(`${DIVIDER}\n${ok ? `\`${ip}\` is no longer denied at the OS firewall.` : `No firewall rule for \`${ip}\` was found to remove.${res.error ? ` (${res.error})` : ""}`}\n${DIVIDER}`)
          .addFields(
            { name: "IP",     value: `\`${ip}\``, inline: true },
            { name: "Rule",   value: "`ufw delete <rule>`", inline: true },
            { name: "Result", value: ok ? `Removed **${res.unblocked}** rule(s)` : "Nothing to remove", inline: true },
          ), "Manual firewall control · owner");
        return interaction.editReply({ embeds: [embed] });
        },
  };
};
