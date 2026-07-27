/* ---------------- commands: slash-command + component interaction handler ----------------
   Extracted from index.js. This is the single onInteraction dispatcher for every
   slash command, button, modal, select, and autocomplete. It's a leaf consumer —
   nothing else calls into it except client.on("interactionCreate"). Every helper,
   loader, embed builder, RCON call and moderation action it uses is injected via
   ctx (a plain object of the bot's shared functions/state built in index.js), so
   this file has no forward wiring of its own.

   Usage:  const { onInteraction } = require("./commands")(ctx);
*/
module.exports = function createCommands(ctx) {
  const {
  ALL_RANK_NAMES, ActionRowBuilder, DIVIDER, EmbedBuilder, MessageFlags, easternDate,
  ModalBuilder, NV, TextInputBuilder, TextInputStyle, adminOnlyEmbed, blacklistedEmbed,
  brand, checkRateLimit, client, commandPlayerCandidates, commands,
  errorEmbed, factionLeaderOnlyEmbed, getFactionRankBadge, getFactionRankOrder, getPlayerChoices,
  handleMenuPanelSubmit, handleVerifySubmit, handleVerifyDecision, hasAdminRole, hasFactionLeaderRole, hasWhitelistManageRole, hasModRole, isBlacklisted, isOwner,
  logger, modOnlyEmbed,
  patchInteractionOutput, rateLimitEmbed, textify, writeModLog,
  } = ctx;

  // Command handlers, split by domain (each takes the same ctx).
  const _handlers = Object.assign({},
    require("./info")(ctx), require("./moderation")(ctx), require("./admin")(ctx),
    require("./factions")(ctx), require("./economy")(ctx), require("./warrant")(ctx), require("./police")(ctx));


  async function onInteraction(interaction) {

  // No-embeds mode: render every embed payload to plain text at send time.
  // Registered before any collector, so component/modal interactions from
  // awaitMessageComponent / awaitModalSubmit flows are patched too.
  try { patchInteractionOutput(interaction); } catch {}

  /* ── Blacklist gate - barred users get nothing, on every interaction.
        Owners are immune and can never be blacklisted. ── */
  if (isBlacklisted(interaction.user.id) && !isOwner(interaction.user.id)) {
    if (interaction.isAutocomplete()) return interaction.respond([]).catch(() => {});
    if (interaction.isChatInputCommand()) {
      return interaction.reply({ embeds: [blacklistedEmbed()], flags: MessageFlags.Ephemeral }).catch(() => {});
    }
    return;
  }

  /* ── Panel buttons + modals ─────────────────────────── */
  // On failure, tell the user instead of leaving the modal stuck on "thinking...".
  const modalFail = (tag) => (e) => {
    logger.warn(tag, e.message);
    const payload = { embeds: [errorEmbed("Something Went Wrong", "That didn't go through - try again in a moment.")], flags: MessageFlags.Ephemeral };
    (interaction.deferred || interaction.replied ? interaction.followUp(payload) : interaction.reply(payload)).catch(() => {});
  };
  if (interaction.isButton() && interaction.customId === "menu_start") {
    const modal = new ModalBuilder().setCustomId("menu_modal").setTitle("Get RCON menu access")
      .addComponents(new ActionRowBuilder().addComponents(
        new TextInputBuilder().setCustomId("menu_name").setLabel("Your exact Pavlov in-game name")
          .setStyle(TextInputStyle.Short).setRequired(true).setMaxLength(64)));
    return interaction.showModal(modal).catch(() => {});
  }
  if (interaction.isModalSubmit() && interaction.customId === "menu_modal") {
    return handleMenuPanelSubmit(interaction).catch(modalFail("MenuPanel"));
  }
  /* ── verification: button -> name modal -> staff accept/deny (persistent) ── */
  if (interaction.isButton() && interaction.customId === "verify_start") {
    const modal = new ModalBuilder().setCustomId("verify_modal").setTitle("Verify")
      .addComponents(new ActionRowBuilder().addComponents(
        new TextInputBuilder().setCustomId("verify_name").setLabel("Your exact Pavlov in-game name")
          .setStyle(TextInputStyle.Short).setRequired(true).setMaxLength(64)));
    return interaction.showModal(modal).catch(() => {});
  }
  if (interaction.isModalSubmit() && interaction.customId === "verify_modal") {
    return handleVerifySubmit(interaction).catch(modalFail("Verify"));
  }
  if (interaction.isButton() && interaction.customId.startsWith("verifyreq_")) {
    return handleVerifyDecision(interaction).catch(e => logger.warn("Verify", `decision failed: ${e.message}`));
  }

  // Any OTHER component belongs to a command's collector (paginator, confirm
  // buttons, config dropdowns). If that collector expired, nothing will ever ack
  if (interaction.isMessageComponent()) {
    setTimeout(() => {
      if (!interaction.replied && !interaction.deferred) interaction.deferUpdate().catch(() => {});
    }, 2500);
    return;
  }
  if (interaction.isModalSubmit()) {   // e.g. cfg_modal submitted after its 120s collector expired
    setTimeout(() => {
      if (!interaction.replied && !interaction.deferred) {
        interaction.reply({ content: "This form expired - run the command again.", flags: MessageFlags.Ephemeral }).catch(() => {});
      }
    }, 2500);
    return;
  }

  /* ── Autocomplete ─────────────────────────────────────── */
  if (interaction.isAutocomplete()) {
    const focused  = interaction.options.getFocused(true);
    const cmdName  = interaction.commandName;

    // /tempban date - quick calendar suggestions (always future dates, YYYY-MM-DD)
    if (focused.name === "date" && cmdName === "tempban") {
      const q = focused.value.trim();
      // Eastern calendar day - late-evening ET would otherwise suggest tomorrow's UTC date.
      const iso = (days) => easternDate(Date.now() + days * 86_400_000);
      const opts = [
        { name: `Tomorrow (${iso(1)})`,  value: iso(1) },
        { name: `In 3 days (${iso(3)})`, value: iso(3) },
        { name: `In 1 week (${iso(7)})`, value: iso(7) },
        { name: `In 2 weeks (${iso(14)})`, value: iso(14) },
        { name: `In 1 month (${iso(30)})`, value: iso(30) },
        { name: `In 3 months (${iso(90)})`, value: iso(90) },
        { name: `In 6 months (${iso(180)})`, value: iso(180) },
        { name: `In 1 year (${iso(365)})`, value: iso(365) },
      ];
      if (/^\d{4}-\d{1,2}-\d{1,2}$/.test(q)) opts.unshift({ name: `${q} (12pm Eastern)`, value: q });   // honour a typed date
      return interaction.respond(opts.filter(o => !q || o.value.includes(q) || o.name.toLowerCase().includes(q.toLowerCase())).slice(0, 25)).catch(() => {});
    }

    if (focused.name === "rank" && cmdName === "whitelist") {
      const faction = interaction.options.getString("whitelist");
      if (faction) {
        const order = getFactionRankOrder(faction);
        const query = focused.value.toLowerCase();
        const matches = order
          .filter(r => !query || r.toLowerCase().includes(query))
          .map(r => ({ name: `${getFactionRankBadge(faction, r)}  ${r}`, value: r }));
        return interaction.respond(matches.slice(0, 25)).catch(() => {});
      }
      const query = focused.value.toLowerCase();
      const matches = ALL_RANK_NAMES.filter(r => !query || r.name.toLowerCase().includes(query));
      return interaction.respond(matches.slice(0, 25)).catch(() => {});
    }

    const query = focused.value.toLowerCase();

    // Context-aware player field: only suggest the population relevant to the command.
    const ctx = commandPlayerCandidates(interaction);
    if (ctx !== null) {
      const out = [...new Set(ctx.filter(Boolean))]
        .filter(n => !query || n.toLowerCase().includes(query))
        .slice(0, 25)
        .map(n => ({ name: n, value: n }));
      if (focused.value && !out.find(c => c.value.toLowerCase() === query)) {
        out.unshift({ name: focused.value, value: focused.value });
      }
      return interaction.respond(out.slice(0, 25)).catch(() => {});
    }

    // Default: currently-online players, then previously-seen (offline) ones.
    const server  = interaction.options.getString("server") ?? null;
    const choices = getPlayerChoices(server, query);
    if (query && !choices.find(c => c.value.toLowerCase() === query)) {
      choices.unshift({ name: focused.value, value: focused.value });
    }
    // /announce target field also offers "All"
    if (cmdName === "announce" && focused.name === "target" && (!query || "all".includes(query))) {
      choices.unshift({ name: "All players", value: "all" });
    }
    return interaction.respond(choices.slice(0, 25)).catch(() => {});
  }

  if (!interaction.isChatInputCommand()) return;

  // Guild-only: in a DM there's no member object, so every role check would be
  // meaningless (and used to crash). Answer cleanly instead.
  if (!interaction.inGuild()) {
    return interaction.reply({ content: "Run commands in the server, not in DMs.", flags: MessageFlags.Ephemeral }).catch(() => {});
  }

  /* ── Permission routing ───────────────────────────────── */
  const PUBLIC         = ["help", "serverinfo", "checkban"];
  const MOD_COMMANDS   = ["kick", "flush", "tempban", "unban", "announce", "givecaps"];
  const FL_COMMANDS    = ["whitelist"];
  const ADMIN_COMMANDS = ["permban", "cleartempbans", "setroles", "givemenu", "stripmenu", "unlinkname", "manual", "adjustcaps", "donator", "staffactivity", "staffleaderboard"];

  const name = interaction.commandName;

  if (!PUBLIC.includes(name)) {
    if (ADMIN_COMMANDS.includes(name) && !hasAdminRole(interaction.member)) {
      return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
    }
    // /whitelist's read-only subcommands (list / audit / playtime) are public - only
    // the mutating ones need the Whitelist Leader / Mod gate. Management is
    // per-faction: the general Whitelist Leader role manages every whitelist, while
    // a faction role (Gambino/Colombo) manages only its own. Which faction is being
    // touched comes from the `whitelist` option on the subcommand.
    const factionPublicSub = name === "whitelist" &&
      ["list", "playtime"].includes(interaction.options.getSubcommand(false));
    if (FL_COMMANDS.includes(name) && !factionPublicSub && !hasModRole(interaction.member)
        && !hasWhitelistManageRole(interaction.member, interaction.options.getString("whitelist"))) {
      return interaction.reply({ embeds: [factionLeaderOnlyEmbed()], flags: MessageFlags.Ephemeral });
    }
    if (MOD_COMMANDS.includes(name) && !hasModRole(interaction.member)) {
      return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
    }
  }

  if (!ADMIN_COMMANDS.includes(name) && !PUBLIC.includes(name) && !isOwner(interaction.user.id)) {
    if (!checkRateLimit(interaction.user.id, name, 4000)) {
      return interaction.reply({ embeds: [rateLimitEmbed()], flags: MessageFlags.Ephemeral });
    }
  }

  try {
    const handler = _handlers[name];
    if (handler) return await handler(interaction, name);

      return interaction.reply({ embeds: [errorEmbed("Unknown Command", `\`/${name}\` isn't wired up in this build - the command list may still be refreshing. Try again in a minute.`)], flags: MessageFlags.Ephemeral }).catch(() => {});

  } catch (err) {
    logger.error("Command", `/${interaction.commandName}: ${err.message}`, { stack: err.stack });
    const reply = {
      embeds: [errorEmbed("System Failure", `An internal error occurred processing \`/${interaction.commandName}\`.\n\n\`\`\`${err.message?.slice(0, 200) ?? "unknown"}\`\`\`\nCheck the server logs for the full stack trace.`)],
      flags: MessageFlags.Ephemeral,
    };
    try {
      if (interaction.deferred || interaction.replied) return interaction.editReply(reply);
      return interaction.reply(reply);
    } catch {}
  }
}

  return { onInteraction };
};
