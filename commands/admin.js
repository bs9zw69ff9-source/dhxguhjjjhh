/* ---------------- commands/admin: /setroles /donator /announce /givemenu /stripmenu /stripmenuall /setrconroles /configure ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  ACTIVE_SERVERS, ActionRowBuilder, ButtonBuilder, ButtonStyle, ComponentType, DIVIDER, DONATOR_FILE, GLYPH, CLIN,
  EmbedBuilder, MENUS, MessageFlags, ModalBuilder, NV, clinical, firewallStatus,
  StringSelectMenuBuilder, TextInputBuilder, TextInputStyle, addDonator, addMenuGrant, addUserBlacklist,
  adminOnlyEmbed, banWithIp, bar, brand, client, commands,
  _diag, deniedEmbed, emptyIdEmbed, errorEmbed, formatUptime, hasAdminRole,
  hasModRole, hero, ipBans, isOwner,
  loadFactionBackup, loadMenuGrants, loadMenuRoles, loadRoles, logAction, modOnlyEmbed,
  ownerOnlyEmbed, paginate, parseRcon, path, randomQuote, readDonatorFile, redactPrivateInfo, sendRcon,
  removeDonator, removeMenuGrant, removeUserBlacklist, sanitizeBanName, sanitizeId,
  sanitizeMessage, saveFactionBackup, saveRoles, sendRconBoth, serverLabel,
  setMenuRole, spawn, textify, update, upsertPermBan, warningEmbed,
  wipeAllMoney, writeModLog,
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
              .setDescription(`> *"The House remembers its most generous patrons."*\n\n${pageLines.join("\n")}`)
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
            .setDescription(`> *"A generous soul joins the ranks of the server's patrons."*\n\n**${interaction.user.username}** added **${playerId}** to the donator file.`)
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
        const fa = interaction.options.getRole("whitelist_role");
        if (hs) await setMenuRole("highstaff", hs.id);
        if (st) await setMenuRole("staff", st.id);
        if (fa) await setMenuRole("faction", fa.id);
        const m = loadMenuRoles();
        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("RCON Menu Roles")
          .setDescription((hs || st || fa) ? "Updated. Members who press **Get Menu** get the menu of their highest role below." : "Current mapping. Pass role options to change. Members get the menu of their **highest** role.")
          .addFields(
            { name: "High Staff", value: m.highstaff ? `<@&${m.highstaff}>` : "*(unset)*", inline: true },
            { name: "Staff",       value: m.staff     ? `<@&${m.staff}>`     : "*(unset)*", inline: true },
            { name: "Faction",     value: m.faction   ? `<@&${m.faction}>`   : "*(unset)*", inline: true },
          ).setFooter({ text: "Priority: High Staff > Staff > Whitelist" }));
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
          );
        const panel = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Owner Control Panel")
          .setDescription(`${hero("Owner-only controls, all in one place.")}\nPick an action below - destructive ones ask you to confirm first.\n` +
            `🚫 **IP Enforcement** - blacklist, alts, clear flags\n` +
            `🔥 **Firewall** - view blocked IPs\n` +
            `⛔ **Discord Access** - bar / un-bar users\n` +
            `👁️ **IP Tracking** - ignore lists\n` +
            `💾 **Whitelists** - save / load\n` +
            `💰 **Economy** - wipe money`)
          .setFooter({ text: "Owner only - sensitive - menu closes after 60s" }));
        // ── all action logic in one place; returns a branded result embed ──
        const audit = (embed) => { logAction(embed).catch(() => {}); return embed; };
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

        const MODAL = { ignore_add: ["Ignore a username", "Username"], ignore_remove: ["Un-ignore a username", "Username"], clear_ip: ["Clear a specific IP", "IP address"], blacklist_ip: ["Blacklist IP / username", "IP or username"], view_alts: ["View alt accounts", "Player username"], user_bl_add: ["Bar a Discord user", "Discord user ID"], user_bl_remove: ["Un-bar a Discord user", "Discord user ID"], wipe_money: ["Wipe ALL money", "Type WIPE to confirm"], load_factions: ["Restore whitelists", "Type LOAD to confirm"] };
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
