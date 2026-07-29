/* ---------------- commands/admin: /setroles /donator /announce /givemenu /stripmenu /stripmenuall /setrconroles /configure ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
const crypto = require("crypto");   // PIN generation - a builtin, no injection needed

module.exports = (ctx) => {
  const {
  ACTIVE_SERVERS, ActionRowBuilder, ButtonBuilder, ButtonStyle, ComponentType, DIVIDER, DONATOR_FILE, GLYPH, CLIN,
  EmbedBuilder, MENUS, MessageFlags, ModalBuilder, NV, clinical, firewallStatus,
  StringSelectMenuBuilder, TextInputBuilder, TextInputStyle, addDonator, addMenuGrant, addUserBlacklist,
  adminOnlyEmbed, banWithIp, bar, brand, client, commands,
  _diag, deniedEmbed, emptyIdEmbed, errorEmbed, formatUptime, hasAdminRole, hookStatus,
  hasModRole, hero, ipBans, isOwner,
  clearMenuLink, loadFactionBackup, loadMenuGrants, loadMenuLinks, loadMenuRoles, loadRoles, logAction, logger, modOnlyEmbed,
  FILES, safeRead,
  ownerOnlyEmbed, paginate, parseRcon, path, probeDetectors, readDonatorFile, redactPrivateInfo, sendRcon,
  removeDonator, removeMenuGrant, removeUserBlacklist, sanitizeBanName, sanitizeId,
  sanitizeMessage, saveFactionBackup, saveRoles, sendRconBoth, serverLabel, sysStats, meter,
  setMenuRole, spawn, textify, update, upsertPermBan, warningEmbed,
  wipeAllMoney, wipeAllPlayerData, writeModLog,
  BLACKLIST_IDS, BOT_START_MS,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         SETROLES
         ───────────────────────────────────────────────────── */

  /* ─────────────────────────────────────────────────────
         HEALTH - owner: uptime, server reachability, suppressed-error visibility
         ───────────────────────────────────────────────────── */
  "health": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        const checks = await Promise.allSettled(ACTIVE_SERVERS.map(s => sendRcon("RefreshList", s, 2500, 0)));
        const up = ACTIVE_SERVERS.map((s, i) => checks[i].status === "fulfilled" && !!parseRcon(checks[i].value)?.Successful);
        const mem = Math.round(process.memoryUsage().rss / 1024 / 1024);
        const lines = [
          `The bot has been up **${formatUptime(Date.now() - BOT_START_MS)}** and is using **${mem} MB** of memory.`,
          ACTIVE_SERVERS.map((s, i) => `${up[i] ? "🟢" : "🔴"} ${serverLabel(s)}`).join("  "),
          `Warnings since start: **${_diag.counts.warn}**. Errors: **${_diag.counts.error}**.`,
        ];
        /* Webhook logs. These post fire-and-forget, so a missing env var looks exactly
           like a quiet server - say which are actually wired up. */
        const hooks = Object.entries(hookStatus || {});
        if (hooks.length) {
          lines.push("Webhook logs: " + hooks
            .map(([label, state]) => `${state === "on" ? "🟢" : "🔴"} ${label} (${state})`).join("  "));
        }
        if (_diag.recent.length) {
          const recent = _diag.recent.slice(-5).map(r =>
            `${r.level === "error" ? "❌" : "⚠️"} [${r.tag}] ${redactPrivateInfo(r.message)} <t:${Math.floor(r.at / 1000)}:R>`);
          lines.push(`Most recent:\n${recent.join("\n")}`);
        } else {
          lines.push("No warnings or errors since the last restart.");
        }
        const ok = up.every(Boolean) && _diag.counts.error === 0;
        return interaction.editReply({ embeds: [brand(new EmbedBuilder()
          .setColor(ok ? NV.IRRAD_GREEN : NV.AMBER).setTitle("🩺 Bot Health")
          .setDescription(lines.join("\n")))] });
        },

  /* ─────────────────────────────────────────────────────
         STATS - owner: process + host resources, and the biggest data consumers
         ───────────────────────────────────────────────────── */
  "stats": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        const B   = sysStats.bytes;
        const mem = sysStats.processMemory();
        const sys = sysStats.systemInfo();
        const cpu = await sysStats.processCpuPercent(250);   // sampled window, not since-boot average
        const ds  = sysStats.datasetSizes(8);
        const dbSize = sysStats.dbFileSize();

        // Host CPU pressure from the 1-minute load average, normalised to core count.
        const loadPct = sys.cores ? Math.round((sys.loadAvg[0] / sys.cores) * 100) : 0;

        const proc = [
          `**RSS ${B(mem.rss)}** resident  -  heap **${B(mem.heapUsed)} / ${B(mem.heapTotal)}** (${mem.heapPct}%)`,
          `External (buffers, SQLite): **${B(mem.external)}**`,
          `CPU: **${cpu.ofOneCore}%** of one core  -  **${cpu.ofAllCores}%** of all ${sys.cores}`,
          `Bot uptime: **${formatUptime(Date.now() - BOT_START_MS)}**`,
        ].join("\n");

        const host = [
          `${sys.cpuModel}`,
          `**${sys.cores}** cores${sys.cpuSpeedMhz ? ` @ ${(sys.cpuSpeedMhz / 1000).toFixed(2)} GHz` : ""}  -  load **${sys.loadAvg.join("  ")}** (${loadPct}% of capacity)`,
          `RAM: ${meter(Math.round(sys.memUsed / 1048576), Math.round(sys.memTotal / 1048576), 12)}  -  **${B(sys.memUsed)} / ${B(sys.memTotal)}**`,
          `${sys.platform}  -  Node ${sys.nodeVersion}  -  host up ${formatUptime(sys.osUptime)}`,
        ].join("\n");

        // The measured answer to "what is using the most memory": every stored dataset
        // ranked by size. These rows are exactly what the in-memory read cache holds.
        const top = ds.rows.length
          ? ds.rows.map((r, i) =>
              `\`${String(i + 1).padStart(2)}\` **${r.name}**  -  ${B(r.bytes)}  \`${bar(r.bytes, ds.rows[0].bytes, 10)}\` ${r.pct}%`).join("\n")
          : "*No stored datasets yet.*";

        const embed = brand(new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("Resource Usage")
          .addFields(
            { name: "Bot process",  value: proc, inline: false },
            { name: "Host hardware", value: host, inline: false },
            { name: `Largest datasets (${B(ds.total)} across ${ds.count}, on disk ${B(dbSize)})`, value: top.slice(0, 1024), inline: false },
          )
          .setFooter({ text: "Dataset sizes are the stored bytes of each cache - the biggest are what the bot keeps in memory" }));
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         VPNCHECK - owner: probe every VPN detector and show its role
         ───────────────────────────────────────────────────── */
  "vpncheck": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        // 1.1.1.1 is a well-known clean resolver - a safe default probe target that
        // every detector recognises, so a "flagged" result here means a misconfiguration.
        const ip = sanitizeId(interaction.options.getString("ip")) || "1.1.1.1";
        let p;
        try { p = await probeDetectors(ip); }
        catch (e) { return interaction.editReply({ embeds: [errorEmbed("Probe Failed", e.message)] }); }

        const icon = (d) => d.status === "up" ? "🟢" : d.status === "not configured" ? "⬜" : "🔴";
        const row  = (d) => {
          const bits = [];
          if (d.status === "up") bits.push(d.flagged ? "**flagged**" : "clean", d.detail || null, `${d.ms}ms`);
          else if (d.status === "not configured") bits.push("*no API key set*");
          else if (d.status === "no verdict") bits.push("*responded but gave no verdict*");
          else bits.push(`*${d.error || "unreachable"}*`);
          return `${icon(d)} **${d.name}** - ${bits.filter(Boolean).join(" - ")}`;
        };
        const tierField = (tier) => {
          const rows = p.detectors.filter(d => d.tier === tier);
          return rows.length ? rows.map(row).join("\n").slice(0, 1024) : "*none*";
        };
        const allScreenUp = p.detectors.filter(d => d.tier === 1 && d.configured).every(d => d.status === "up");
        const anyScreen   = p.screenActive > 0;
        const colour = !anyScreen ? NV.RUST_RED : (allScreenUp && p.down === 0) ? NV.IRRAD_GREEN : NV.AMBER;
        const t = p.thresholds;
        const summary = [
          `Probed \`${ip}\` against every detector.`,
          `**${p.up}** up  -  **${p.down}** failing  -  **${p.detectors.length - p.up - p.down}** not configured`,
          `Regular check: **${p.screenActive}/${p.screenTotal}** active  -  Final confirmation: **${p.confirmActive}/${p.confirmTotal}** active`,
        ];
        if (!anyScreen) summary.push("🔴 **No regular-check detector is configured - VPN detection is OFF.**");
        else if (!p.confirmActive) summary.push(`⚠️ No final-confirmation detector configured: a ban needs **${t.screenBanMin}** regular checks to agree.`);
        const provider = p.detectors.find(d => d.provider)?.provider;

        const embed = brand(new EmbedBuilder().setColor(colour).setTitle("VPN Detection Health")
          .setDescription(summary.join("\n"))
          .addFields(
            { name: `Regular Check - runs on EVERY IP (${p.screenActive}/${p.screenTotal})`, value: tierField(1), inline: false },
            { name: `Final Confirmation - only on a flagged IP (${p.confirmActive}/${p.confirmTotal})`, value: tierField(2), inline: false },
            { name: "Consensus rules", value:
              `• Escalate to final confirmation at **${t.screenMin}+** regular-check hit(s)\n` +
              `• Auto-ban at **${t.confirmMin}+** final-confirmation hit(s)\n` +
              `• With no confirmation available, auto-ban needs **${t.screenBanMin}+** regular checks agreeing\n` +
              `• A unanimous confirmation all-clear **disputes** the flag (logged, never banned)`, inline: false },
            ...(provider ? [{ name: "Provider lookup", value: `Reported \`${provider}\` for this IP`, inline: false }] : []),
          ).setFooter({ text: "Each probe spends one lookup per configured detector" }));
        return interaction.editReply({ embeds: [embed] });
        },

  "setroles": async (interaction, name) => {
        const modRole    = interaction.options.getRole("mod_role");
        const adminRole  = interaction.options.getRole("admin_role");
        const flRole     = interaction.options.getRole("whitelist_leader_role");
        const policeRole = interaction.options.getRole("police_role");
        const gambinoRole = interaction.options.getRole("gambino_role");
        const colomboRole = interaction.options.getRole("colombo_role");
        const nypdRole    = interaction.options.getRole("nypd_role");
        if (!modRole && !adminRole && !flRole && !policeRole && !gambinoRole && !colomboRole && !nypdRole) {
          const c = loadRoles();
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.AMBER).setTitle("Role Configuration")
              .setDescription(`> *Current role settings. Pass role options to update.*\n\nIf no roles are configured, all commands are unrestricted.\n`)
              .addFields(
                { name: "Moderator",     value: c.modRoleId           ? `<@&${c.modRoleId}>`           : "`not set`", inline: true },
                { name: "Admin",          value: c.adminRoleId         ? `<@&${c.adminRoleId}>`         : "`not set`", inline: true },
                { name: "Whitelist Leader", value: c.factionLeaderRoleId ? `<@&${c.factionLeaderRoleId}>` : "`not set`", inline: true },
                { name: "Police Officer", value: c.policeRoleId         ? `<@&${c.policeRoleId}>`        : "`not set`", inline: true },
                { name: "Gambino",        value: c.gambinoRoleId        ? `<@&${c.gambinoRoleId}>`       : "`not set`", inline: true },
                { name: "Colombo",        value: c.colomboRoleId        ? `<@&${c.colomboRoleId}>`       : "`not set`", inline: true },
                { name: "NYPD",           value: c.nypdRoleId           ? `<@&${c.nypdRoleId}>`          : "`not set`", inline: true },
              ).setFooter({ text: "Pass role options to /setroles to update" })
          ], flags: MessageFlags.Ephemeral });
        }
        const c = loadRoles();
        if (modRole)     c.modRoleId           = modRole.id;
        if (adminRole)   c.adminRoleId         = adminRole.id;
        if (flRole)      c.factionLeaderRoleId = flRole.id;
        if (policeRole)  c.policeRoleId        = policeRole.id;
        if (gambinoRole) c.gambinoRoleId       = gambinoRole.id;
        if (colomboRole) c.colomboRoleId       = colomboRole.id;
        if (nypdRole)    c.nypdRoleId          = nypdRole.id;
        saveRoles(c);
        const changes = [modRole && `Mod → <@&${modRole.id}>`, adminRole && `Admin → <@&${adminRole.id}>`, flRole && `Whitelist → <@&${flRole.id}>`, policeRole && `Police → <@&${policeRole.id}>`, gambinoRole && `Gambino → <@&${gambinoRole.id}>`, colomboRole && `Colombo → <@&${colomboRole.id}>`, nypdRole && `NYPD → <@&${nypdRole.id}>`].filter(Boolean);
        const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("Role Permissions Updated")
          .setDescription(`${changes.join("\n")}\n\n— **${interaction.user.username}**`).setFooter({ text: "Takes effect immediately" });
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         DONATOR  (admin - manage the donator whitelist file)
         ───────────────────────────────────────────────────── */
  "donator": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();

        if (sub === "list") {
          const lines = readDonatorFile();
          if (lines === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Could not read the donator file.\n\`${DONATOR_FILE}\``)], flags: MessageFlags.Ephemeral });
          }
          if (!lines.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Donator List - Empty")
                .setDescription("No players are in the donator file yet.\n\nUse `/donator add` to enrol someone.")
            ], flags: MessageFlags.Ephemeral });
          }
          const out = lines.map((id, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${id}**`);
          return paginate(interaction, out, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`Donators - ${lines.length}`)
              .setDescription(pageLines.join("\n"))
              .setFooter({ text: DONATOR_FILE }),
            { perPage: 20, ephemeral: true });
        }

        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });

        if (sub === "add") {
          const { ok, already } = addDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], flags: MessageFlags.Ephemeral });
          if (already) return interaction.reply({ embeds: [warningEmbed("Already a Donator", `\`${playerId}\` is already in the donator file.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "donator-add", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Donator Added")
            .setDescription(`**${interaction.user.username}** added **${playerId}** to the donator file.`)
            .setFooter({ text: DONATOR_FILE });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        if (sub === "remove") {
          const { ok, missing } = removeDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], flags: MessageFlags.Ephemeral });
          if (missing) return interaction.reply({ embeds: [warningEmbed("Not a Donator", `\`${playerId}\` is not in the donator file.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "donator-remove", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Donator Removed")
            .setDescription(`**${interaction.user.username}** removed **${playerId}** from the donator file.`)
            .setFooter({ text: DONATOR_FILE });
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        return;
        },

  /* ─────────────────────────────────────────────────────
         ANNOUNCE
         ───────────────────────────────────────────────────── */
  "announce": async (interaction, name) => {
        const message = sanitizeMessage(interaction.options.getString("message"));
        const server  = "both"   /* server option removed - applies to all servers */;
        const rawTarget = interaction.options.getString("target");
        if (!message.trim()) return interaction.reply({ embeds: [errorEmbed("Empty Message", "Cannot broadcast an empty message.")], flags: MessageFlags.Ephemeral });
        const isAll  = !rawTarget || rawTarget.trim().toLowerCase() === "all";
        const target = isAll ? "All" : sanitizeId(rawTarget);
        if (!target) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        const rres = await sendRconBoth(`Notify ${target} ${message}`, server);
        // Pavlov RCON has no broadcast verb on stock builds; Notify is build/mod-dependent.
        // Heuristically detect whether the server acknowledged the command.
        const ackOne = (raw) => {
          if (raw == null) return null; // server not targeted
          const lower = raw.toLowerCase();
          if (!raw.trim()) return false; // silent - likely unrecognised
          // Treat explicit failure markers as a miss, anything else as accepted
          return !(lower.includes("unknown") || lower.includes("not recognized") ||
                   lower.includes("not recognised") || lower.includes("invalid") ||
                   lower.includes("\"successful\":false"));
        };
        const acks = [rres.s1, rres.s2, rres.s3].map(ackOne).filter(v => v !== null);
        const allOk  = acks.length > 0 && acks.every(Boolean);
        const anyOk  = acks.some(Boolean);
        writeModLog({ action: "announce", message, target, by: interaction.user.tag, server, delivered: allOk });
        const deliveryNote = allOk
          ? "Sent via RCON `Notify` - visible in-game if your build supports it."
          : anyOk
            ? "One server may not support `Notify`. Message logged here regardless."
            : "Server gave no acknowledgement - your Pavlov build may not support `Notify`. Message logged here only.";
        const embed = new EmbedBuilder().setColor(allOk ? NV.BLUE_VATS : NV.NCR_TAN).setTitle("Broadcast Sent")
          .setDescription(`> ${message}\n\n**${interaction.user.username}** broadcast to ${isAll ? "**all players**" : `\`${target}\``} on ${serverLabel(server)}. ${deliveryNote}`)
          .setFooter({ text: "RCON Notify broadcast" });
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         GIVEMENU / STRIPMENU  ← deferReply added
         ───────────────────────────────────────────────────── */
  "givemenu": async (interaction, name) => {
        const playerId  = sanitizeId(interaction.options.getString("playerid"));
        const server    = "both"   /* server option removed - applies to all servers */;
        const menuValue = interaction.options.getString("menu");
        const menuMeta  = MENUS.find(m => m.value === menuValue);
        const menuId    = menuMeta?.menuId ?? menuValue;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        // RCON+ targets the player's UniqueId, not their display name - resolve it
        // (live online id -> IP-tracker EOS id -> the name as a last resort).
        const target = sanitizeId(playerId);          // USERNAME, not EOS id
        if (menuValue === "highstaff") {
          // High Staff needs three distinct RCON commands - run each separately.
          await sendRconBoth(`AddMod ${target}`, server);
          await sendRconBoth(`AddAccessManager ${target}`, server);
          await sendRconBoth(`GiveMenu ${target} ${menuId}`, server);
        } else {
          await sendRconBoth(`GiveMenu ${target} ${menuId}`, server);
        }
        addMenuGrant(playerId, server, menuValue, menuId, interaction.user.tag);
        const embed = new EmbedBuilder().setColor(NV.AMBER)
          .setTitle("Menu Access Granted")
          .setDescription(`**${interaction.user.username}** granted **${menuMeta?.name ?? menuValue}** to **${playerId}** on ${serverLabel(server)}.\n-# Recorded for tracking - not re-applied automatically on rejoin.`)
          ;
        if (menuValue === "highstaff") {
          embed.addFields({ name: "Auto-applied (each run separately)", value: `\`\`\`\nAddMod ${playerId}\nAddAccessManager ${playerId}\nGiveMenu ${playerId} <menu bitmask>\n\`\`\`` , inline: false });
        }
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  "stripmenu": async (interaction, name) => {
        // RemoveMenu <user> clears the player's menu bit code regardless of which menu -
        // no menu choice, no bit code needed.
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const server   = "both"   /* server option removed - applies to all servers */;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();
        // If they were ever granted High Staff, also revoke Access Manager (RCON+).
        const wasHighStaff = (loadMenuGrants()[playerId.toLowerCase()] || []).some(g => g.menuValue === "highstaff");
        const target = sanitizeId(playerId);          // USERNAME, not EOS id
        const applied = [`RemoveMenu ${target}`];
        await sendRconBoth(`RemoveMenu ${target}`, server);            // clears the menu bit code
        if (wasHighStaff) {
          // AddMod was run at grant time - revoke it too, or the player keeps
          // in-game moderator powers after losing the menu.
          await sendRconBoth(`RemoveMod ${target}`, server);
          applied.push(`RemoveMod ${target}`);
          await sendRconBoth(`RemoveAccessManager ${target}`, server);
          applied.push(`RemoveAccessManager ${target}`);
        }
        // Clear every menu grant record for this player on the affected server(s).
        for (const m of MENUS) {
          if (server === "both") { for (const srv of ACTIVE_SERVERS) removeMenuGrant(playerId, srv, m.value); }
          removeMenuGrant(playerId, server, m.value);
        }
        const embed = brand(new EmbedBuilder().setColor(NV.NCR_TAN)
          .setTitle("Menu Access Revoked")
          .setDescription(`**${interaction.user.username}** revoked menu access from **${playerId}** on ${serverLabel(server)}.\n\`\`\`\n${applied.join("\n")}\n\`\`\``)
          );
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         STRIPMENUALL - owner only: clear EVERYONE's menu access
         ───────────────────────────────────────────────────── */
  /* ─────────────────────────────────────────────────────
         SETPIN / REMOVEPIN - close a server behind a join PIN
         ───────────────────────────────────────────────────── */
  "setpin": async (interaction, name) => {
        const server = interaction.options.getString("server") || "both";
        const raw    = (interaction.options.getString("pin") ?? "").trim();
        const reason = sanitizeMessage(interaction.options.getString("reason") ?? "") || null;
        /* Per the Pavlov RCON docs, SetPin takes 1-9999 with NO LEADING ZEROS, and a
           pin of 0 or "0000" sets the value to zero rather than removing it - a state
           worth refusing outright rather than handing someone a lock they think is off.
           Empty is equally dangerous in the other direction: a bare `SetPin` is what
           CLEARS the pin, so an empty argument would silently UNLOCK the server while
           reporting that it had been locked. */
        if (raw && !/^[1-9]\d{0,3}$/.test(raw)) {
          const why = /^0+$/.test(raw)            ? "A PIN of `0` sets the value to zero rather than locking - pick 1-9999."
                    : /^0\d+$/.test(raw)          ? "Leading zeros are not allowed - Pavlov reads the PIN as a number."
                    : "The PIN must be a number from **1 to 9999**, no leading zeros.";
          return interaction.reply({ embeds: [errorEmbed("Invalid PIN",
            `${why}\nLeave it blank and I'll generate one.`)], flags: MessageFlags.Ephemeral });
        }
        /* crypto randomness, not Math.random - this is the only thing between the
           public and a closed server. A generated PIN always uses the full 4 digits
           (1000-9999): shorter is permitted if you ask for it, but never chosen. */
        const pin = raw || String(1000 + (crypto.randomBytes(4).readUInt32BE(0) % 9000));

        /* Ephemeral: the PIN is a shared secret. A public reply would post it into
           channel history for anyone with read access, which defeats the lock. */
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        const res = await sendRconBoth(`SetPin ${pin}`, server);
        const applied = ACTIVE_SERVERS.filter(s => res[`ok${String(s).replace("server", "")}`]);
        if (!applied.length) {
          return interaction.editReply({ embeds: [errorEmbed("SetPin Failed",
            `No server accepted \`SetPin\` on ${serverLabel(server)}. Check RCON connectivity with \`/health\`.`)] });
        }
        /* Per-server state: with a server choice the servers can now genuinely diverge -
           one locked, one open - so a single global flag would misreport both. */
        await update(FILES.SERVER_LOCK, {}, (st) => {
          for (const s of applied) {
            st[s] = { locked: true, pin, reason, by: interaction.user.tag, byId: interaction.user.id, at: Date.now() };
          }
          return st;
        });
        logger.info("Lock", `${interaction.user.tag} set a PIN on ${applied.join(", ")}${reason ? ` - ${reason}` : ""}`);

        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Server Locked")
          .setDescription(`A join PIN is now set. Only players who enter it can connect.`)
          .addFields(
            { name: "PIN", value: `\`${pin}\``, inline: true },
            { name: "Applied to", value: applied.map(serverLabel).join(", "), inline: true },
            ...(reason ? [{ name: "Reason", value: reason, inline: false }] : []),
          )
          .setFooter({ text: "Share the PIN only with people who should get in. /removepin reopens the server." }));
        /* The staff log gets WHO and WHERE, never the PIN - that channel is visible to
           more people than should be able to join. */
        await logAction(brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Server Locked")
          .setDescription(`**${interaction.user.username}** set a join PIN on **${applied.map(serverLabel).join(", ")}**${reason ? ` - ${reason}` : "."}\nThe PIN was sent privately; run \`/setpin\` to see it.`)));
        return interaction.editReply({ embeds: [embed] });
        },

  "removepin": async (interaction, name) => {
        const server = interaction.options.getString("server") || "both";
        await interaction.deferReply();
        /* A bare `SetPin` - no argument - is what clears the pin. There is no
           RemovePin verb. */
        const res = await sendRconBoth("SetPin", server);
        const cleared = ACTIVE_SERVERS.filter(s => res[`ok${String(s).replace("server", "")}`]);
        if (!cleared.length) {
          return interaction.editReply({ embeds: [errorEmbed("Clear PIN Failed",
            `No server accepted \`SetPin\` (clear) on ${serverLabel(server)}. Check RCON connectivity with \`/health\`.`)] });
        }
        const prev = safeRead(FILES.SERVER_LOCK, {});
        // Drop the stored PIN for the servers actually reopened - keeping a stale
        // secret around serves nothing, and leaving it set would misreport the state.
        await update(FILES.SERVER_LOCK, {}, (st) => {
          for (const s of cleared) st[s] = { locked: false, unlockedBy: interaction.user.tag, at: Date.now() };
          return st;
        });
        logger.info("Lock", `${interaction.user.tag} removed the PIN on ${cleared.join(", ")}`);
        const held = cleared.map(s => (prev?.[s]?.at
          ? `${serverLabel(s)} was locked by ${prev[s].by ?? "someone"} <t:${Math.floor(prev[s].at / 1000)}:R>`
          : null)).filter(Boolean);
        const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Server Unlocked")
          .setDescription(`**${interaction.user.username}** removed the join PIN on **${cleared.map(serverLabel).join(", ")}**. Anyone can join again.`
            + (held.length ? `\n${held.join("\n")}` : "")));
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* A member's in-game name link is permanent by design - that is what stops someone
     dropping their menu and re-claiming it on an alt. This is the only way to break
     one, for a genuine Pavlov name change. Logged, because it re-opens that door. */
  "unlinkname": async (interaction, name) => {
        const user = interaction.options.getUser("user");
        const link = loadMenuLinks()[user.id];
        if (!link?.name) {
          return interaction.reply({ embeds: [warningEmbed("Not Linked", `<@${user.id}> isn't linked to an in-game name.`)], flags: MessageFlags.Ephemeral });
        }
        await interaction.deferReply();
        await clearMenuLink(user.id);
        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Name Link Broken")
          .setDescription(`**${interaction.user.username}** unlinked <@${user.id}> from \`${link.name}\`.\nThey can now register a different in-game name.`));
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  "stripmenuall": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply();

        // ClearMenuAccess wipes every player's menu access; ClearAccessManagers wipes
        // everyone's access-manager rights (both RCON+, no args).
        await sendRconBoth("ClearMenuAccess", "both");
        await sendRconBoth("ClearAccessManagers", "both");
        // Clear every menu grant record across both servers.
        const grants = loadMenuGrants();
        const holders = Object.keys(grants);
        // There is no ClearMods verb - revoke AddMod per player for every High Staff
        // grant on record, or they keep in-game moderator powers after the wipe.
        for (const pid of holders) {
          if ((grants[pid] || []).some(g => g.menuValue === "highstaff")) {
            await sendRconBoth(`RemoveMod ${sanitizeId(pid)}`, "both");
          }
        }
        for (const pid of holders) for (const m of MENUS) {
          removeMenuGrant(pid, "server1", m.value);
          removeMenuGrant(pid, "server2", m.value);
          removeMenuGrant(pid, "server3", m.value);
          removeMenuGrant(pid, "both", m.value);
        }

        const embed = brand(new EmbedBuilder().setColor(NV.LEGION_RED)
          .setTitle("Mass Menu Revocation")
          .setDescription(`${hero("Cleared menu access for every player on both servers.")}\n\`ClearMenuAccess\` - \`ClearAccessManagers\` - **${holders.length}** grant(s) cleared.`)
          );
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         SETFACTIONADMIN - owner sets this guild's Whitelist Leader role

      /* ─────────────────────────────────────────────────────
         SETRCONROLES - which Discord role grants each RCON menu
         ───────────────────────────────────────────────────── */
  "setrconroles": async (interaction, name) => {
        if (!hasAdminRole(interaction.member) && !isOwner(interaction.user.id)) return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const hs = interaction.options.getRole("high_staff_role");
        const st = interaction.options.getRole("staff_role");
        if (hs) await setMenuRole("highstaff", hs.id);
        if (st) await setMenuRole("staff", st.id);
        const m = loadMenuRoles();
        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("RCON Menu Roles")
          .setDescription((hs || st) ? "Updated. Members who press **Get Menu** get the menu of their highest role below." : "Current mapping. Pass role options to change. Members get the menu of their **highest** role.")
          .addFields(
            { name: "High Staff", value: m.highstaff ? `<@&${m.highstaff}>` : "*(unset)*", inline: true },
            { name: "Staff",       value: m.staff     ? `<@&${m.staff}>`     : "*(unset)*", inline: true },
          ).setFooter({ text: "Priority: High Staff > Staff" }));
        await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         CONFIGURE - owner-only hidden controls (blacklist, IP, factions)
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         LINK - owner: link a Discord account to a Pavlov username
         ───────────────────────────────────────────────────── */
  "configure": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });

        // Grouped by area (emoji-prefixed) - Discord select menus have no native
        // optgroups, so the emoji + ordering carry the sections visually.
        const menu = new StringSelectMenuBuilder().setCustomId("cfg_menu").setPlaceholder("Choose an owner action...")
          .addOptions(
            // ── IP enforcement ──
            { label: "Blacklist IP / username",   value: "blacklist_ip",   emoji: "🚫", description: "Auto-ban anyone matching an IP or username" },
            { label: "View blacklist",            value: "view_blacklist", emoji: "📋", description: "All blacklisted IPs, usernames and account IDs" },
            { label: "View alt accounts",         value: "view_alts",      emoji: "🕵️", description: "A player's known alts (shared confirmed IP)" },
            { label: "Clear a specific IP",       value: "clear_ip",       emoji: "🧹", description: "Un-flag + remove one IP" },
            { label: "Clear flagged usernames",   value: "clear_names",    emoji: "🧹", description: "Stop all 'blacklisted username' auto-bans" },
            { label: "Clear all flagged IPs",     value: "clear_flags",    emoji: "🧹", description: "Stop every IP auto-ban (keep history)" },
            { label: "Wipe ALL IP data",          value: "clear_all",      emoji: "💥", description: "Full registry + flag reset (irreversible)" },
            // ── firewall ──
            { label: "Firewall - blocked IPs",    value: "firewall_status", emoji: "🔥", description: "Every IP currently denied at the OS firewall" },
            // ── discord access ──
            { label: "Bar a Discord user",        value: "user_bl_add",    emoji: "⛔", description: "Block a Discord user from ALL bot commands" },
            { label: "Un-bar a Discord user",     value: "user_bl_remove", emoji: "✅", description: "Restore a Discord user's command access" },
            { label: "List barred Discord users", value: "user_bl_list",   emoji: "📋", description: "Show Discord users barred from commands" },
            // ── IP tracking ──
            { label: "Ignore a username",         value: "ignore_add",     emoji: "🙈", description: "Stop tracking a player's IPs" },
            { label: "Un-ignore a username",      value: "ignore_remove",  emoji: "👁️", description: "Resume tracking a player" },
            { label: "List ignored usernames",    value: "ignore_list",    emoji: "📋", description: "Show the ignore list" },
            // ── factions ──
            { label: "Save whitelists",          value: "save_factions",  emoji: "💾", description: "Snapshot all whitelist spawn + rank files" },
            { label: "Load whitelists",          value: "load_factions",  emoji: "♻️", description: "Restore the last snapshot (overwrites current)" },
            // ── economy ──
            { label: "Wipe ALL money",            value: "wipe_money",     emoji: "💰", description: "Delete every player's ledger file from ModSave (irreversible)" },
            // ── data ──
            { label: "Wipe ALL player data",      value: "wipe_players",   emoji: "🧨", description: "Clear the registry, K/D, playtime, last-seen (irreversible)" },
          );
        const panel = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Owner Control Panel")
          .setDescription(`${hero("Owner-only controls, all in one place.")}\nPick an action below - destructive ones ask you to confirm first.\n` +
            `🚫 **IP Enforcement** - blacklist, alts, clear flags\n` +
            `🔥 **Firewall** - view blocked IPs\n` +
            `⛔ **Discord Access** - bar / un-bar users\n` +
            `👁️ **IP Tracking** - ignore lists\n` +
            `💾 **Whitelists** - save / load\n` +
            `💰 **Economy** - wipe money\n` +
            `🧨 **Data** - wipe all player tracking`)
          .setFooter({ text: "Owner only - sensitive - menu closes after 60s" }));
        // ── all action logic in one place; returns a branded result embed ──
        const audit = (embed) => { Promise.resolve(logAction(embed)).catch(() => {}); return embed; };
        async function runAction(choice, val) {
          if (choice === "view_alts") {
            let alts = []; try { alts = ipBans.getAltNamesOf(val); } catch {}
            const list = alts.length ? alts.map(n => `• **${n}**`).join("\n").slice(0, 4000) : "*No known alt accounts (no other account shares a confirmed IP).*";
            return brand(new EmbedBuilder().setColor(alts.length ? NV.LEGION_RED : NV.IRRAD_GREEN).setTitle(`Alt Accounts - ${val}`)
              .addFields({ name: `Linked accounts (${alts.length})`, value: list, inline: false })
              .setFooter({ text: "Alt links come from confirmed shared IPs" }));
          }
          if (choice === "blacklist_ip") {
            const r = ipBans.flagTarget(val);
            const toBan = new Set();
            if (r.kind === "username" && r.value) toBan.add(r.value);
            for (const id of r.ids) { const nm = ipBans.registry[id]?.name; if (nm) toBan.add(nm); }
            for (const nm of toBan) { try { await banWithIp(nm, "both", { permanent: true }); await upsertPermBan({ playerId: nm, reason: "Blacklisted via /configure", moderator: interaction.user.tag }); } catch {} }
            const desc = `${r.kind} \`${r.value}\` blacklisted - any account matching it is auto-banned.` +
              (toBan.size ? `\nBanned & kicked **${toBan.size}** matching name(s) now.` : `\nNo accounts on record yet - future connections will be caught.`);
            return audit(brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Blacklisted").setDescription(hero(desc))));
          }
          if (choice === "wipe_money") {
            if (val.toUpperCase() !== "WIPE") return brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **WIPE** to confirm - no money was wiped."));
            const r = wipeAllMoney();
            return audit(brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Money Wiped").setDescription(hero(r.ok ? `Deleted **${r.wiped}** player ledger(s) from ModSave${r.failed ? ` (**${r.failed}** could not be deleted)` : ""}.` : `Wipe failed: ${r.error}`))));
          }
          if (choice === "wipe_players") {
            if (val.toUpperCase() !== "WIPE") return brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **WIPE** to confirm - no data was wiped."));
            const r = wipeAllPlayerData();
            return audit(brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Player Data Wiped")
              .setDescription(hero(`Cleared the bot's player registry and stats.`) +
                `\n• Registry records: **${r.registry}**  (flags: **${r.flagged}**)` +
                `\n• K/D on record: **${r.kd}**` +
                `\n• Playtime entries: **${r.playtime}**` +
                `\n• Last-seen entries: **${r.lastseen}**` +
                `\n• Known players: **${r.known}**` +
                `\n\nBans, blacklist, warrants, whitelists, and donators were **not** touched.`)));
          }
          if (choice === "load_factions") {
            if (val.toUpperCase() !== "LOAD") return brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **LOAD** to confirm - nothing was restored."));
            const r = loadFactionBackup();
            return audit(brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Whitelists Restored").setDescription(hero(r.ok ? `Restored **${r.restored}** whitelist file(s)${r.savedAt ? ` from the snapshot saved <t:${Math.floor(r.savedAt / 1000)}:R>` : ""}.` : (r.empty ? "No saved snapshot found - use **Save whitelists** first." : `Load failed: ${r.error}`)))));
          }
          if (["user_bl_add", "user_bl_remove", "ignore_add", "ignore_remove", "clear_ip"].includes(choice)) {
            let desc, color = NV.IRRAD_GREEN;
            if (choice === "user_bl_add")         { const uid = val.replace(/\D/g, ""); const added = uid && addUserBlacklist(uid); color = NV.LEGION_RED; desc = added ? `<@${uid}> (\`${uid}\`) is barred from ALL bot commands.` : `\`${uid || val}\` was already barred or isn't a valid ID.`; }
            else if (choice === "user_bl_remove") { const uid = val.replace(/\D/g, ""); const removed = uid && removeUserBlacklist(uid); desc = removed ? `<@${uid}> (\`${uid}\`) can use commands again.` : `\`${uid || val}\` wasn't on the barred list.`; }
            else if (choice === "ignore_add")     { const r = ipBans.addUntracked(val); desc = `**${val}** will no longer be tracked. Purged **${r.purged}** record(s). (No IP logging, feed, or auto-ban for this name.)`; }
            else if (choice === "ignore_remove")  { const ok2 = ipBans.removeUntracked(val); desc = ok2 ? `**${val}** is tracked again from their next connection.` : `**${val}** wasn't on the ignore list.`; }
            else                                  { const r = ipBans.clearIp(val); desc = `\`${val}\` - ${r.flagRemoved ? "un-flagged" : "was not flagged"}, removed from **${r.players}** record(s).`; }
            return audit(brand(new EmbedBuilder().setColor(color).setTitle("Done").setDescription(hero(desc))));
          }
          if (choice === "firewall_status") {
            let st; try { st = await firewallStatus(); } catch (e) { st = { error: e.message }; }
            if (st?.off)   return brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Firewall - Disabled").setDescription("OS firewall blocking is off (set **UFW_BLOCK=1** to enable)."));
            if (st?.error) return brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle("Firewall - Status Unavailable").setDescription(`Could not read \`sudo ufw status numbered\`.\n\`\`\`${st.error}\`\`\``));
            const denied = st.denied || [];
            const listed = denied.map(ip => `${st.isFlagged(ip) ? GLYPH.deny : GLYPH.dot} \`${ip}\`${st.isFlagged(ip) ? "  *(flagged)*" : ""}`).join("\n").slice(0, 3600) || "*No IPs are currently denied at the firewall.*";
            const warn = (st.mastersBlocked?.length ? `\n⚠ **Master IP(s) blocked:** ${st.mastersBlocked.map(x => `\`${x}\``).join(", ")} - reconcile will clear them.` : "")
              + (st.flaggedNotBlocked?.length ? `\n⚠ **${st.flaggedNotBlocked.length} flagged IP(s) not yet blocked** - reconcile will re-apply.` : "");
            return brand(new EmbedBuilder().setColor(denied.length ? NV.LEGION_RED : NV.IRRAD_GREEN).setTitle("Firewall - Blocked IPs")
              .setDescription(`**${denied.length}** IP${denied.length !== 1 ? "s" : ""} denied (ufw ${st.active ? "**active**" : "inactive"}) - **${st.flaggedCount ?? 0}** flagged in ipBans.${warn}\n${listed}`)
              .setFooter({ text: "sudo ufw status numbered - owner - sensitive" }));
          }
          if (choice === "view_blacklist") {
            const b = ipBans.getBlacklist();
            const fmt = (a) => a.length ? a.map(x => `\`${x}\``).join("  -  ").slice(0, 1024) : "*none*";
            return brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Blacklist")
              .addFields(
                { name: `IPs (${b.ips.length})`,        value: fmt(b.ips),   inline: false },
                { name: `Usernames (${b.names.length})`, value: fmt(b.names), inline: false },
                { name: `Account IDs (${(b.ids || []).length})`, value: fmt(b.ids || []), inline: false },
              ));
          }
          if (choice === "ignore_list")  { const n = ipBans.getUntracked(); return brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Ignored Usernames").setDescription(n.length ? n.map(x => `• \`${x}\``).join("\n").slice(0, 4000) : "No usernames are ignored - everyone is tracked.")); }
          if (choice === "user_bl_list") { const ids = [...BLACKLIST_IDS]; return brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Barred Discord Users").setDescription(ids.length ? ids.map(x => `• <@${x}> \`${x}\``).join("\n").slice(0, 4000) : "No Discord users are barred from commands.")); }
          let desc, color = NV.AMBER;
          if (choice === "clear_names")  { const n = ipBans.clearFlaggedNames(); color = NV.LEGION_RED; desc = `Removed **${n}** flagged username${n !== 1 ? "s" : ""}. No more "blacklisted username" auto-bans. (Flagged IPs kept.)`; }
          else if (choice === "clear_flags") { const n = ipBans.clearFlags(); color = NV.LEGION_RED; desc = `Removed **${n}** flagged IP${n !== 1 ? "s" : ""}. No IP auto-bans until new bans flag IPs again. (History kept.)`; }
          else if (choice === "clear_all")   { const r = ipBans.clearAll(); color = NV.LEGION_RED; desc = `Wiped **${r.ids}** player record(s) and **${r.flagged}** flagged IP${r.flagged !== 1 ? "s" : ""}. Rebuilds from the logs as players connect.`; }
          else if (choice === "save_factions") { const r = saveFactionBackup(); desc = r.ok ? `Snapshot saved - **${r.count}** whitelist file(s). Use **Load whitelists** to restore them later.` : `Save failed: ${r.error}`; }
          else { desc = "Unknown action."; }
          return audit(brand(new EmbedBuilder().setColor(color).setTitle("Done").setDescription(hero(desc))));
        }

        const MODAL = { ignore_add: ["Ignore a username", "Username"], ignore_remove: ["Un-ignore a username", "Username"], clear_ip: ["Clear a specific IP", "IP address"], blacklist_ip: ["Blacklist IP / username", "IP or username"], view_alts: ["View alt accounts", "Player username"], user_bl_add: ["Bar a Discord user", "Discord user ID"], user_bl_remove: ["Un-bar a Discord user", "Discord user ID"], wipe_money: ["Wipe ALL money", "Type WIPE to confirm"], wipe_players: ["Wipe ALL player data", "Type WIPE to confirm"], load_factions: ["Restore whitelists", "Type LOAD to confirm"] };
        const menuRow = () => new ActionRowBuilder().addComponents(menu);
        const navRow  = () => new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("cfg_back").setLabel("Back to menu").setEmoji("◀️").setStyle(ButtonStyle.Secondary),
          new ButtonBuilder().setCustomId("cfg_close").setLabel("Close").setEmoji("✖️").setStyle(ButtonStyle.Danger),
        );

        await interaction.reply({ embeds: [panel], components: [menuRow()], flags: MessageFlags.Ephemeral });
        const msg = await interaction.fetchReply();
        const IDLE = 180_000;

        // Persistent control panel: keep serving actions until Close / idle timeout.
        for (;;) {
          let sel;
          try { sel = await msg.awaitMessageComponent({ componentType: ComponentType.StringSelect, time: IDLE, filter: i => i.user.id === interaction.user.id }); }
          catch { return interaction.editReply({ components: [] }).catch(() => {}); }   // idle -> disable
          const choice = sel.values[0];

          let result;
          if (MODAL[choice]) {
            const [title, label] = MODAL[choice];
            const input = new TextInputBuilder().setCustomId("cfg_val").setLabel(label).setStyle(TextInputStyle.Short).setRequired(true).setMaxLength(64);
            const modal = new ModalBuilder().setCustomId("cfg_modal_" + choice).setTitle(title).addComponents(new ActionRowBuilder().addComponents(input));
            try { await sel.showModal(modal); } catch { continue; }
            let sub;
            try { sub = await sel.awaitModalSubmit({ time: 120_000, filter: i => i.user.id === interaction.user.id && i.customId === "cfg_modal_" + choice }); }
            catch { continue; }   // cancelled -> menu is still up, loop
            await sub.deferUpdate().catch(() => {});
            try { result = await runAction(choice, sub.fields.getTextInputValue("cfg_val").trim()); }
            catch (e) { result = brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle("Action Failed").setDescription(`\`\`\`${e.message}\`\`\``)); }
          } else {
            await sel.deferUpdate().catch(() => {});
            try { result = await runAction(choice); }
            catch (e) { result = brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle("Action Failed").setDescription(`\`\`\`${e.message}\`\`\``)); }
          }

          await interaction.editReply({ embeds: [result], components: [navRow()] }).catch(() => {});
          let nav;
          try { nav = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: IDLE, filter: i => i.user.id === interaction.user.id && ["cfg_back", "cfg_close"].includes(i.customId) }); }
          catch { return interaction.editReply({ components: [] }).catch(() => {}); }   // idle -> leave result, disable
          await nav.deferUpdate().catch(() => {});
          if (nav.customId === "cfg_close") return interaction.editReply({ components: [] }).catch(() => {});
          await interaction.editReply({ embeds: [panel], components: [menuRow()] }).catch(() => {});   // Back -> restore menu
        }
        },

  };
};
