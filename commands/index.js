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
  ACTIVE_SERVERS, ALL_FACTIONS, ALL_RANK_NAMES, APPEAL_LINK, ActionRowBuilder, ActivityType,
  BAN_DURATIONS, BAN_REASON_LABELS, BAN_RECONCILE_MIN_INTERVAL_MS, BLACKLIST_IDS, BOT_AUTHOR, BOT_COPYRIGHT,
  BOT_START_MS, BOT_VERSION, BRAND_NAME, BUILD_ID, ButtonBuilder, ButtonStyle,
  CACHE_TTL_MS, CARD_RANKS, CARD_SUITS, CASINO_CONFIG_DEFAULTS, CLIN, CURRENT_LOG_LEVEL,
  Client, ComponentType, DASHBOARD_CHANNEL, DASHBOARD_INTERVAL_MS, DAY_MS, DB_EXPORT_INTERVAL_MS,
  DIVIDER, DONATOR_FILE, EXTRA_FACTION_FILES, EmbedBuilder, FACTION_BAK_DIR, FACTION_BOT,
  FACTION_BULK_DROP_LIMIT, FACTION_COMMAND_NAMES, FACTION_DEFAULT_CAP, FACTION_RANKS, FACTION_ROLES_PATH, FACTION_SPAWN_MAP,
  FILES, GAMBLE_QUOTA_MAX, GAMBLE_QUOTA_WINDOW_MS, GAME_ICON, GIT_SAFE, GLYPH,
  GatewayIntentBits, IPHUB_API_KEY, IPINFO_TOKEN, IPQS_API_KEY, JACKPOT_MIN_BALANCE, JACKPOT_WIN_CHANCE,
  KILLFEED_CHANNEL, LEADERBOARD_INTERVAL_MS, LEADERBOARD_TOP_N, LINK_APPROVER_ROLE, LINK_REQUEST_CHANNEL, LOCK_FILE,
  LOG_FILE, LOG_LEVEL, MASTER_NAMES, MENUS, MENU_PANEL_CHANNEL, MENU_ROLE_DEFAULTS,
  MODSAVE_REL, MODSAVE_SYNC_INTERVAL_MS, MODSAVE_SYNC_SKIP, MessageFlags, ModalBuilder, NV,
  OWNER_IDS, PAVLOV_BASES, PAVLOV_BASE_1, PLAYERLIST_CHANNEL, PLAYERLIST_INTERVAL_MS, PLAYTIME_LB_CHANNEL,
  PUNISHMENTS, PUNISH_BY_VALUE, PUNISH_CHOICES, PermissionFlagsBits, QUOTES, RCON_BLACKLIST_ROLE_ID,
  RCON_HEALTH_INTERVAL_MS, REST, ROULETTE_COLOR_EMOJI, ROULETTE_RED, ROULETTE_SPACES, RULE,
  RUSSIAN_ROULETTE_MULTS, RoleSelectMenuBuilder, Routes, SLOT_SYMBOLS, SPAWN_FILE_MAP, STAFF_MENU_ID,
  SlashCommandBuilder, StringSelectMenuBuilder, TextInputBuilder, TextInputStyle, UFW_BLOCK, UNBARRED_IDS,
  UPDATE_LOG_CHANNEL, WAGE_INTERVAL_MS, WAGE_TIERS, WebhookClient, _IPV4_RE, __dirname,
  _backfillGeo, _blAllCache, _doVpnCheck, _hasRole, _ledgerQueues, _modLogIndexCache,
  _recentRegrant, _reconcileBusy, _regionName, _sameId, _sweepBusy, _vpnInFlight,
  acquireSingleInstanceLock, addAutobanExempt, addDonator, addMenuGrant, addPlayerToRankFile, addToPot,
  addUserBlacklist, adminOnlyEmbed, allCachedPlayers, applyMuteOnJoin, atomicCopyPreservingMtime, atomicWriteFile,
  autoBackupFactions, autoBanDecision, awaitOwnedComponent, backupFactionFile, banWithIp, bar,
  blacklistAdd, blacklistAll, blacklistAllCached, blacklistHas, blacklistPathFor, blacklistRemove,
  blacklistStatus, blacklistedEmbed, brand, brandIcon, buildDashboardEmbed, buildFactionMembershipIndex,
  buildLeaderboardData, buildLeaderboardEmbed, buildModsaveBanlist, buildPlayerListEmbed, buildPlaytimeLeaderboardData, buildPlaytimeLeaderboardEmbed,
  cardValue, casinoIntake, casinoResultEmbed, cell, checkAutoRotate, checkGambleQuota,
  checkRateLimit, checkVpn, checkVpnAndAlert, chunkFields, clampEmbed, clearMenuLink,
  clearMute, client, clinical, commandPlayerCandidates, commands, commitSubjectsBetween,
  confirmDialog, countFactionRank, creditCaps, currentGitCommit, currentPot, customEmoji,
  dashboardSnapshots, debitCaps, deniedEmbed, disableValidators, discordIdForPavlov, discoverPavlovBases,
  dmPunishmentNotice, dmStatusField, dmUserForPavlov, drainPot, easternClock, easternNoonUTC,
  embedToText, emptyIdEmbed, enforceBansSweep, ensureFactionFiles, ensureFile, ensureMenuPanel,
  errorEmbed, execFile, execFileSync, exportDbToJson, extractPlayerNames, factionChoices,
  factionClient, factionCommands, factionKillBreakdown, factionLeaderOnlyEmbed, factionLeaderStrictEmbed, fclient,
  feedHook, firewallBlockIps, firewallField, firewallResyncAll, firewallUnblockIps, fixAutoBanReasons,
  formatFullLocation, formatHand, formatKD, formatPlaytime, formatTimeLeft, formatUptime,
  freshDeck, fs, gagEverywhere, gambleQuotaLimitEmbed, geoLookup, getAutopostMsgId,
  getFactionCap, getFactionDefaultRank, getFactionMembers, getFactionRank, getFactionRankBadge, getFactionRankCap,
  getFactionRankCaps, getFactionRankConfig, getFactionRankOrder, getKnownPlayerChoices, getLastSeen, getModsavePath,
  getMute, getOnlinePlayers, getPlayerChoices, getPlayerFactions, getPlayerFilePath, getPlayerHistory,
  getPlayerRanks, getServerConfig, grantMasterMenu, handValue, handleMenuPanelSubmit, hardEnforce,
  hasAdminRole, hasFactionLeaderRole, hasModRole, hasServer2, hasServer3, healTreeOwnership,
  hero, hudRow, importBlacklistToBans, importModsaveBanlist, intendedOwner, ipBans,
  isAutobanExempt, isBlackjack, isBlacklisted, isDonator, isMasterName, isOwner,
  isPidAlive, isPlayerOnline, isProtectedPlayer, isRealBan, listFilesRec, loadAutoRotate,
  loadAutobanExempt, loadAutopostState, loadBans, loadCasinoConfig, loadDiscordLinks, loadDonatorSuspends,
  loadFactionAudit, loadFactionBackup, loadFactionConfig, loadFactionRanks, loadKnownPlayers, loadLastSeen,
  loadMenuGrants, loadMenuLinks, loadMenuRoles, loadModLog, loadMutes, loadPlaytime,
  loadRoles, loadVpnChecks, loadWages, log, logAction, logBan,
  logger, looksLikeLedgerEntry, mainCommands, matchTreeOwner, md5, memberHasRoleId,
  menuLinkActive, menuRoleTiers, meter, mirrorPaths, modOnlyEmbed, modsaveBanlistPath,
  mutateBalance, onlineServersOf, ownerOnlyEmbed, paginate, parseClockTime, parseDuration,
  parseRcon, patchInteractionOutput, path, pip, playerCache, postDashboard,
  postFeed, postKillFeed, postLeaderboard, postPlayerList, postPlaytimeLeaderboard, postToUpdateLogChannel,
  postUpdateLogIfChanged, preserveBalanceAcrossKick, processDonatorRestores, processExpiredBans, processWagePayout, punishDurationLabel,
  purgeChannel, randomQuote, rankBadge, rankHasRoom, rankLabel, rankWeight,
  rateLimitEmbed, rateLimits, rconHealthCheck, readBlacklist, readDonatorFile, readFactionFile,
  readPlayerBalance, reconcileBans, reconcileBlacklists, recordKnownPlayers, recordLastSeen, refreshLeaderboardChannels,
  refreshPlayerCache, releaseSingleInstanceLock, removeAutobanExempt, removeBans, removeDiscordLink, removeDonator,
  removeFactionRank, removeMenuGrant, removePlayerFromAllRankFiles, removePlayerFromRankFile, removeUserBlacklist, rouletteColor,
  safeRead, safeWrite, sanitizeBanName, sanitizeId, sanitizeMessage, saveCasinoConfig,
  saveFactionBackup, savePlaytime, saveRoles, saveUnbarred, saveUserBlacklist, saveVpnCheck,
  saveWages, scheduleBanRecheck, scheduleMenuRegrant, seedKnownPlayers, sendRcon, sendRconBoth,
  sendRconRaw, serverEmoji, serverLabel, serverOption, serverSnapshot, setAutoRotate,
  setAutopostMsgId, setDiscordLink, setFactionCap, setFactionRank, setFactionRankCap, setMenuLink,
  setMenuRole, setMute, setPlayerCacheFromData, shutdown, sourceBanFor, spawn,
  spinRoulette, spinSlotReel, spinSlots, splitPages, startIntervals, successEmbed,
  suspendDonator, syncAllModSave, syncModsaveBanlist, syncPlayerLedger, textify, textifyChunks,
  tickPlaytime, unbanEverywhere, ungagEverywhere, update, upsertPermBan, upsertTempBan,
  validateConfig, warningEmbed, wipeAllMoney, wipeFaction, wouldWipeBalance, writeDonatorFile,
  writeFactionAudit, writeFactionFile, writeGameFile, writeGameFileSingle, writeModLog, writePlayerBalance,
  } = ctx;

  async function onInteraction(interaction) {

  // No-embeds mode: render every embed payload to plain text at send time.
  // Registered before any collector, so component/modal interactions from
  // awaitMessageComponent / awaitModalSubmit flows are patched too.
  try { patchInteractionOutput(interaction); } catch {}

  /* ── Blacklist gate — barred users get nothing, on every interaction.
        Owners are immune and can never be blacklisted. ── */
  if (isBlacklisted(interaction.user.id) && !isOwner(interaction.user.id)) {
    if (interaction.isAutocomplete()) return interaction.respond([]).catch(() => {});
    if (interaction.isChatInputCommand()) {
      return interaction.reply({ embeds: [blacklistedEmbed()], flags: MessageFlags.Ephemeral }).catch(() => {});
    }
    return;
  }

  /* ── Panel buttons + modals ─────────────────────────── */
  // On failure, tell the user instead of leaving the modal stuck on "thinking…".
  const modalFail = (tag) => (e) => {
    logger.warn(tag, e.message);
    const payload = { embeds: [errorEmbed("Something Went Wrong", "That didn't go through — try again in a moment.")], flags: MessageFlags.Ephemeral };
    (interaction.deferred || interaction.replied ? interaction.followUp(payload) : interaction.reply(payload)).catch(() => {});
  };
  /* Link-request Accept/Deny — persistent (no collector), so it works after restarts.
     Only the approver role (or an owner) may act. */
  if (interaction.isButton() && interaction.customId.startsWith("linkreq_")) {
    const canAct = isOwner(interaction.user.id) || memberHasRoleId(interaction.member, LINK_APPROVER_ROLE);
    if (!canAct) return interaction.reply({ embeds: [deniedEmbed("Not Authorized", `Only <@&${LINK_APPROVER_ROLE}> can approve or deny link requests.`)], flags: MessageFlags.Ephemeral }).catch(() => {});
    const [tag, uid, encName] = interaction.customId.split(":");
    const pavlov = decodeURIComponent(encName ?? "");
    const approve = tag === "linkreq_ok";
    // Re-check the one-to-one rules at ACCEPT time — a second pending request for the
    // same name (or same user) may have been approved while this card sat here.
    if (approve) {
      const takenBy = discordIdForPavlov(pavlov);
      const already = loadDiscordLinks()[uid];
      if ((takenBy && takenBy !== uid) || already) {
        const stale = brand(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Link Request — Void")
          .setDescription(`${DIVIDER}\n${already ? `<@${uid}> is already linked to \`${already.name}\`.` : `\`${pavlov}\` was claimed by <@${takenBy}> while this request was pending.`}\nNothing was changed.`)
          .setTimestamp());
        return interaction.update(textify({ content: "", embeds: [stale], components: [] })).catch(() => {});
      }
    }
    try {
      if (approve) {
        await setDiscordLink(uid, pavlov, interaction.user.tag);
        writeModLog({ action: "link", targetUserId: uid, playerId: pavlov, by: interaction.user.tag });
      }
      const done = brand(new EmbedBuilder().setColor(approve ? NV.IRRAD_GREEN : NV.RUST_RED)
        .setTitle(approve ? "Link Request — Approved" : "Link Request — Denied")
        .setDescription(`${interaction.user} ${approve ? "approved" : "denied"} <@${uid}>'s request to link to \`${pavlov}\`.`)
        .setTimestamp());
      await interaction.update(textify({ content: "", embeds: [done], components: [] }));
      // DM the requester the outcome (best effort).
      try {
        const u = await client.users.fetch(uid);
        await u.send(textify({ embeds: [brand(new EmbedBuilder().setColor(approve ? NV.IRRAD_GREEN : NV.RUST_RED)
          .setTitle(approve ? "Your link request was approved" : "Your link request was denied")
          .setDescription(approve ? `Your Discord is now linked to \`${pavlov}\`.` : `Your request to link to \`${pavlov}\` was denied by staff.`))] }));
      } catch { /* DMs closed */ }
    } catch (e) {
      logger.warn("LinkReq", `accept/deny failed: ${e.message}`);
      interaction.reply({ embeds: [errorEmbed("Failed", "Couldn't process that request — try again.")], flags: MessageFlags.Ephemeral }).catch(() => {});
    }
    return;
  }

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
        interaction.reply({ content: "This form expired — run the command again.", flags: MessageFlags.Ephemeral }).catch(() => {});
      }
    }, 2500);
    return;
  }

  /* ── Autocomplete ─────────────────────────────────────── */
  if (interaction.isAutocomplete()) {
    const focused  = interaction.options.getFocused(true);
    const cmdName  = interaction.commandName;

    // /mute duration - quick suggestions (typed valid durations are honoured)
    if (focused.name === "duration" && cmdName === "mute") {
      const q = focused.value.trim().toLowerCase();
      const opts = ["30s", "5m", "10m", "30m", "1h", "2h", "12h", "1d", "3d", "7d"]
        .map(d => ({ name: d, value: d }));
      if (q && parseDuration(q) && !opts.find(o => o.value === q)) opts.unshift({ name: q, value: q });
      return interaction.respond(opts.filter(o => !q || o.value.startsWith(q)).slice(0, 25)).catch(() => {});
    }

    // /autorotate time - common Eastern times (typed valid times are honoured)
    if (focused.name === "time" && cmdName === "autorotate") {
      const q = focused.value.trim().toLowerCase();
      const opts = ["00:00", "03:00", "06:00", "09:00", "12:00", "15:00", "18:00", "21:00"]
        .map(t => ({ name: `${t} Eastern`, value: t }));
      if (q && parseClockTime(q)) opts.unshift({ name: `${q} Eastern`, value: q });
      return interaction.respond(opts.filter(o => !q || o.value.includes(q)).slice(0, 25)).catch(() => {});
    }

    // /tempban date - quick calendar suggestions (always future dates, YYYY-MM-DD)
    if (focused.name === "date" && cmdName === "tempban") {
      const q = focused.value.trim();
      const iso = (days) => new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10);
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

    if (focused.name === "rank" && cmdName === "faction") {
      const faction = interaction.options.getString("faction") ?? interaction.options.getString("to_faction");
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
  const PUBLIC         = ["help", "ping", "dashboard", "serverinfo", "find", "checkban", "banlist", "wagelist", "checkbalance", "stats", "kd", "link",
                           "slots", "coinflip", "blackjack", "roulette", "cockfight", "russianroulette", "jackpot"];
  const MOD_COMMANDS   = ["kick", "flush", "tempban", "unban", "mute", "unmute", "announce", "givecaps"];
  const FL_COMMANDS    = ["addwage", "removewage", "faction"];
  const ADMIN_COMMANDS = ["permban", "cleartempbans", "setroles", "givemenu", "stripmenu", "manual", "adjustcaps", "donator", "staffactivity", "casino"];

  const name = interaction.commandName;

  if (!PUBLIC.includes(name)) {
    if (ADMIN_COMMANDS.includes(name) && !hasAdminRole(interaction.member)) {
      return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
    }
    // /faction's read-only subcommands (list / audit / playtime) are public - only
    // the mutating ones need the Faction Leader / Mod gate.
    const factionPublicSub = name === "faction" &&
      ["list", "audit", "playtime"].includes(interaction.options.getSubcommand(false));
    if (FL_COMMANDS.includes(name) && !factionPublicSub && !hasModRole(interaction.member) && !hasFactionLeaderRole(interaction.member)) {
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
    switch (name) {

      /* ─────────────────────────────────────────────────────
         HELP
         ───────────────────────────────────────────────────── */
      case "help": {
        const { modRoleId, adminRoleId, factionLeaderRoleId } = loadRoles();
        const isAdmin = hasAdminRole(interaction.member);
        const isMod   = hasModRole(interaction.member);
        const isFLead = hasFactionLeaderRole(interaction.member);

        let badge, color;
        if (isAdmin)      { badge = "**ADMIN**";          color = NV.AMBER;     }
        else if (isMod)   { badge = "**MODERATOR**";      color = NV.NCR_TAN;   }
        else if (isFLead) { badge = "**FACTION LEADER**"; color = NV.GOLD;      }
        else              { badge = "**PUBLIC ACCESS**";  color = NV.BLUE_VATS; }

        const mStr = modRoleId           ? `<@&${modRoleId}>`           : "`not set`";
        const aStr = adminRoleId         ? `<@&${adminRoleId}>`         : "`not set`";
        const fStr = factionLeaderRoleId ? `<@&${factionLeaderRoleId}>` : "`not set`";

        const rankSummaryLines = ALL_FACTIONS.map(f => {
          const cfg = getFactionRankConfig(f);
          const rankStr = cfg ? cfg.order.map(r => `${cfg.badges[r]} ${r}`).join(" → ") : "*no ranks*";
          return `**${f}:** ${rankStr}`;
        }).join("\n");

        const embed = new EmbedBuilder().setColor(color)
          .setTitle("Command Roster")
          .setDescription(
            `> *War never changes — but the rules of the Strip, those we enforce.*\n\n` +
            `### ${GLYPH.rank}  Your Access\n${badge}\n` +
            `-# Mod ${mStr}  ${GLYPH.dot}  Admin ${aStr}  ${GLYPH.dot}  Faction ${fStr}\n` +
            `-# Autocomplete works in every Courier ID and Rank field.`
          )
          .addFields(
            { name: "Public",
              value: "`/help` `/ping` `/dashboard` `/serverinfo` `/find` `/checkban` `/banlist` `/stats` `/kd` `/checkbalance` `/wagelist` `/link add`\n`/faction list` `/faction audit` `/faction playtime`" },
            { name: "Moderator",
              value: [
                "`/kick <id> <server> [reason]` — Eject",
                "`/mute <id> <duration> [reason]` — In-game mute (re-applied every join until it expires)",
                "`/unmute <id>` — Lift a mute now",
                "`/flush <server>` — Randomly kick one online player (staff & donators immune)",
                "`/tempban <id> <duration> <server> <reason>` — Temporary exile",
                "`/unban <id> <server>` — Lift exile",
                "`/announce <msg> <server> <target>` — RCON Notify a player or All",
                "`/givecaps <id> <amount> [reason]` — Give caps to a courier",
                "`/faction transfer <id> <from> <to> [rank]` — Move player between factions",
              ].join("\n") },
            { name: "Faction Leader",
              value: [
                "`/faction add <id> <faction> [rank]` — Whitelist player (optional starting rank)",
                "`/faction remove <id> <faction>` — Remove from whitelist",
                "`/faction rank <id> <faction> <rank>` — Set member rank *(FL only)*",
                "`/faction list <faction>` — Roster with ranks (pages)",
                "`/faction audit <faction>` — Add/remove/rank change log (pages)",
                "`/addwage <id> <tier>` — Enrol in payroll or issue mercenary pay",
                "`/removewage <id>` — Remove from payroll",
              ].join("\n") },
            { name: "Admin",
              value: [
                "`/permban <id> <server> <reason>` — Permanent ban",
                "`/cleartempbans` `/setroles`",
                "`/staffactivity <staff>` — All mod actions by a staff member",
                "`/givemenu` `/stripmenu` `/adjustcaps`",
                "`/manual`",
                "`/donator add|remove|list <id>` — Manage the donator whitelist file",
                "`/stripmenuall` — *Owner only* — clear ALL menu access from everyone",
                "`/configure` — *Owner only* — hidden control panel (IP tracker management)",
                "`/setrconroles [high_staff] [staff] [faction]` — *Admin* — set which roles grant each RCON menu (self-service panel)",
                "`/link remove|list` — *Mod* — manage Discord ↔ Pavlov links (adds are public requests)",
                "`/autorotate set|off|status` — *Owner* — daily map rotation at a set Eastern time",
                "`/clearallbans` — *Owner only* — unban everyone (clears blacklist.txt)",
                "`/faction setcap <faction> <cap>` — Set faction size limit",
                "`/faction setrankcap <faction> <rank> <cap>` — Cap members per rank (0 = unlimited)",
              ].join("\n") },
            { name: "Faction Ranks (per faction)",
              value: rankSummaryLines },
            { name: "Automation",
              value: [
                "Temp bans auto-lifted every **60s**",
                "Leaderboards refreshed every **30s**",
                "Wages disbursed every **7 days**",
                "RCON health check every **5 min**",
                "Rank changes update both the rank registry and the rank-specific spawn files automatically",
                "`/kick` `/tempban` `/permban` accept an optional **discord_user** — the bot DMs them their punishment details",
                "Command blacklist is set via **`BLACKLIST_IDS`** in `.env` (restart to apply)",
              ].join("\n") },
          )
          .setFooter({ text: BOT_COPYRIGHT });
        brand(embed, { thumb: true });
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
      }

      /* ─────────────────────────────────────────────────────
         PING
         ───────────────────────────────────────────────────── */
      case "dashboard": {
        await interaction.deferReply();
        const until = Date.now() + 5 * 60 * 1000;   // live-refresh for 5 minutes, then freeze
        for (;;) {
          await interaction.editReply({ embeds: [buildDashboardEmbed(await dashboardSnapshots())], keepEmbeds: true });
          if (Date.now() >= until) break;
          await new Promise(r => setTimeout(r, DASHBOARD_INTERVAL_MS));
        }
        return;
      }

      case "ping": {
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        const start = Date.now();
        const pings = await Promise.allSettled(ACTIVE_SERVERS.map(s => sendRcon("RefreshList", s, 2000, 0)));
        const rtt   = Date.now() - start;
        const okBy  = ACTIVE_SERVERS.map((s, i) => pings[i].status === "fulfilled" && !!parseRcon(pings[i].value)?.Successful);
        const okCount = okBy.filter(Boolean).length;
        const color = okCount === ACTIVE_SERVERS.length ? NV.IRRAD_GREEN : okCount > 0 ? NV.AMBER : NV.RUST_RED;
        const headline = okCount === ACTIVE_SERVERS.length ? "All systems nominal — Securitron network active."
          : okCount > 0 ? "Partial connectivity — a server is unreachable."
          : "All servers unreachable — check RCON config.";
        const wsPing = Math.max(0, client.ws.ping);
        const nodes = ACTIVE_SERVERS.length + 1;      // servers + the bot gateway
        const online = 1 + okCount;
        const stat = (ok) => ok ? `${GLYPH.up} up` : `${GLYPH.down} down`;
        const lines = [
          "SYSTEM DIAGNOSTICS",
          "──────────────────────────",
          `${cell("gateway", 9)} ${GLYPH.up} up  ${wsPing}ms`,
          ...ACTIVE_SERVERS.map((s, i) => `${cell(`server ${i + 1}`, 9)} ${stat(okBy[i])}`),
          "──────────────────────────",
          `${cell("nodes", 9)} ${bar(online, nodes, 8)} ${online}/${nodes}`,
          `${cell("rtt", 9)} ${rtt}ms`,
          `${cell("uptime", 9)} ${formatUptime(Date.now() - BOT_START_MS)}`,
          `${cell("cached", 9)} ${ACTIVE_SERVERS.map(s => playerCache[s].length).join("+")} players`,
          `${cell("bans", 9)} ${loadBans().length} active`,
        ];
        const embed = new EmbedBuilder().setColor(color)
          .setTitle("System Status")
          .setDescription(`${hero(headline)}\n\`\`\`\n${lines.join("\n")}\n\`\`\``);
        brand(embed, { thumb: true });
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         SERVERINFO
         ───────────────────────────────────────────────────── */
      case "serverinfo": {
        const server = interaction.options.getString("server");
        await interaction.deferReply();
        const fetchInfo = async (srv) => {
          try {
            const [listRaw, infoRaw] = await Promise.all([
              sendRcon("RefreshList", srv, 3000, 1),
              sendRcon("ServerInfo",  srv, 3000, 1),
            ]);
            const listData = parseRcon(listRaw);
            const infoData = parseRcon(infoRaw);
            return {
              ok:         listData?.Successful ?? false,
              players:    listData?.PlayerList?.length ?? 0,
              mapLabel:   infoData?.MapLabel    ?? infoData?.ServerName ?? "*Unknown*",
              gameMode:   infoData?.GameMode    ?? "*Unknown*",
              serverName: infoData?.ServerName  ?? serverLabel(srv),
              maxPlayers: infoData?.MaxPlayers  ?? "?",
            };
          } catch { return { ok: false, players: 0, mapLabel: "*Unreachable*", gameMode: "*Unreachable*", serverName: serverLabel(srv), maxPlayers: "?" }; }
        };
        const servers = server === "both" ? ACTIVE_SERVERS : [server];
        const infos   = await Promise.all(servers.map(fetchInfo));
        const embeds  = infos.map((info, i) => {
          const srv = servers[i];
          const maxN = Number(info.maxPlayers) || info.players || 1;
          const lines = [
            `${cell("status", 8)} ${info.ok ? `${GLYPH.up} online` : `${GLYPH.down} offline`}`,
            `${cell("map", 8)} ${info.mapLabel}`,
            `${cell("mode", 8)} ${info.gameMode}`,
            `${cell("players", 8)} ${bar(info.players, maxN, 10)} ${info.players}/${info.maxPlayers}`,
          ];
          const e = new EmbedBuilder()
            .setColor(info.ok ? NV.IRRAD_GREEN : NV.RUST_RED)
            .setTitle(info.serverName)
            .setDescription(`\`\`\`\n${lines.join("\n")}\n\`\`\``);
          const roster = [...(playerCache[srv] || [])].sort((a, b) => a.localeCompare(b));
          if (info.ok && roster.length) {
            const shown = roster.slice(0, 15).map(n => `\`${n}\``).join("  ");
            e.addFields({ name: `Online (${roster.length})`, value: (shown + (roster.length > 15 ? `  *+${roster.length - 15} more*` : "")).slice(0, 1024), inline: false });
          }
          return brand(e, { footer: { text: `${serverLabel(srv)} · live data` } });
        });
        return interaction.editReply({ embeds });
      }

      /* ─────────────────────────────────────────────────────
         FIND
         ───────────────────────────────────────────────────── */
      case "find": {
        const query = interaction.options.getString("name").toLowerCase();
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        await Promise.all(ACTIVE_SERVERS.map(refreshPlayerCache));
        const _findMembership = buildFactionMembershipIndex();   // one read for faction tags
        const matches = [];
        const seen    = new Set();
        for (const srv of ACTIVE_SERVERS) {
          for (const name of playerCache[srv]) {
            if (!name.toLowerCase().includes(query)) continue;
            const key = name.toLowerCase();
            if (seen.has(key)) {
              const m = matches.find(x => x.name.toLowerCase() === key);
              if (m) m.servers.push(srv);
            } else {
              seen.add(key);
              matches.push({ name, servers: [srv] });
            }
          }
        }
        if (!matches.length) {
          return interaction.editReply({ embeds: [
            brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("No Matches Found")
              .setDescription(`${hero(`No couriers matching "${query}" are online.`)}\n*Try a shorter search term.*`))
          ]});
        }
        const lines = matches.map((m) => {
          const srvStr = m.servers.map(s => "S" + s.replace("server", "")).join("+");
          const facs = _findMembership?.get(m.name.toLowerCase());
          return `\`[${srvStr}]\`  **${m.name}**${facs?.length ? `  —  ${facs.join(" / ")}` : ""}`;
        });
        return interaction.editReply({ embeds: [
          brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`Search Results — "${query}"`)
            .setDescription(`${hero(`**${matches.length}** match${matches.length !== 1 ? "es" : ""} found.`)}\n${lines.join("\n")}`))
        ]});
      }

      /* ─────────────────────────────────────────────────────
         KICK  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "kick": {
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
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Courier Ejected from the Strip")
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
      }

      /* ─────────────────────────────────────────────────────
         MUTE — in-game gag for a set time, re-applied every join
         ───────────────────────────────────────────────────── */
      case "mute": {
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
        const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Courier Silenced")
          .setDescription(`${interaction.user} muted **${playerId}** for **${durStr}** (expires <t:${ts}:R>) — ${reason}`)
          .setFooter({ text: "Re-gagged every join until it expires — then unmuted on their next join" }).setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      case "unmute": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const had = getMute(playerId);
        await clearMute(playerId);
        // Only toggle if we believe they're actually gagged right now - Gag is a bare
        // toggle with no True/False, so calling it on someone who isn't muted would
        // incorrectly mute them instead of harmlessly no-op'ing like the old form did.
        if (had) ungagEverywhere(playerId);
        writeModLog({ action: "unmute", playerId, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Courier Unsilenced")
          .setDescription(had ? `${interaction.user} lifted the mute on \`${playerId}\` and ungagged them.` : `\`${playerId}\` had no active mute — nothing to lift.`)
          .setTimestamp());
        await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
      }

      /* ─────────────────────────────────────────────────────
         FLUSH — randomly kick one online player from a server
         ───────────────────────────────────────────────────── */
      case "flush": {
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
      }

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
      case "staffactivity": {
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
          const detail = e.reason ? ` — ${e.reason}` : e.amount ? ` — ${e.amount > 0 ? "+" : ""}${e.amount} caps` : e.faction ? ` — ${e.faction}` : "";
          const who    = e.playerId ? ` · ${e.playerId}` : "";
          return `\`${e.action}\`${who}${detail} · <t:${ts}:R>`;
        });
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.AMBER)
            .setTitle(`Staff Activity — ${staff.tag}`)
            .setDescription(`${matches.length} action${matches.length !== 1 ? "s" : ""} total *(newest first)*\n${summary}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Mod log" }).setTimestamp(),
          { perPage: 12, ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         TEMPBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "tempban": {
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
          .setTitle(permanent ? "Permanent Exile Issued" : "Courier Exiled from the Mojave")
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
      }

      /* ─────────────────────────────────────────────────────
         UNBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "unban": {
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
        const embed = clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Exile Lifted — Welcome Back to the Strip")
          .setDescription(`> *${randomQuote("unban")}*\n\n${interaction.user} pardoned **${playerId}**. ${removed ? "Temp ban record cleared." : "No temp ban record."} ${bl.removed ? `Removed from blacklist.txt on ${bl.removed} install(s).` : "Was not on blacklist.txt."} ${ipLifted}`));
        await logBan(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         BANLIST — all active bans
         ───────────────────────────────────────────────────── */
      case "banlist": {
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
      }

      /* ─────────────────────────────────────────────────────
         CHECKBAN
         ───────────────────────────────────────────────────── */
      case "checkban": {
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
            .setDescription(`${hero("This courier walks free.")}\n\`${playerId}\` has no active ban.`));
          if (_cbRec?.flagged) cleanE.addFields({ name: "Evasion Watch", value: "Matches an active IP/EOS flag — next join is auto-banned.", inline: false });
          if (isAutobanExempt(playerId)) cleanE.addFields({ name: "Unban Protection", value: "Explicitly unbanned — auto-bans will never re-catch this name.", inline: false });
          if (_cbCtx.length) cleanE.addFields(..._cbCtx);
          return interaction.reply({ embeds: [cleanE] });
        }
        return interaction.reply({ embeds: [
          clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Active")
            .setDescription(`**${playerId}** is on the blacklist — banned on ${hits.map(n => `**Server ${n}**`).join(" + ")}.`), "Blacklisted — use /unban to lift")
        ]});
      }

      /* ─────────────────────────────────────────────────────
         BANLIST
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         PERMBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "permban": {
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
      }

      /* ─────────────────────────────────────────────────────
         CLEARTEMPBANS
         ───────────────────────────────────────────────────── */
      case "cleartempbans": {
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
        // courier mid-clear before their record is removed by removeBans() below.
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
      }

      /* ─────────────────────────────────────────────────────
         CLEARALLBANS — owner only: Unban every banned player
         ───────────────────────────────────────────────────── */
      case "clearallbans": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        // gather every banned name: bot temp bans + blacklist.txt on both installs
        const names = [...new Set([...loadBans().map(b => b.playerId), ...blacklistAll()].map(s => String(s).trim()).filter(Boolean))];
        if (!names.length) {
          return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("No Exiles on Record").setDescription(`${hero("The wasteland is at peace.")}\nNothing to clear — no bans on record.`))] });
        }

        const preview = names.slice(0, 30).map(n => `- \`${n}\``).join("\n") + (names.length > 30 ? `\n...and ${names.length - 30} more` : "");
        const go = await confirmDialog(interaction, {
          title: "Unban EVERYONE?",
          body: `Removes **${names.length}** courier(s) from blacklist.txt on both servers and lifts their IP/username flags. This cannot be undone.\n\n${preview}`,
          confirmLabel: `Unban all ${names.length}`,
        });
        if (!go) return;
        // Exempt every name FIRST (like /unban) so enforceBansSweep can't re-ban a
        // courier mid-clear before their record is removed by removeBans() below.
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
      }

      /* ─────────────────────────────────────────────────────
         SETROLES
         ───────────────────────────────────────────────────── */
      case "setroles": {
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
      }

      /* ─────────────────────────────────────────────────────
         DONATOR  (admin — manage the donator whitelist file)
         ───────────────────────────────────────────────────── */
      case "donator": {
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
            .setDescription(`> *"A generous soul joins the ranks of the Strip's patrons."*\n\n${interaction.user} added **${playerId}** to the donator file.`)
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

        break;
      }

      /* ─────────────────────────────────────────────────────
         ANNOUNCE
         ───────────────────────────────────────────────────── */
      case "announce": {
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
      }

      /* ─────────────────────────────────────────────────────
         GIVEMENU / STRIPMENU  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "givemenu": {
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
      }

      case "stripmenu": {
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
      }

      /* ─────────────────────────────────────────────────────
         STRIPMENUALL — owner only: clear EVERYONE's menu access
         ───────────────────────────────────────────────────── */
      case "stripmenuall": {
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
          .setDescription(`${hero("Cleared menu access for every courier on both servers.")}\n\`ClearMenuAccess\` · \`ClearAccessManagers\` — **${holders.length}** grant(s) cleared.`)
          .setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         SETFACTIONADMIN — owner sets this guild's Faction Leader role

      /* ─────────────────────────────────────────────────────
         SETRCONROLES — which Discord role grants each RCON menu
         ───────────────────────────────────────────────────── */
      case "setrconroles": {
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
      }

      /* ─────────────────────────────────────────────────────
         CONFIGURE — owner-only hidden controls (blacklist, IP, factions)
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         LINK — owner: link a Discord account to a Pavlov username
         ───────────────────────────────────────────────────── */
      case "link": {
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
      }

      case "configure": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });

        const menu = new StringSelectMenuBuilder().setCustomId("cfg_menu").setPlaceholder("Select a hidden command…")
          .addOptions(
            { label: "Blacklist IP / username", value: "blacklist_ip", description: "Auto-ban anyone matching an IP or username" },
            { label: "View blacklist",          value: "view_blacklist", description: "Show all blacklisted IPs and usernames" },
            { label: "View alt accounts",       value: "view_alts",      description: "A courier's known alt accounts (shared IP)" },
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
            { label: "Wipe ALL money",          value: "wipe_money",    description: "Set every player's caps to 0 (irreversible)" },
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
          const labelByChoice = { ignore_add: "Username", ignore_remove: "Username", clear_ip: "IP address", blacklist_ip: "IP or username", view_alts: "Courier username", user_bl_add: "Discord user ID", user_bl_remove: "Discord user ID", wipe_money: "Type WIPE to confirm", load_factions: "Type LOAD to confirm" };
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
      }

      /* ─────────────────────────────────────────────────────
         FACTION — all subcommands
         ───────────────────────────────────────────────────── */
      case "faction": {
        const sub = interaction.options.getSubcommand();

        /* ── setcap (admin only) ── */
        if (sub === "setcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("faction");
          const cap     = interaction.options.getInteger("cap");
          await setFactionCap(faction, cap);
          writeModLog({ action: "faction-setcap", faction, cap, by: interaction.user.tag });
          const spawn   = SPAWN_FILE_MAP[faction];
          const current = spawn ? (readFactionFile(spawn)?.length ?? 0) : 0;
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("Faction Size Cap Updated")
            .setDescription(`${interaction.user} set **${faction}**'s cap to **${cap}** members (currently ${current}/${cap}${current > cap ? " — over cap!" : ""}).`)
            .setFooter({ text: "Cap enforced on /faction add" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        /* ── setrankcap (admin only) ── */
        if (sub === "setrankcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("faction");
          const rank    = interaction.options.getString("rank");
          const cap     = interaction.options.getInteger("cap");
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          await setFactionRankCap(faction, rank, cap);
          writeModLog({ action: "faction-setrankcap", faction, rank, cap, by: interaction.user.tag });
          const current = countFactionRank(faction, rank);
          const capStr  = cap > 0 ? `**${cap}**` : "**Unlimited**";
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("Rank Cap Updated")
            .setDescription(`${interaction.user} set **${faction}** ${rankBadge(faction, rank)}'s cap to ${capStr} (currently ${current}${cap > 0 ? `/${cap}${current > cap ? " — over cap!" : ""}` : ""}).`)
            .setFooter({ text: cap > 0 ? "Cap enforced on add / rank / transfer" : "Rank is now uncapped" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        /* ── wipe (owner only) — reset one faction's whitelist, or every faction ── */
        if (sub === "wipe") {
          if (!isOwner(interaction.user.id)) {
            return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const faction = interaction.options.getString("faction");
          const targets = faction ? [faction] : ALL_FACTIONS;
          const counts  = targets.map(f => ({ faction: f, count: (readFactionFile(SPAWN_FILE_MAP[f]) ?? []).length }));
          const total   = counts.reduce((s, c) => s + c.count, 0);
          if (!total) {
            return interaction.reply({ embeds: [successEmbed("Nothing to Wipe",
              faction ? `**${faction}** has no members.` : "No faction has any members.")], flags: MessageFlags.Ephemeral });
          }
          const preview = counts.filter(c => c.count).map(c => `- **${c.faction}**: ${c.count} member${c.count !== 1 ? "s" : ""}`).join("\n");
          const go = await confirmDialog(interaction, {
            title: faction ? `Wipe ${faction}'s whitelist?` : "Wipe ALL faction whitelists?",
            body: `Clears membership and every rank file${faction ? "" : ", for every faction"} — **${total}** courier${total !== 1 ? "s" : ""} total.\nA pre-wipe snapshot of each file is kept in \`${FACTION_BAK_DIR}\`.\n\n${preview}`,
            confirmLabel: faction ? `Wipe ${faction}` : "Wipe ALL",
          });
          if (!go) return;
          let wipedFactions = 0, wipedMembers = 0;
          for (const f of targets) {
            const r = await wipeFaction(f);
            if (!r.ok) continue;
            wipedFactions++; wipedMembers += r.count;
            writeFactionAudit({ action: "wipe", faction: f, count: r.count, by: interaction.user.tag });
          }
          writeModLog({ action: "faction-wipe", faction: faction || "ALL", count: wipedMembers, by: interaction.user.tag });
          const embed = successEmbed(
            faction ? `${faction} Whitelist Wiped` : "All Faction Whitelists Wiped",
            `Cleared **${wipedMembers}** courier${wipedMembers !== 1 ? "s" : ""} across **${wipedFactions}** faction${wipedFactions !== 1 ? "s" : ""}.`);
          await logAction(embed);
          return interaction.editReply({ embeds: [embed], components: [], keepEmbeds: true });
        }

        /* ── list (public, paginated) ── */
        if (sub === "list") {
          const faction  = interaction.options.getString("faction");
          const members  = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], flags: MessageFlags.Ephemeral });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} — Empty Roster`)
                .setDescription("No players are currently whitelisted for this faction.\n\nUse `/faction add` to enlist someone.")
                .setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          const cap = getFactionCap(faction);
          const summary = getFactionRankOrder(faction).slice().reverse().map(r => {
            const n = countFactionRank(faction, r);   // file-based: counts every holder (a member may hold several ranks)
            const rcap = getFactionRankCap(faction, r);
            if (!n && !rcap) return null;
            const count = rcap ? `${n}/${rcap}` : `${n}`;
            return `${getFactionRankBadge(faction, r)} ${r}: **${count}**`;
          }).filter(Boolean).join("  ·  ");
          const lines = members.map((m, i) =>
            `\`${String(i + 1).padStart(2, "0")}\`  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  ·  *${(m.ranks || [m.rank]).join(", ")}*`);
          const header = `**${members.length}/${cap}** members${members.length > cap ? " over cap" : ""}  ·  ${summary}`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`${faction} — Roster`)
              .setDescription(`${header}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
              .setFooter({ text: SPAWN_FILE_MAP[faction] }),
            { perPage: 20 });
        }

        /* ── playtime (public, paginated) — roster ranked by time served ── */
        if (sub === "playtime") {
          const faction = interaction.options.getString("faction");
          const members = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], flags: MessageFlags.Ephemeral });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} — Empty Roster`)
                .setDescription("No players are currently whitelisted for this faction.\n\nUse `/faction add` to enlist someone.")
                .setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          // Playtime keys are display-cased names — match members case-insensitively.
          const byName = new Map(Object.entries(loadPlaytime()).map(([n, m]) => [n.toLowerCase(), Number(m) || 0]));
          const ranked = members
            .map(m => ({ ...m, minutes: byName.get(m.playerId.toLowerCase()) ?? null }))
            .sort((a, b) => (b.minutes ?? -1) - (a.minutes ?? -1));
          const top   = ranked[0]?.minutes || 1;
          const total = ranked.reduce((s, m) => s + (m.minutes ?? 0), 0);
          const lines = ranked.map((m, i) => {
            const time  = m.minutes !== null ? formatPlaytime(m.minutes) : "*No record*";
            const meter = m.minutes !== null && i < 5 ? `  \`${bar(m.minutes, top, 8)}\`` : "";
            return `${rankLabel(i)}  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  ·  ${time}${meter}`;
          });
          const header = `**${members.length}** member${members.length !== 1 ? "s" : ""}  ·  **${formatPlaytime(total)}** combined`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`${faction} — Playtime`)
              .setDescription(`${header}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
              .setFooter({ text: "Playtime sampled every 60s while online" }),
            { perPage: 20 });
        }

        /* ── audit (public, paginated) ── */
        if (sub === "audit") {
          const faction   = interaction.options.getString("faction");
          const allAudit  = loadFactionAudit().filter(e => e.faction === faction).reverse();
          if (!allAudit.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`${faction} — Audit Log`)
                .setDescription("No faction changes recorded yet for this faction.")
                .setTimestamp()
            ], flags: MessageFlags.Ephemeral });
          }
          const ACTION_ICONS = { "add": "", "remove": "", "rank": "", "transfer-in": "", "transfer-out": "", "wipe": "" };
          const lines = allAudit.map(e => {
            const ts      = Math.floor(e.at / 1000);
            const icon    = ACTION_ICONS[e.action] ?? "";
            const subject = e.action === "wipe" ? `${e.count} member${e.count !== 1 ? "s" : ""}` : `**${e.playerId}**`;
            const detail  = e.rank ? ` → **${e.rank}**` : e.oldRank ? ` *(was ${e.oldRank})*` : "";
            return `${icon}  \`${e.action}\`  ${subject}${detail}  ·  by *${e.by}*  ·  <t:${ts}:R>`;
          });
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.AMBER)
              .setTitle(`${faction} — Audit Log`)
              .setDescription(`**${allAudit.length}** total changes *(newest first)*\n\n${DIVIDER}\n${pageLines.join("\n")}`),
            { perPage: 15, ephemeral: true });
        }

        /* ── rank (Faction Leader ONLY) ── */
        if (sub === "rank") {
          if (!hasFactionLeaderRole(interaction.member)) {
            return interaction.reply({ embeds: [factionLeaderStrictEmbed()], flags: MessageFlags.Ephemeral });
          }
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const rank     = interaction.options.getString("rank");
          const removing = interaction.options.getBoolean("remove") === true;
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          const spawn = SPAWN_FILE_MAP[faction];
          const lines = readFactionFile(spawn);
          if (!lines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**.`)], flags: MessageFlags.Ephemeral });
          if (!lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Not a Member", `\`${playerId}\` is not whitelisted in **${faction}**.\n\nUse \`/faction add\` first.`)], flags: MessageFlags.Ephemeral });
          }
          const had = getPlayerRanks(faction, playerId);
          if (removing) {
            if (!had.includes(rank)) {
              return interaction.reply({ embeds: [warningEmbed("Rank Not Held", `\`${playerId}\` doesn't hold **${rank}** in **${faction}**.\n\nThey hold: ${had.join(", ") || "none"}.`)], flags: MessageFlags.Ephemeral });
            }
            if (!removePlayerFromRankFile(faction, playerId, rank)) {
              return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not update the **${rank}** file for **${faction}** — check the server path/permissions. Nothing was changed.`)], flags: MessageFlags.Ephemeral });
            }
          } else {
            if (had.includes(rank)) {
              return interaction.reply({ embeds: [warningEmbed("Already Holds Rank", `\`${playerId}\` already holds **${rank}** in **${faction}**.\n\nThey hold: ${had.join(", ")}.`)], flags: MessageFlags.Ephemeral });
            }
            const room = rankHasRoom(faction, rank);   // a member can hold MULTIPLE ranks; cap is per rank file
            if (!room.ok) {
              return interaction.reply({ embeds: [errorEmbed("Rank Full",
                `**${rank}** in **${faction}** is at its cap (**${room.count}/${room.cap}**).\n\nRaise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
            }
            if (!addPlayerToRankFile(faction, playerId, rank)) {
              return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not update the **${rank}** file for **${faction}** — check the server path/permissions. Nothing was changed.`)], flags: MessageFlags.Ephemeral });
            }
          }
          const now = getPlayerRanks(faction, playerId);
          await setFactionRank(faction, playerId, now[now.length - 1] ?? getFactionDefaultRank(faction));   // track highest as primary
          writeFactionAudit({ action: "rank", faction, playerId, rank: removing ? `-${rank}` : rank, by: interaction.user.tag });
          writeModLog({ action: removing ? "faction-unrank" : "faction-rank", playerId, faction, rank, by: interaction.user.tag });
          const rankFile = getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a";
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(removing ? "Faction Rank Removed" : "Faction Rank Added")
            .setDescription(`> *${randomQuote("faction")}*\n\n${interaction.user} ${removing ? "removed" : "added"} **${playerId}**'s ${rankBadge(faction, rank)} rank in **${faction}** (${removing ? "removed from" : "added to"} \`${rankFile}\`). They now hold: ${now.length ? now.map(r => `**${r}**`).join(", ") : "*no ranks*"}.`)
            .setFooter({ text: "Members can hold multiple ranks · rank files updated on disk" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── transfer (Mod+) ── */
        if (sub === "transfer") {
          if (!hasModRole(interaction.member)) {
            return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
          }
          const playerId    = sanitizeId(interaction.options.getString("playerid"));
          const fromFaction = interaction.options.getString("from_faction");
          const toFaction   = interaction.options.getString("to_faction");
          const rawRank     = interaction.options.getString("rank");
          const newRank     = rawRank ?? getFactionDefaultRank(toFaction);
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          if (fromFaction === toFaction) {
            return interaction.reply({ embeds: [errorEmbed("Same Faction", "Source and destination factions must be different.")], flags: MessageFlags.Ephemeral });
          }
          const toValidRanks = getFactionRankOrder(toFaction);
          if (!toValidRanks.includes(newRank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${newRank}** is not a valid rank for **${toFaction}**.\n\nValid ranks: ${toValidRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          const fromSpawn = SPAWN_FILE_MAP[fromFaction];
          const fromLines = readFactionFile(fromSpawn);
          if (!fromLines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${fromFaction}**.`)], flags: MessageFlags.Ephemeral });
          if (!fromLines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Not a Member", `\`${playerId}\` is not whitelisted in **${fromFaction}**.`)], flags: MessageFlags.Ephemeral });
          }
          const toSpawn = SPAWN_FILE_MAP[toFaction];
          const toLines = readFactionFile(toSpawn);
          if (toLines === null) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${toFaction}**. Transfer aborted to protect the roster.`)], flags: MessageFlags.Ephemeral });
          const toCap   = getFactionCap(toFaction);
          if (toLines.length >= toCap) {
            return interaction.reply({ embeds: [errorEmbed("Faction Full", `**${toFaction}** is at capacity (**${toLines.length}/${toCap}** members).\n\nIncrease the cap with \`/faction setcap\` or remove a member first.`)], flags: MessageFlags.Ephemeral });
          }
          if (toLines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Already a Member", `\`${playerId}\` is already whitelisted in **${toFaction}**.`)], flags: MessageFlags.Ephemeral });
          }
          const toRoom = rankHasRoom(toFaction, newRank);
          if (!toRoom.ok) {
            return interaction.reply({ embeds: [errorEmbed("Rank Full",
              `**${newRank}** in **${toFaction}** is at its cap (**${toRoom.count}/${toRoom.cap}**).\n\nChoose a different rank, or raise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
          }
          const oldRank = getFactionRank(fromFaction, playerId);
          const updatedFrom = fromLines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
          if (!writeFactionFile(fromSpawn, updatedFrom)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${fromSpawn}\`. Check file permissions.`)], flags: MessageFlags.Ephemeral });
          }
          removePlayerFromAllRankFiles(fromFaction, playerId);
          await removeFactionRank(fromFaction, playerId);
          // Re-read the destination roster NOW: the await above yielded the event loop,
          // and a concurrent /faction add or whitelist-panel claim landing in between
          // would be silently clobbered if we wrote back the stale pre-await copy.
          const toNow = readFactionFile(toSpawn) ?? toLines;
          if (!toNow.some(l => l.toLowerCase() === playerId.toLowerCase())) toNow.push(playerId);
          if (!writeFactionFile(toSpawn, toNow)) {
            // Re-read fromSpawn fresh: the awaited removeFactionRank above yielded the
            // event loop, so a concurrent faction edit may have changed it. Restore the
            // player without clobbering that change (mirror of the toSpawn re-read).
            const fromBack = readFactionFile(fromSpawn) ?? updatedFrom;
            if (!fromBack.some(l => l.toLowerCase() === playerId.toLowerCase())) fromBack.push(playerId);
            writeFactionFile(fromSpawn, fromBack);
            addPlayerToRankFile(fromFaction, playerId, oldRank);
            await setFactionRank(fromFaction, playerId, oldRank);
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${toSpawn}\`. Transfer rolled back.`)], flags: MessageFlags.Ephemeral });
          }
          const rankFileOk = addPlayerToRankFile(toFaction, playerId, newRank);
          if (!rankFileOk) logger.warn("Faction", `Transfer: membership moved but rank file write failed for ${playerId} -> ${toFaction}/${newRank}`);
          await setFactionRank(toFaction, playerId, newRank);
          writeFactionAudit({ action: "transfer-out", faction: fromFaction, playerId, oldRank, by: interaction.user.tag });
          writeFactionAudit({ action: "transfer-in",  faction: toFaction,   playerId, rank: newRank, by: interaction.user.tag });
          writeModLog({ action: "faction-transfer", playerId, fromFaction, toFaction, oldRank, newRank, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Faction Transfer Complete")
            .setDescription(`> *${randomQuote("faction")}*\n\n${interaction.user} moved **${playerId}** from **${fromFaction}** (${rankBadge(fromFaction, oldRank)}) to **${toFaction}** (${rankBadge(toFaction, newRank)}) — ${toNow.length}/${toCap} in the new roster.\n${rankFileOk ? "Rank files updated on both ends." : `FAILED to write \`${getFactionRankConfig(toFaction)?.rankFiles[newRank] ?? "n/a"}\` — re-run /faction rank.`}`)
            .setFooter({ text: "Both faction files updated · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── add ── */
        if (sub === "add") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const rawRank  = interaction.options.getString("rank");
          const rank     = rawRank ?? getFactionDefaultRank(faction);
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Faction", `Faction \`${faction}\` has no configured spawn file.`)], flags: MessageFlags.Ephemeral });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], flags: MessageFlags.Ephemeral });
          }
          // One faction per courier - block if they're already in a different faction.
          const otherFactions = (getPlayerFactions(playerId) || []).filter(f => f !== faction);
          if (otherFactions.length) {
            return interaction.reply({ embeds: [errorEmbed("Already in a Faction",
              `\`${playerId}\` already belongs to **${otherFactions.join(", ")}**. A courier can only be in one faction.\n\nUse \`/faction transfer\` to move them, or \`/faction remove\` first.`)], flags: MessageFlags.Ephemeral });
          }
          const lines = readFactionFile(spawn);
          if (lines === null) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Add aborted to protect the roster.`)], flags: MessageFlags.Ephemeral });
          if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Already Whitelisted", `\`${playerId}\` is already in **${faction}** (ranks: ${(getPlayerRanks(faction, playerId).join(", ") || "none")}).\n\nUse \`/faction rank\` to add or remove ranks — a member can hold several.`)], flags: MessageFlags.Ephemeral });
          }
          const cap = getFactionCap(faction);
          if (lines.length >= cap) {
            return interaction.reply({ embeds: [errorEmbed("Faction Full", `**${faction}** is at capacity (**${lines.length}/${cap}** members).\n\nUse \`/faction setcap\` to increase the limit, or remove a member first.`)], flags: MessageFlags.Ephemeral });
          }
          const addRoom = rankHasRoom(faction, rank);
          if (!addRoom.ok) {
            return interaction.reply({ embeds: [errorEmbed("Rank Full",
              `**${rank}** in **${faction}** is at its cap (**${addRoom.count}/${addRoom.cap}**).\n\nAdd them at a different rank, or raise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
          }
          lines.push(playerId);
          if (!writeFactionFile(spawn, lines)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to \`${spawn}\`. Check file permissions.`)], flags: MessageFlags.Ephemeral });
          }
          if (!addPlayerToRankFile(faction, playerId, rank)) {
            writeFactionFile(spawn, lines.filter(l => l.toLowerCase() !== playerId.toLowerCase()));
            return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not write to rank file for **${rank}**. Check file permissions.`)], flags: MessageFlags.Ephemeral });
          }
          await setFactionRank(faction, playerId, rank);
          writeFactionAudit({ action: "add", faction, playerId, rank, by: interaction.user.tag });
          writeModLog({ action: "faction-add", playerId, faction, rank, by: interaction.user.tag });
          const rankFile = getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a";
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(`Added to ${faction}`)
            .setDescription(`> *${randomQuote("faction")}*\n\n${interaction.user} added **${playerId}** to **${faction}** as ${rankBadge(faction, rank)} — ${lines.length}/${cap} in the roster.`)
            .setFooter({ text: "Main spawn file + rank file updated · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove ── */
        if (sub === "remove") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Faction", `Faction \`${faction}\` has no configured spawn file.`)], flags: MessageFlags.Ephemeral });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
          const lines = readFactionFile(spawn);
          if (!lines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read \`${spawn}\`.`)], flags: MessageFlags.Ephemeral });
          const idx = lines.findIndex(l => l.toLowerCase() === playerId.toLowerCase());
          if (idx === -1) {
            return interaction.reply({ embeds: [warningEmbed("Not Whitelisted", `\`${playerId}\` is not in the **${faction}** spawn list.`)], flags: MessageFlags.Ephemeral });
          }
          const oldRank = getFactionRank(faction, playerId);
          lines.splice(idx, 1);
          if (!writeFactionFile(spawn, lines)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to \`${spawn}\`.`)], flags: MessageFlags.Ephemeral });
          }
          removePlayerFromAllRankFiles(faction, playerId);
          await removeFactionRank(faction, playerId);
          writeFactionAudit({ action: "remove", faction, playerId, oldRank, by: interaction.user.tag });
          writeModLog({ action: "faction-remove", playerId, faction, oldRank, by: interaction.user.tag });
          const cap = getFactionCap(faction);
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle(`Removed from ${faction}`)
            .setDescription(`${interaction.user} removed **${playerId}** from **${faction}** (was ${rankBadge(faction, oldRank)}) — ${lines.length}/${cap} in the roster.`)
            .setFooter({ text: "Removed from spawn file and all rank files · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        break;
      }

      /* ─────────────────────────────────────────────────────
         MANUAL
         ───────────────────────────────────────────────────── */
      case "manual": {
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
      }

      /* ─────────────────────────────────────────────────────
         AUTOROTATE — owner: schedule a daily map rotation (Eastern)
         ───────────────────────────────────────────────────── */
      case "autorotate": {
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
      }

      /* ─────────────────────────────────────────────────────
         ADDWAGE
         ───────────────────────────────────────────────────── */
      case "addwage": {
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
          if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Ledger Write Failed", `Could not deposit **${tier.amount} caps** to \`${playerId}\`. Check \`MODSAVE_PATH\`.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "givecaps", playerId, amount: tier.amount, reason: "Mercenary payment", by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Mercenary Payment Issued")
            .setDescription(`> *"Caps now. No strings attached."*\n\n${interaction.user} paid **${playerId}** **${tier.amount.toLocaleString()} caps** — new balance **${newBal.toLocaleString()} caps**.`)
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
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Courier Added to Payroll")
          .setDescription(`> *"A fair day's work for a fair day's pay."*\n\n${interaction.user} enrolled **${playerId}** as **${tier.label}** (+${tier.amount} caps/week). Balance: ${bal !== null ? `${bal.toLocaleString()} caps` : "*no ledger*"}. First payout within 7 days.`)
          .setFooter({ text: randomQuote("wages") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         REMOVEWAGE
         ───────────────────────────────────────────────────── */
      case "removewage": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const wages   = loadWages();
        const removed = wages.find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        if (!removed) return interaction.reply({ embeds: [warningEmbed("Not on Payroll", `\`${playerId}\` isn't enrolled.\nUse \`/wagelist\` to see who's on the books.`)], flags: MessageFlags.Ephemeral });
        saveWages(wages.filter(w => w.playerId.toLowerCase() !== playerId.toLowerCase()));
        const tier = WAGE_TIERS[removed.tier];
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Removed from Payroll").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Courier",value: `\`${playerId}\``,                                          inline: true },
            { name: "Was",    value: `${tier?.label ?? removed.tier} (+${tier?.amount ?? "?"}/wk)`, inline: true },
            { name: "By",    value: `${interaction.user}`,                                       inline: true },
            { name: "Note",  value: "Existing balance unchanged. No further weekly payouts.",    inline: false },
          ).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         WAGELIST
         ───────────────────────────────────────────────────── */
      case "wagelist": {
        const wages = loadWages().filter(w => WAGE_TIERS[w.tier]?.weekly);
        if (!wages.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Payroll — Empty").setDescription('> *"No couriers on the books yet."*\n\nUse `/addwage` to enrol someone.').setTimestamp()], flags: MessageFlags.Ephemeral });
        const totalPay = wages.reduce((s, w) => s + (WAGE_TIERS[w.tier]?.amount ?? 0), 0);
        const tierSummary = Object.entries(WAGE_TIERS).filter(([, t]) => t.weekly)
          .map(([k, t]) => { const n = wages.filter(w => w.tier === k).length; return n ? `${t.label}: **${n}**` : null; }).filter(Boolean).join("  ·  ");
        const lines = wages.map((w, i) => {
          const tier  = WAGE_TIERS[w.tier] ?? { label: w.tier, amount: "?" };
          const bal   = readPlayerBalance(w.playerId);
          const next  = w.lastPaidAt ? Math.floor((w.lastPaidAt + WAGE_INTERVAL_MS) / 1000) : null;
          return `\`${String(i + 1).padStart(2, "0")}\`  **${w.playerId}**  ·  ${tier.label} *(+${tier.amount}/wk)*  ·  ${bal !== null ? `${bal.toLocaleString()} caps` : "*no ledger*"}${next ? `  ·  next <t:${next}:R>` : ""}`;
        });
        const header = `> *"The House always pays its debts."*\n\n${DIVIDER}\n**${wages.length}** enrolled  ·  ${tierSummary}  ·  **${totalPay.toLocaleString()} caps/week total**`;
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.GOLD).setTitle("Weekly Payroll — The House's Ledger")
            .setDescription(`${header}\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Wages disbursed automatically every 7 days" }).setTimestamp(),
          { perPage: 12, ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         CHECKBALANCE
         ───────────────────────────────────────────────────── */
      case "checkbalance": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const fp = getPlayerFilePath(playerId);
        if (!fp) return interaction.reply({ embeds: [errorEmbed("Vault Offline", "`MODSAVE_PATH` not set in `.env`.")], flags: MessageFlags.Ephemeral });
        if (!fs.existsSync(fp)) return interaction.reply({ embeds: [warningEmbed("No Ledger Found", `\`${playerId}\` has no ledger yet.\nThey must join the server first, or be assigned a wage with \`/addwage\`.`)], flags: MessageFlags.Ephemeral });
        const balance = readPlayerBalance(playerId);
        if (balance === null) return interaction.reply({ embeds: [errorEmbed("Ledger Corrupted", `Could not parse ledger for \`${playerId}\`.\nPath: \`${fp}\``)], flags: MessageFlags.Ephemeral });
        const wage   = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier  = wage ? (WAGE_TIERS[wage.tier] ?? { label: wage.tier, amount: "?", weekly: true }) : null;
        const nextTs = wage?.lastPaidAt ? Math.floor((wage.lastPaidAt + WAGE_INTERVAL_MS) / 1000) : null;
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Courier Ledger")
          .setDescription(`**${playerId}** has **${balance.toLocaleString()} caps**. ${wTier ? `Payroll: ${wTier.label} (+${wTier.amount}/wk)${nextTs ? `, next <t:${nextTs}:R>` : ""}.` : "Not enrolled in payroll."}`)
          .setFooter({ text: randomQuote("caps") }).setTimestamp();
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
      }

      /* ─────────────────────────────────────────────────────
         GIVECAPS
         ───────────────────────────────────────────────────── */
      case "givecaps": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const amount   = interaction.options.getInteger("amount");
        const reason   = interaction.options.getString("reason") ?? "Cap gift";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const current = readPlayerBalance(playerId) ?? 0;
        const newBal  = current + amount;
        if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Ledger Write Failed", "Check `MODSAVE_PATH`.")], flags: MessageFlags.Ephemeral });
        writeModLog({ action: "givecaps", playerId, amount, reason, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Caps Given")
          .setDescription(`${interaction.user} gave **${playerId}** **+${amount.toLocaleString()} caps** — new balance **${newBal.toLocaleString()} caps**. ${reason}`)
          .setFooter({ text: randomQuote("caps") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         TRANSFERCAPS
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         ADJUSTCAPS
         ───────────────────────────────────────────────────── */
      case "adjustcaps": {
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
          .setTitle(`Caps ${pos ? "Credited" : "Debited"}`)
          .setDescription(`${interaction.user} ${pos ? "credited" : "debited"} **${playerId}** **${pos ? "+" : ""}${amount.toLocaleString()} caps** — new balance **${newBal.toLocaleString()} caps**. ${reason}`)
          .setFooter({ text: "Manual cap adjustment · logged" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         STATS
         ───────────────────────────────────────────────────── */
      /* ─────────────────────────────────────────────────────
         CASINO — slots, coinflip, blackjack, roulette,
         cockfight, russian roulette, admin config
         ───────────────────────────────────────────────────── */
      case "slots": {
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
          resultValue: payout ? `**+${payout.toLocaleString()} caps** (${mult}x)` : "**Lost the wager**",
        });
        return interaction.reply({ embeds: [embed] });
      }

      case "coinflip": {
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
          resultValue: win ? `**+${payout.toLocaleString()} caps**` : "**Lost the wager**",
        });
        return interaction.reply({ embeds: [embed] });
      }

      case "blackjack": {
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
          return brand(new EmbedBuilder().setColor(NV.GOLD).setTitle(`${GAME_ICON.blackjack}  ${title}`).setDescription(DIVIDER)
            .addFields(
              { name: "Your Hand",   value: `${formatHand(player)}\nValue: **${playerValue}**`,                     inline: false },
              { name: "Dealer Hand", value: `${formatHand(dealer, reveal ? Infinity : 1)}\nValue: **${dealerValue}**`, inline: false },
              { name: "Wager",       value: `**${bet.toLocaleString()} caps**`,                                     inline: true },
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
            const btn = await awaitOwnedComponent(msg, interaction.user.id, Date.now() + 60_000, "This isn't your hand — you can't play someone else's blackjack.");
            if (!btn) break;   // idle timeout -> auto-stand on whatever hand stands now
            if (btn.customId === "bj_hit") {
              player.push(draw());
              if (handValue(player).total > 21) { bust = true; await btn.deferUpdate(); break; }
              await btn.update({ embeds: [renderEmbed("Blackjack", "Hit, Stand, or Double Down", false)], components: [renderRow(true)] });
              continue;
            }
            if (btn.customId === "bj_double") {
              const d = await debitCaps(playerId, bet);
              if (!d.ok) { await btn.update({ embeds: [renderEmbed("Blackjack", "Not enough caps to double — Hit or Stand.", false)], components: [renderRow(true)] }); continue; }
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
        const resultLine = outcome === "push" ? "**Bet refunded**" : payout ? `**+${(payout - bet).toLocaleString()} caps**` : "**Lost the wager**";
        const finalEmbed = renderEmbed(title, randomQuote("casino"), true);
        finalEmbed.addFields({ name: "Result", value: resultLine, inline: true }, { name: "Balance", value: `**${newBalance.toLocaleString()} caps**`, inline: true });
        if (pNatural || dNatural) return interaction.reply({ embeds: [finalEmbed] });
        return interaction.editReply({ embeds: [finalEmbed], components: [] });
      }

      case "roulette": {
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
          resultValue: result.win ? `**+${payout.toLocaleString()} caps** (${result.mult}x)` : "**Lost the wager**",
        });
        return interaction.reply({ embeds: [embed] });
      }

      case "cockfight": {
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
            resultValue: win ? `**+${payout.toLocaleString()} caps**` : "**Lost the wager**",
          });
          return interaction.reply({ embeds: [embed] });
        }

        const oppName = loadDiscordLinks()[opponent.id]?.name;
        if (!oppName) {
          return interaction.reply({ embeds: [errorEmbed("Opponent Not Linked", `${opponent} hasn't linked a Pavlov account with \`/link add\`.`)], flags: MessageFlags.Ephemeral });
        }
        const oppBalance = readPlayerBalance(oppName) ?? 0;
        if (oppBalance < bet) {
          return interaction.reply({ embeds: [errorEmbed("Opponent Can't Cover It", `${opponent} doesn't have **${bet.toLocaleString()}** caps.`)], flags: MessageFlags.Ephemeral });
        }

        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("cf_accept").setLabel("Accept").setStyle(ButtonStyle.Success),
          new ButtonBuilder().setCustomId("cf_decline").setLabel("Decline").setStyle(ButtonStyle.Secondary),
        );
        await interaction.reply({ embeds: [brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`${GAME_ICON.cockfight}  Cockfight Challenge`)
          .setDescription(`${DIVIDER}\n${interaction.user} challenges ${opponent} to a cockfight for **${bet.toLocaleString()} caps** each.\n${DIVIDER}\n-# Expires in 60s.`))], components: [row] });
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
          return interaction.editReply({ embeds: [errorEmbed("Fight Cancelled", "One of you no longer has enough caps.")], components: [] });
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
          .setDescription(`${DIVIDER}\n🐓 ${winnerMention}'s bird wins the pit!\n${DIVIDER}`)
          .addFields(
            { name: "Prize Pool",       value: `**${(bet * 2).toLocaleString()} caps**`, inline: true },
            { name: "House Cut → 🎉 Jackpot", value: `**${rake.toLocaleString()} caps**`,      inline: true },
            { name: "Winner Takes",     value: `**${prize.toLocaleString()} caps**`,     inline: true },
          ).setFooter({ text: randomQuote("casino") }));
        return interaction.editReply({ embeds: [embed], components: [] });
      }

      case "russianroulette": {
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
          .setDescription(`${DIVIDER}\n🔫 1-in-6 chance each pull. Surviving all six pulls cashes out automatically.\n${DIVIDER}`)
          .addFields(
            { name: "Wager",              value: `**${bet.toLocaleString()} caps**`, inline: true },
            { name: "Pulls Survived",     value: `**${pull}** / ${RUSSIAN_ROULETTE_MULTS.length}`, inline: true },
            { name: "Current Multiplier", value: pull ? `**${RUSSIAN_ROULETTE_MULTS[pull - 1]}x**` : "—", inline: true },
          ).setFooter({ text: footer }));

        await interaction.reply({ embeds: [renderEmbed("Russian Roulette", "Pull the trigger, or cash out")], components: [renderRow(true)] });
        const msg = await interaction.fetchReply();

        let died = false;
        for (;;) {
          const btn = await awaitOwnedComponent(msg, interaction.user.id, Date.now() + 60_000, "This isn't your revolver — you can't pull someone else's trigger.");
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
        const resultLine = died ? "**Lost the wager**" : payout > bet ? `**+${(payout - bet).toLocaleString()} caps**` : "**Bet refunded**";
        const finalEmbed = renderEmbed(title, randomQuote("casino"));
        finalEmbed.addFields({ name: "Result", value: resultLine, inline: true }, { name: "Balance", value: `**${newBalance.toLocaleString()} caps**`, inline: true });
        return interaction.editReply({ embeds: [finalEmbed], components: [] });
      }

      case "jackpot": {
        const cfg = loadCasinoConfig();
        if (!cfg.enabled) {
          return interaction.reply({ embeds: [warningEmbed("The House Is Closed", "Gambling is currently disabled.")], flags: MessageFlags.Ephemeral });
        }
        const playerId = loadDiscordLinks()[interaction.user.id]?.name;
        if (!playerId) {
          return interaction.reply({ embeds: [warningEmbed("Not Linked", "Link your Discord to your Pavlov username first — use `/link add`.")], flags: MessageFlags.Ephemeral });
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
          return interaction.reply({ embeds: [errorEmbed("Not Enough Caps", `You need at least **${JACKPOT_MIN_BALANCE.toLocaleString()}** caps to shoot for the jackpot. You have **${balance.toLocaleString()}**.`)], flags: MessageFlags.Ephemeral });
        }
        const pot = currentPot();
        if (pot <= 0) {
          return interaction.reply({ embeds: [warningEmbed("Jackpot Is Empty", "There's nothing in the pot right now — check back after a few more losses across the casino.")], flags: MessageFlags.Ephemeral });
        }

        const go = await confirmDialog(interaction, {
          title: "Bet your ENTIRE bank on the jackpot?",
          body: `You have **${balance.toLocaleString()}** caps. Win, and you take the **${pot.toLocaleString()}**-cap pot plus your bet back. Lose, and your entire balance goes into the pot for the next challenger.\n\nWin chance: **${Math.round(JACKPOT_WIN_CHANCE * 100)}%**`,
          confirmLabel: "Bet it all",
        });
        if (!go) return;

        const debit = await debitCaps(playerId, balance);
        if (!debit.ok) {
          return interaction.editReply({ embeds: [errorEmbed("Balance Changed", "Your balance changed before this went through — nothing was wagered.")], components: [] });
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
            .setDescription(`${DIVIDER}\n${interaction.user} just won the entire pot!\n${DIVIDER}`)
            .addFields(
              { name: "Wagered",     value: `**${wager.toLocaleString()} caps**`,     inline: true },
              { name: "Pot Won",     value: `**${won.toLocaleString()} caps**`,       inline: true },
              { name: "New Balance", value: `**${newBalance.toLocaleString()} caps**`, inline: true },
            ).setFooter({ text: randomQuote("casino") }));
          await logAction(embed);
          return interaction.editReply({ embeds: [embed], components: [] });
        }

        await addToPot(wager);
        const newBalance = readPlayerBalance(playerId) ?? 0;
        const newPot = currentPot();
        writeModLog({ action: "jackpot-lose", playerId, wager, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle(`${GAME_ICON.jackpot}  Busted`)
          .setDescription(`${DIVIDER}\nThe house takes it all. Your **${wager.toLocaleString()}** caps are added to the pot.\n${DIVIDER}`)
          .addFields(
            { name: "Lost",    value: `**${wager.toLocaleString()} caps**`, inline: true },
            { name: "New Pot", value: `**${newPot.toLocaleString()} caps**`, inline: true },
            { name: "Balance", value: `**${newBalance.toLocaleString()} caps**`, inline: true },
          ).setFooter({ text: randomQuote("casino") }));
        return interaction.editReply({ embeds: [embed], components: [] });
      }

      case "casino": {
        const sub = interaction.options.getSubcommand();
        const cfg = loadCasinoConfig();
        if (sub === "status") {
          const embed = brand(new EmbedBuilder().setColor(cfg.enabled ? NV.IRRAD_GREEN : NV.DEAD_GREY).setTitle("🎰  Casino Config")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Status",     value: cfg.enabled ? "**Open**" : "**Closed**",              inline: true },
              { name: "Min Bet",    value: `**${cfg.minBet.toLocaleString()}** caps`,             inline: true },
              { name: "Max Bet",    value: `**${cfg.maxBet.toLocaleString()}** caps`,             inline: true },
              { name: "Cooldown",   value: `**${(cfg.cooldownMs / 1000).toFixed(1)}s** between gambles`, inline: true },
              { name: "Gamble Cap", value: `**${GAMBLE_QUOTA_MAX}** per **${GAMBLE_QUOTA_WINDOW_MS / 3_600_000}h**`, inline: true },
              { name: "🎉 Jackpot Pot", value: `**${currentPot().toLocaleString()}** caps`,       inline: true },
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
          const embed = successEmbed("Casino Updated", `Bet range set to **${min.toLocaleString()}**–**${max.toLocaleString()}** caps.`);
          await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }
        break;
      }

      case "kd": {
        const raw = interaction.options.getString("playerid");
        if (raw && raw.trim()) {
          const playerId = sanitizeId(raw);
          let k; try { k = ipBans.getKD(playerId); } catch { k = { name: playerId, kills: 0, deaths: 0 }; }
          const ratio = (k.deaths ? k.kills / k.deaths : k.kills).toFixed(2);
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.AMBER).setTitle(`K/D — ${playerId}`)
              .setDescription(`${DIVIDER}`)
              .addFields(
                { name: "Kills",  value: `**${k.kills}**`,  inline: true },
                { name: "Deaths", value: `**${k.deaths}**`, inline: true },
                { name: "Ratio",  value: `**${ratio}**`,    inline: true },
                { name: "Playtime", value: (() => { const pt = loadPlaytime(); const key = Object.keys(pt).find(x => x.toLowerCase() === playerId.toLowerCase()); return key !== undefined ? formatPlaytime(pt[key]) : "*no record*"; })(), inline: true },
                { name: "Faction",  value: (() => { const f = getPlayerFactions(playerId); return f && f.length ? f.join(", ") : "*none*"; })(), inline: true },
              ).setFooter({ text: "Tracked from live kill logs while the bot is running" }).setTimestamp()
          ]});
        }
        // no player -> leaderboard
        let top = []; try { top = ipBans.topKD(100); } catch {}
        if (!top.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("K/D Leaderboard").setDescription("No kill data tracked yet.").setTimestamp()] });
        await interaction.deferReply();
        const lines = top.map((e, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${e.name}**  ·  ${e.kills}/${e.deaths}  ·  **${e.ratio.toFixed(2)}** K/D`);
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.AMBER).setTitle("K/D Leaderboard")
            .setDescription(`> *"Only the deadliest walk the Strip."*\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Sorted by K/D ratio" }), { perPage: 20 });
      }

      /* ─────────────────────────────────────────────────────
         INSPECT — owner-only deep dossier (IPs, VPN detection,
         alts, EOS id, enforcement flags). Ephemeral: sensitive.
         ───────────────────────────────────────────────────── */
      case "inspect": {
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
      }

      /* ─────────────────────────────────────────────────────
         FIREWALL — owner-only manual ufw block/unblock of an IP,
         independent of any ban. Needs UFW_BLOCK=1 (root/sudo ufw).
         ───────────────────────────────────────────────────── */
      case "firewall": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const sub = interaction.options.getSubcommand();
        const ip  = String(interaction.options.getString("ip") ?? "").trim();
        if (!_IPV4_RE.test(ip)) return interaction.reply({ embeds: [warningEmbed("Invalid IP", `\`${ip || "(empty)"}\` is not a valid IPv4 address.`)], flags: MessageFlags.Ephemeral });
        if (!UFW_BLOCK) return interaction.reply({ embeds: [warningEmbed("Firewall Disabled", "OS firewall blocking is off. Set **UFW_BLOCK=1** (and run the bot as root, or give it passwordless `sudo ufw`) to enable it.")], flags: MessageFlags.Ephemeral });
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
      }

      case "stats": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const playtime = loadPlaytime();
        const ptKey    = Object.keys(playtime).find(k => k.toLowerCase() === playerId.toLowerCase());
        const minutes  = ptKey !== undefined ? playtime[ptKey] : null;
        const factions = getPlayerFactions(playerId);
        const onS1     = playerCache.server1.some(n => n.toLowerCase() === playerId.toLowerCase());
        const onS2     = playerCache.server2.some(n => n.toLowerCase() === playerId.toLowerCase());
        const onS3     = playerCache.server3.some(n => n.toLowerCase() === playerId.toLowerCase());
        const online   = onS1 || onS2 || onS3;
        const balance  = readPlayerBalance(playerId);
        const wage     = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier    = wage ? WAGE_TIERS[wage.tier] : null;
        const tb       = loadBans().find(b => String(b.playerId).toLowerCase() === playerId.toLowerCase());
        const history  = getPlayerHistory(playerId);
        const lastSeen = getLastSeen(playerId);
        const donator  = isDonator(playerId);

        const fStr = factions === null ? "Folder unreadable"
          : !factions.length ? "*No faction access*"
          : factions.map(f => {
              const rank = getFactionRank(f, playerId);
              return `${getFactionRankBadge(f, rank)}  **${f}** *(${rank})*`;
            }).join("\n");

        const statusStr = !online ? "Offline" : [onS1 && "Server 1", onS2 && "Server 2", onS3 && "Server 3"].filter(Boolean).join("  +  ");
        const color = tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;
        const muteRec  = getMute(playerId);
        const muteStr  = muteRec && (!muteRec.expires || muteRec.expires > Date.now())
          ? `Muted — lifts <t:${Math.floor(muteRec.expires / 1000)}:R>` : null;
        const linkedId = discordIdForPavlov(playerId);
        let ipRec = null; try { ipRec = ipBans.getRecord(playerId); } catch {}

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`Courier Dossier — ${playerId}`)
          .setDescription(
            tb ? hero(tb.permanent || !tb.expires ? "Permanently exiled from the Mojave." : `Currently serving exile — ${formatTimeLeft(tb.expires)} remaining.`) :
            online ? hero("Currently active on the Strip.") :
            hero("Offline — last tracked playtime shown.")
          )
          .addFields(
            { name: "Status",        value: statusStr,                                                          inline: true },
            { name: "Playtime",      value: minutes !== null ? `**${formatPlaytime(minutes)}**` : "*No record*", inline: true },
            { name: "Last Seen",     value: online ? "Online now" : lastSeen ? `<t:${Math.floor(lastSeen / 1000)}:R>` : "*No record*", inline: true },
            { name: "Donator",       value: donator ? "Yes" : "No",                                       inline: true },
            { name: "K / D",         value: formatKD(playerId),                                                 inline: true },
            { name: "Sessions",      value: String(ipRec?.logins ?? 0),                                          inline: true },
            { name: "Discord",       value: linkedId ? `<@${linkedId}>` : "*not linked*",                        inline: true },
            { name: "Faction Ranks", value: fStr,                                                               inline: false },
          );

        if (balance !== null) {
          embed.addFields({ name: "Balance", value: `**${balance.toLocaleString()} caps**${wTier ? `  ·  Payroll: ${wTier.label} (+${wTier.amount}/wk)` : "  ·  Not on payroll"}`, inline: false });
        }
        if (tb) {
          embed.addFields({ name: "Active Exile", value: tb.permanent || !tb.expires
            ? `Permanent ban — *${tb.reason}*`
            : `Temp ban — *${tb.reason}*  ·  expires <t:${Math.floor(tb.expires / 1000)}:R>`, inline: false });
        }
        if (history.length) {
          embed.addFields({ name: "Mod Actions", value: `**${history.length}** on record`, inline: false });
        }
        if (muteStr) embed.addFields({ name: "In-Game Mute", value: muteStr, inline: false });
        if (ipRec?.flagged && !tb) embed.addFields({ name: "Evasion Watch", value: "This account matches an active IP/EOS flag — next join is auto-banned.", inline: false });

        // Faction kills — how many times this player has killed members of each
        // faction, cross-referenced from the live kill log against the spawn files.
        const fkills = factionKillBreakdown(playerId);
        if (fkills && Object.keys(fkills).length) {
          const ordered = Object.entries(fkills).sort((a, b) => b[1].total - a[1].total);
          const grand   = ordered.reduce((a, [, d]) => a + d.total, 0);
          embed.addFields({
            name: `Faction Kills — ${grand} total`,
            value: ordered.map(([f, d]) => `${GLYPH.rank} **${f}** — ${d.total} kill${d.total !== 1 ? "s" : ""}`).join("\n"),
            inline: false,
          });
        }

        brand(embed, { thumb: true, footer: { text: "Playtime tracked every 60s since deployment" } });
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         INSPECT  (owner only — full dossier incl. IPs & alts)
         ───────────────────────────────────────────────────── */

      // Stale registration (command removed in a redeploy but still cached by
      // Discord) - answer instead of leaving the user on "thinking…" forever.
      default:
        return interaction.reply({ embeds: [errorEmbed("Unknown Command", `\`/${name}\` isn't wired up in this build — the command list may still be refreshing. Try again in a minute.`)], flags: MessageFlags.Ephemeral }).catch(() => {});
    }
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
