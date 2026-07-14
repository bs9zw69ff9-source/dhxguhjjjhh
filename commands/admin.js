/* ---------------- commands/admin: /setroles /donator /announce /givemenu /stripmenu /stripmenuall /setrconroles /link /configure /autorotate ----------------
   Split from commands/index.js. Each handler receives (interaction, name) and
   closes over the shared ctx (injected from index.js via the dispatcher). */
module.exports = (ctx) => {
  const {
  ActionRowBuilder, ButtonBuilder, ButtonStyle, ComponentType, DIVIDER, DONATOR_FILE,
  EmbedBuilder, LINK_REQUEST_CHANNEL, MENUS, MessageFlags, ModalBuilder, NV,
  StringSelectMenuBuilder, TextInputBuilder, TextInputStyle, addDonator, addMenuGrant, addUserBlacklist,
  adminOnlyEmbed, banWithIp, bar, brand, client, commands,
  deniedEmbed, discordIdForPavlov, easternClock, emptyIdEmbed, errorEmbed, hasAdminRole,
  hasModRole, hero, ipBans, isOwner, loadAutoRotate, loadDiscordLinks,
  loadFactionBackup, loadMenuGrants, loadMenuRoles, loadRoles, logAction, modOnlyEmbed,
  ownerOnlyEmbed, paginate, parseClockTime, path, randomQuote, readDonatorFile,
  removeDiscordLink, removeDonator, removeMenuGrant, removeUserBlacklist, sanitizeBanName, sanitizeId,
  sanitizeMessage, saveFactionBackup, saveRoles, sendRconBoth, serverLabel, setAutoRotate,
  setMenuRole, spawn, textify, update, upsertPermBan, warningEmbed,
  wipeAllMoney, writeModLog,
  } = ctx;

  return {

  /* ─────────────────────────────────────────────────────
         SETROLES
         ───────────────────────────────────────────────────── */
  "setroles": async (interaction, name) => {
        const modRole   = interaction.options.getRole("mod_role");
        const adminRole = interaction.options.getRole("admin_role");
        const flRole    = interaction.options.getRole("faction_leader_role");
        if (!modRole && !adminRole && !flRole) {
          const c = loadRoles();
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.AMBER).setTitle("Role Configuration")
              .setDescription(`> *Current role settings. Pass role options to update.*\n\nIf no roles are configured, all commands are unrestricted.\n\n${DIVIDER}`)
              .addFields(
                { name: "Moderator",     value: c.modRoleId           ? `<@&${c.modRoleId}>`           : "`not set`", inline: true },
                { name: "Admin",          value: c.adminRoleId         ? `<@&${c.adminRoleId}>`         : "`not set`", inline: true },
                { name: "Faction Leader", value: c.factionLeaderRoleId ? `<@&${c.factionLeaderRoleId}>` : "`not set`", inline: true },
              ).setFooter({ text: "Pass role options to /setroles to update" }).setTimestamp()
          ], flags: MessageFlags.Ephemeral });
        }
        const c = loadRoles();
        if (modRole)   c.modRoleId           = modRole.id;
        if (adminRole) c.adminRoleId         = adminRole.id;
        if (flRole)    c.factionLeaderRoleId = flRole.id;
        saveRoles(c);
        const changes = [modRole && `Mod → <@&${modRole.id}>`, adminRole && `Admin → <@&${adminRole.id}>`, flRole && `Faction → <@&${flRole.id}>`].filter(Boolean);
        const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("Role Permissions Updated")
          .setDescription(`${changes.join("\n")}\n\n— ${interaction.user}`).setFooter({ text: "Takes effect immediately" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         DONATOR  (admin — manage the donator whitelist file)
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
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Donator List — Empty")
                .setDescription("No players are in the donator file yet.\n\nUse `/donator add` to enrol someone.").setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          const out = lines.map((id, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${id}**`);
          return paginate(interaction, out, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`Donators — ${lines.length}`)
              .setDescription(`> *"The House remembers its most generous patrons."*\n\n${DIVIDER}\n${pageLines.join("\n")}`)
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
            .setDescription(`> *"A generous soul joins the ranks of the server's patrons."*\n\n${interaction.user} added **${playerId}** to the donator file.`)
            .setFooter({ text: DONATOR_FILE }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        if (sub === "remove") {
          const { ok, missing } = removeDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], flags: MessageFlags.Ephemeral });
          if (missing) return interaction.reply({ embeds: [warningEmbed("Not a Donator", `\`${playerId}\` is not in the donator file.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "donator-remove", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Donator Removed")
            .setDescription(`${interaction.user} removed **${playerId}** from the donator file.`)
            .setFooter({ text: DONATOR_FILE }).setTimestamp();
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
        const server  = interaction.options.getString("server");
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
          ? "Sent via RCON `Notify` — visible in-game if your build supports it."
          : anyOk
            ? "One server may not support `Notify`. Message logged here regardless."
            : "Server gave no acknowledgement — your Pavlov build may not support `Notify`. Message logged here only.";
        const embed = new EmbedBuilder().setColor(allOk ? NV.BLUE_VATS : NV.NCR_TAN).setTitle("Broadcast Sent")
          .setDescription(`> *${randomQuote("announce")}*\n\n> ${message}\n\n${interaction.user} broadcast to ${isAll ? "**all players**" : `\`${target}\``} on ${serverLabel(server)}. ${deliveryNote}`)
          .setFooter({ text: "RCON Notify broadcast" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         GIVEMENU / STRIPMENU  ← deferReply added
         ───────────────────────────────────────────────────── */
  "givemenu": async (interaction, name) => {
        const playerId  = sanitizeId(interaction.options.getString("playerid"));
        const server    = interaction.options.getString("server");
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
          .setDescription(`${interaction.user} granted **${menuMeta?.name ?? menuValue}** to **${playerId}** on ${serverLabel(server)}.\n-# Recorded for tracking — not re-applied automatically on rejoin.`)
          .setTimestamp();
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
        const server   = interaction.options.getString("server");
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
          .setDescription(`${interaction.user} revoked menu access from **${playerId}** on ${serverLabel(server)}.\n\`\`\`\n${applied.join("\n")}\n\`\`\``)
          .setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         STRIPMENUALL — owner only: clear EVERYONE's menu access
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
          .setDescription(`${hero("Cleared menu access for every player on both servers.")}\n\`ClearMenuAccess\` · \`ClearAccessManagers\` — **${holders.length}** grant(s) cleared.`)
          .setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
        },

  /* ─────────────────────────────────────────────────────
         SETFACTIONADMIN — owner sets this guild's Faction Leader role

      /* ─────────────────────────────────────────────────────
         SETRCONROLES — which Discord role grants each RCON menu
         ───────────────────────────────────────────────────── */
  "setrconroles": async (interaction, name) => {
        if (!hasAdminRole(interaction.member) && !isOwner(interaction.user.id)) return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const hs = interaction.options.getRole("high_staff_role");
        const st = interaction.options.getRole("staff_role");
        const fa = interaction.options.getRole("faction_role");
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
          ).setFooter({ text: "Priority: High Staff > Staff > Faction" }).setTimestamp());
        await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },

  /* ─────────────────────────────────────────────────────
         CONFIGURE — owner-only hidden controls (blacklist, IP, factions)
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         LINK — owner: link a Discord account to a Pavlov username
         ───────────────────────────────────────────────────── */
  "link": async (interaction, name) => {
        const sub = interaction.options.getSubcommand();

        if (sub === "list") {
          if (!hasModRole(interaction.member)) return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
          const links = loadDiscordLinks();
          const ids = Object.keys(links);
          if (!ids.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Discord Links").setDescription("No accounts are linked yet.")], flags: MessageFlags.Ephemeral });
          const lines = ids.map(id => `<@${id}>  →  \`${links[id].name}\``);
          return paginate(interaction, lines, (pageLines) =>
            brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Discord ↔ Pavlov Links")
              .setDescription(`${DIVIDER}\n${pageLines.join("\n")}`)), { perPage: 20, ephemeral: true });
        }

        if (sub === "remove") {
          if (!hasModRole(interaction.member)) return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
          const user = interaction.options.getUser("discord_user");
          const had = loadDiscordLinks()[user.id];
          await removeDiscordLink(user.id);
          writeModLog({ action: "unlink", targetUserId: user.id, by: interaction.user.tag });
          return interaction.reply({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Link Removed")
            .setDescription(had ? `Unlinked ${user} *(was \`${had.name}\`)*.` : `${user} had no link.`))], flags: MessageFlags.Ephemeral });
        }

        // add — PUBLIC: request to link YOUR OWN Discord to a Pavlov name; staff approves.
        // Hard one-to-one rules, enforced BEFORE any request is posted:
        //   • an account that already holds a link cannot use the command (staff must
        //     /link remove it first), and
        //   • a Pavlov name that is already linked to someone is auto-denied outright.
        const pavlov = sanitizeBanName(interaction.options.getString("pavlov"));   // preserves spaces in names
        if (!pavlov) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const existing = loadDiscordLinks()[interaction.user.id];
        if (existing) {
          return interaction.reply({ embeds: [deniedEmbed("Already Linked",
            `Your Discord is already linked to \`${existing.name}\`. One link per account — ask a mod to \`/link remove\` it first if it's wrong.`,
            "One Pavlov name per Discord account")], flags: MessageFlags.Ephemeral });
        }
        const clash = discordIdForPavlov(pavlov);
        if (clash) {
          writeModLog({ action: "link-denied", targetUserId: interaction.user.id, playerId: pavlov, reason: `name already linked to ${clash}`, by: "auto" });
          return interaction.reply({ embeds: [deniedEmbed("Name Already Claimed",
            `\`${pavlov}\` is already linked to another Discord account. If that's YOUR in-game name, tell a mod — they can \`/link remove\` the false claim.`,
            "Auto-denied — no request sent")], flags: MessageFlags.Ephemeral });
        }
        let ch = null;
        try { ch = await client.channels.fetch(LINK_REQUEST_CHANNEL); } catch {}
        if (!ch?.isTextBased()) {
          return interaction.reply({ embeds: [errorEmbed("Requests Unavailable", "The link-request channel is not reachable — tell an admin.")], flags: MessageFlags.Ephemeral });
        }
        const reqEmbed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Link Request — Pending")
          .setDescription(`${interaction.user} (\`${interaction.user.id}\`) wants to link to \`${pavlov}\`.`)
          .setFooter({ text: "Approve or deny below" }).setTimestamp());
        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId(`linkreq_ok:${interaction.user.id}:${encodeURIComponent(pavlov)}`).setLabel("Accept").setStyle(ButtonStyle.Success),
          new ButtonBuilder().setCustomId(`linkreq_no:${interaction.user.id}:${encodeURIComponent(pavlov)}`).setLabel("Deny").setStyle(ButtonStyle.Danger),
        );
        try { await ch.send(textify({ embeds: [reqEmbed], components: [row] })); }   // no staff ping — the card in the channel is enough
        catch (e) { return interaction.reply({ embeds: [errorEmbed("Request Failed", `Couldn't post the request: ${e.message}`)], flags: MessageFlags.Ephemeral }); }
        return interaction.reply({ embeds: [brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Link Request Sent")
          .setDescription(`Your request to link to \`${pavlov}\` is pending staff approval. You'll be DM'd the result.`))], flags: MessageFlags.Ephemeral });
        },

  "configure": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });

        const menu = new StringSelectMenuBuilder().setCustomId("cfg_menu").setPlaceholder("Select a hidden command…")
          .addOptions(
            { label: "Blacklist IP / username", value: "blacklist_ip", description: "Auto-ban anyone matching an IP or username" },
            { label: "View blacklist",          value: "view_blacklist", description: "Show all blacklisted IPs and usernames" },
            { label: "View alt accounts",       value: "view_alts",      description: "A player's known alt accounts (shared IP)" },
            { label: "Bar a Discord user",     value: "user_bl_add",    description: "Block a Discord user from ALL bot commands" },
            { label: "Un-bar a Discord user",  value: "user_bl_remove", description: "Restore a Discord user's command access" },
            { label: "List barred Discord users", value: "user_bl_list", description: "Show Discord users barred from commands" },
            { label: "Ignore a username",      value: "ignore_add",    description: "Stop tracking a player's IPs" },
            { label: "Un-ignore a username",   value: "ignore_remove", description: "Resume tracking a player" },
            { label: "List ignored usernames", value: "ignore_list",   description: "Show the ignore list" },
            { label: "Clear flagged usernames", value: "clear_names",  description: "Stop all 'blacklisted username' auto-bans" },
            { label: "Clear all flagged IPs",  value: "clear_flags",   description: "Stop every IP auto-ban (keep history)" },
            { label: "Clear a specific IP",    value: "clear_ip",      description: "Un-flag + remove one IP" },
            { label: "Wipe ALL IP data",       value: "clear_all",     description: "Full registry + flag reset" },
            { label: "Save faction whitelists", value: "save_factions", description: "Snapshot all faction spawn + rank files" },
            { label: "Load faction whitelists", value: "load_factions", description: "Restore the last snapshot (overwrites current)" },
            { label: "Wipe ALL money",          value: "wipe_money",    description: "Set every player's credits to 0 (irreversible)" },
          );
        const panel = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Configure — Hidden Commands"));
        await interaction.reply({ embeds: [panel], components: [new ActionRowBuilder().addComponents(menu)], flags: MessageFlags.Ephemeral });
        const msg = await interaction.fetchReply();

        let sel;
        try { sel = await msg.awaitMessageComponent({ componentType: ComponentType.StringSelect, time: 60_000, filter: i => i.user.id === interaction.user.id }); }
        catch { return interaction.editReply({ components: [] }).catch(() => {}); }
        const choice = sel.values[0];

        // actions that need text input -> open a modal
        if (["ignore_add", "ignore_remove", "clear_ip", "blacklist_ip", "view_alts", "user_bl_add", "user_bl_remove", "wipe_money", "load_factions"].includes(choice)) {
          const titleByChoice = { ignore_add: "Ignore a username", ignore_remove: "Un-ignore a username", clear_ip: "Clear a specific IP", blacklist_ip: "Blacklist IP / username", view_alts: "View alt accounts", user_bl_add: "Bar a Discord user", user_bl_remove: "Un-bar a Discord user", wipe_money: "Wipe ALL money", load_factions: "Restore faction whitelists" };
          const labelByChoice = { ignore_add: "Username", ignore_remove: "Username", clear_ip: "IP address", blacklist_ip: "IP or username", view_alts: "Player username", user_bl_add: "Discord user ID", user_bl_remove: "Discord user ID", wipe_money: "Type WIPE to confirm", load_factions: "Type LOAD to confirm" };
          const input = new TextInputBuilder().setCustomId("cfg_val").setLabel(labelByChoice[choice]).setStyle(TextInputStyle.Short).setRequired(true).setMaxLength(64);
          const modal = new ModalBuilder().setCustomId("cfg_modal").setTitle(titleByChoice[choice]).addComponents(new ActionRowBuilder().addComponents(input));
          await sel.showModal(modal);
          let sub;
          try { sub = await sel.awaitModalSubmit({ time: 120_000, filter: i => i.user.id === interaction.user.id && i.customId === "cfg_modal" }); }
          catch { return; }
          const val = sub.fields.getTextInputValue("cfg_val").trim();
          let desc, color = NV.IRRAD_GREEN;
          if (choice === "view_alts") {
            let alts = [];
            try { alts = ipBans.getAltNamesOf(val); } catch {}
            const list = alts.length
              ? alts.map(n => `• **${n}**`).join("\n").slice(0, 4000)
              : "*No known alt accounts (no other account shares a confirmed IP).*";
            const eAlt = brand(new EmbedBuilder().setColor(alts.length ? NV.LEGION_RED : NV.IRRAD_GREEN)
              .setTitle(`Alt Accounts — ${val}`)
              .addFields({ name: `Linked accounts (${alts.length})`, value: list, inline: false })
              .setFooter({ text: "Alt links come from confirmed shared IPs" }).setTimestamp());
            return sub.reply({ embeds: [eAlt], flags: MessageFlags.Ephemeral });
          }
          if (choice === "blacklist_ip") {
            await sub.deferReply({ flags: MessageFlags.Ephemeral });
            const r = ipBans.flagTarget(val);            // IPv4 detected by shape; else a username
            // Ban (blacklist.txt + kick + IP-flag) the username itself and every on-record
            // account matching the target, so an in-game player is removed immediately.
            const toBan = new Set();
            if (r.kind === "username" && r.value) toBan.add(r.value);
            for (const id of r.ids) { const nm = ipBans.registry[id]?.name; if (nm) toBan.add(nm); }
            for (const nm of toBan) { try { await banWithIp(nm, "both", { permanent: true }); await upsertPermBan({ playerId: nm, reason: "Blacklisted via /configure", moderator: interaction.user.tag }); } catch {} }
            color = NV.LEGION_RED;
            desc = `${r.kind} \`${r.value}\` blacklisted — any account matching it is auto-banned.` +
              (toBan.size ? `\nBanned & kicked **${toBan.size}** matching name(s) now.` : `\nNo accounts on record yet — future connections will be caught.`);
            const e1 = brand(new EmbedBuilder().setColor(color).setTitle("Blacklisted").setDescription(hero(desc)).setTimestamp());
            await logAction(e1);
            return sub.editReply({ embeds: [e1] });
          }
          if (choice === "wipe_money") {
            await sub.deferReply({ flags: MessageFlags.Ephemeral });
            if (val.toUpperCase() !== "WIPE") return sub.editReply({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **WIPE** to confirm — no money was wiped."))] });
            const r = wipeAllMoney();
            const e = brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Money Wiped")
              .setDescription(hero(r.ok ? `Set **${r.wiped}** of ${r.total} player balance(s) to **0**.` : `Wipe failed: ${r.error}`)).setTimestamp());
            await logAction(e);
            return sub.editReply({ embeds: [e] });
          }
          if (choice === "load_factions") {
            await sub.deferReply({ flags: MessageFlags.Ephemeral });
            if (val.toUpperCase() !== "LOAD") return sub.editReply({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **LOAD** to confirm — nothing was restored."))] });
            const r = loadFactionBackup();
            const e = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Faction Whitelists Restored")
              .setDescription(hero(r.ok ? `Restored **${r.restored}** faction file(s)${r.savedAt ? ` from the snapshot saved <t:${Math.floor(r.savedAt / 1000)}:R>` : ""}.` : (r.empty ? "No saved snapshot found — use **Save faction whitelists** first." : `Load failed: ${r.error}`))).setTimestamp());
            await logAction(e);
            return sub.editReply({ embeds: [e] });
          }
          if (choice === "user_bl_add")        { const uid = val.replace(/\D/g, ""); const added = uid && addUserBlacklist(uid); color = NV.LEGION_RED; desc = added ? `<@${uid}> (\`${uid}\`) is barred from ALL bot commands.` : `\`${uid || val}\` was already barred or isn't a valid ID.`; }
          else if (choice === "user_bl_remove") { const uid = val.replace(/\D/g, ""); const removed = uid && removeUserBlacklist(uid); desc = removed ? `<@${uid}> (\`${uid}\`) can use commands again.` : `\`${uid || val}\` wasn't on the barred list.`; }
          else if (choice === "ignore_add")    { const r = ipBans.addUntracked(val); desc = `**${val}** will no longer be tracked. Purged **${r.purged}** record(s). (No IP logging, feed, or auto-ban for this name.)`; }
          else if (choice === "ignore_remove") { const ok2 = ipBans.removeUntracked(val); desc = ok2 ? `**${val}** is tracked again from their next connection.` : `**${val}** wasn't on the ignore list.`; }
          else                               { const r = ipBans.clearIp(val); desc = `\`${val}\` — ${r.flagRemoved ? "un-flagged" : "was not flagged"}, removed from **${r.players}** record(s).`; }
          const e = brand(new EmbedBuilder().setColor(color).setTitle("Done").setDescription(hero(desc)).setTimestamp());
          await logAction(e);
          return sub.reply({ embeds: [e], flags: MessageFlags.Ephemeral });
        }

        // view the blacklist (IPs / usernames)
        if (choice === "view_blacklist") {
          const b = ipBans.getBlacklist();
          const fmt = (a) => a.length ? a.map(x => `\`${x}\``).join("  ·  ").slice(0, 1024) : "*none*";
          const e = brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Blacklist")
            .addFields(
              { name: `IPs (${b.ips.length})`,        value: fmt(b.ips),   inline: false },
              { name: `Usernames (${b.names.length})`, value: fmt(b.names), inline: false },
              { name: `Account IDs (${(b.ids || []).length})`, value: fmt(b.ids || []), inline: false },
            ).setTimestamp());
          return sel.update({ embeds: [e], components: [] });
        }

        // direct actions (no input)
        let desc, color = NV.AMBER, audit = true;
        if (choice === "ignore_list")      { const n = ipBans.getUntracked(); desc = n.length ? n.map(x => `• \`${x}\``).join("\n").slice(0, 4000) : "No usernames are ignored — everyone is tracked."; audit = false; }
        else if (choice === "user_bl_list") { const ids = [...BLACKLIST_IDS]; desc = ids.length ? ids.map(x => `• <@${x}> \`${x}\``).join("\n").slice(0, 4000) : "No Discord users are barred from commands."; audit = false; }
        else if (choice === "clear_names") { const n = ipBans.clearFlaggedNames(); color = NV.LEGION_RED; desc = `Removed **${n}** flagged username${n !== 1 ? "s" : ""}. No more "blacklisted username" auto-bans. (Flagged IPs kept.)`; }
        else if (choice === "clear_flags") { const n = ipBans.clearFlags(); color = NV.LEGION_RED; desc = `Removed **${n}** flagged IP${n !== 1 ? "s" : ""}. No IP auto-bans until new bans flag IPs again. (History kept.)`; }
        else if (choice === "clear_all")   { const r = ipBans.clearAll(); color = NV.LEGION_RED; desc = `Wiped **${r.ids}** player record(s) and **${r.flagged}** flagged IP${r.flagged !== 1 ? "s" : ""}. Rebuilds from the logs as players connect.`; }
        else if (choice === "save_factions") { const r = saveFactionBackup(); color = NV.AMBER; desc = r.ok ? `Snapshot saved — **${r.count}** faction file(s). Use **Load faction whitelists** to restore them later.` : `Save failed: ${r.error}`; }
        const e = brand(new EmbedBuilder().setColor(color).setTitle("Configure").setDescription(hero(desc)).setTimestamp());
        if (audit) await logAction(e);
        return sel.update({ embeds: [e], components: [] });
        },

  /* ─────────────────────────────────────────────────────
         AUTOROTATE — owner: schedule a daily map rotation (Eastern)
         ───────────────────────────────────────────────────── */
  "autorotate": async (interaction, name) => {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const sub = interaction.options.getSubcommand();
        const cfg = loadAutoRotate();

        if (sub === "off") {
          setAutoRotate({});
          return interaction.reply({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Auto-Rotation Disabled")
            .setDescription(cfg.time ? `Stopped the daily map rotation *(was ${cfg.time} Eastern)*.` : "There was no rotation scheduled."))], flags: MessageFlags.Ephemeral });
        }

        if (sub === "status") {
          const desc = cfg.time
            ? `The map rotates **every day at ${cfg.time} Eastern** on **${serverLabel(cfg.server || "both")}**.${cfg.lastRun ? ` Last rotated ${cfg.lastRun}.` : " Hasn't rotated yet."}`
            : "No rotation scheduled. Use `/autorotate set` to add one.";
          const embed = brand(new EmbedBuilder().setColor(cfg.time ? NV.IRRAD_GREEN : NV.DEAD_GREY).setTitle("Map Auto-Rotation").setDescription(desc));
          const now = easternClock();
          embed.setFooter({ text: `Server clock: ${now.hm} Eastern` });
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        // set
        const time = parseClockTime(interaction.options.getString("time"));
        if (!time) return interaction.reply({ embeds: [errorEmbed("Invalid Time",
          "Enter a time like `03:00`, `18:30`, `3pm`, or `6:30pm` — interpreted as **Eastern**.")], flags: MessageFlags.Ephemeral });
        const server = interaction.options.getString("server") || "both";
        setAutoRotate({ time, server, lastRun: null });
        writeModLog({ action: "autorotate-set", time, server, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Auto-Rotation Scheduled")
          .setDescription(`The map will rotate **every day at ${time} Eastern** on **${serverLabel(server)}** (\`RotateMap\`).`)
          .setFooter({ text: `Server clock: ${easternClock().hm} Eastern · checked every minute` }).setTimestamp());
        await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        },
  };
};
