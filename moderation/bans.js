/* ---------------- moderation/bans: native RCON ban/kick enforcement, reconcile, unban ----------------
   Extracted from index.js. All shared helpers/state it uses are injected via ctx
   (a plain object built in index.js). Usage: require("./moderation/bans")(ctx). */
module.exports = function(ctx) {
  const {
  ACTIVE_SERVERS, ALL_FACTIONS, ActionRowBuilder, ActivityType, BAN_DURATIONS, BAN_REASON_LABELS,
  BLACKLIST_IDS, BOT_AUTHOR, BOT_COPYRIGHT, BOT_START_MS, BOT_VERSION, BRAND_NAME,
  BUILD_ID, ButtonBuilder, ButtonStyle, CASINO_CONFIG_DEFAULTS, CLIN, CURRENT_LOG_LEVEL,
  Client, ComponentType, DASHBOARD_CHANNEL, DASHBOARD_INTERVAL_MS, DAY_MS, DB_EXPORT_INTERVAL_MS,
  DIVIDER, DONATOR_FILE, EXTRA_FACTION_FILES, EmbedBuilder, FACTION_DEFAULT_CAP, FACTION_RANKS,
  FACTION_ROLES_PATH, FACTION_SPAWN_MAP, FILES, GLYPH, GatewayIntentBits, KILLFEED_CHANNEL,
  LEADERBOARD_INTERVAL_MS, LEADERBOARD_TOP_N, LINK_APPROVER_ROLE, LINK_REQUEST_CHANNEL, LOCK_FILE, LOG_FILE,
  LOG_LEVEL, MASTER_NAMES, MENUS, MENU_PANEL_CHANNEL, MENU_ROLE_DEFAULTS, MODSAVE_REL,
  MODSAVE_SYNC_INTERVAL_MS, MODSAVE_SYNC_SKIP, MessageFlags, ModalBuilder, NV, OWNER_IDS,
  PAVLOV_BASES, PAVLOV_BASE_1, PLAYERLIST_CHANNEL, PLAYERLIST_INTERVAL_MS, PLAYTIME_LB_CHANNEL, PUNISHMENTS,
  PUNISH_BY_VALUE, PUNISH_CHOICES, PermissionFlagsBits, QUOTES, RCON_BLACKLIST_ROLE_ID, RCON_HEALTH_INTERVAL_MS,
  REST, RULE, RoleSelectMenuBuilder, Routes, SPAWN_FILE_MAP, STAFF_MENU_ID,
  SlashCommandBuilder, StringSelectMenuBuilder, TextInputBuilder, TextInputStyle, UFW_BLOCK, UNBARRED_IDS,
  UPDATE_LOG_CHANNEL, WAGE_INTERVAL_MS, WAGE_TIERS, WebhookClient, _IPV4_RE, __dirname,
  _blAllCache, _hasRole, _modLogIndexCache, _sameId, acquireSingleInstanceLock, addUserBlacklist,
  adminOnlyEmbed, atomicCopyPreservingMtime, atomicWriteFile, awaitOwnedComponent, bar, blacklistAdd,
  blacklistAll, blacklistAllCached, blacklistHas, blacklistPathFor, blacklistRemove, blacklistStatus,
  blacklistedEmbed, brand, brandIcon, cell, checkAutoRotate, checkRateLimit,
  chunkFields, clampEmbed, clinical, commandPlayerCandidates, confirmDialog, countFactionRank,
  deniedEmbed, disableValidators, discoverPavlovBases, easternClock, emptyIdEmbed, ensureFactionFiles,
  ensureFile, errorEmbed, execFile, execFileSync, exportDbToJson, factionLeaderOnlyEmbed,
  factionLeaderStrictEmbed, feedHook, firewallBlockIps, firewallField, firewallResyncAll, firewallUnblockIps,
  formatKD, formatPlaytime, formatTimeLeft, formatUptime, fs, getFactionCap,
  getFactionDefaultRank, getFactionRank, getFactionRankBadge, getFactionRankCap, getFactionRankCaps, getFactionRankConfig,
  getFactionRankOrder, getKnownPlayerChoices, getLastSeen, getOnlinePlayers, getPlayerHistory, getPlayerRanks,
  getServerConfig, hasAdminRole, hasFactionLeaderRole, hasModRole, hasServer2, hasServer3,
  healTreeOwnership, hero, importBlacklistToBans, intendedOwner, ipBans, isAutobanExempt,
  isBlacklisted, isMasterName, isOwner, isPidAlive, isPlayerOnline, listFilesRec,
  loadAutoRotate, loadBans, loadCasinoConfig, loadFactionAudit, loadFactionConfig, loadFactionRanks,
  loadKnownPlayers, loadLastSeen, loadMenuGrants, loadMenuRoles, loadModLog, loadPlaytime,
  loadRoles, loadWages, log, logger, looksLikeLedgerEntry, matchTreeOwner,
  md5, menuRoleTiers, meter, mirrorPaths, modOnlyEmbed, ownerOnlyEmbed,
  paginate, parseClockTime, parseDuration, path, pip, preserveBalanceAcrossKick,
  punishDurationLabel, randomQuote, rankBadge, rankHasRoom, rankWeight, rateLimitEmbed,
  rateLimits, readBlacklist, reconcileBlacklists, recordKnownPlayers, recordLastSeen, releaseSingleInstanceLock,
  removeAutobanExempt, removeBans, removeFactionRank, removeUserBlacklist, safeRead, safeWrite,
  sanitizeBanName, sanitizeId, sanitizeMessage, saveCasinoConfig, savePlaytime, saveRoles,
  saveUnbarred, saveUserBlacklist, saveWages, seedKnownPlayers, sendRcon, sendRconBoth,
  sendRconRaw, serverEmoji, serverLabel, setAutoRotate, setFactionCap, setFactionRank,
  setFactionRankCap, setMenuRole, spawn, splitPages, successEmbed, syncAllModSave,
  syncPlayerLedger, update, upsertPermBan, upsertTempBan, validateConfig, warningEmbed,
  wouldWipeBalance, writeFactionAudit, writeGameFile, writeGameFileSingle, writeModLog,
  } = ctx;

async function banWithIp(playerId, server = "both", opts = {}) {
  const name = sanitizeBanName(playerId);
  // Master names are never banned — no matter which path asks (command or auto-ban).
  if (isMasterName(name)) {
    logger.warn("Bans", `Refused to ban master name "${name}"`);
    return { ids: [], ips: [], alts: [], field: null, blacklist: { name, servers: 0 }, ok: false, master: true };
  }
  removeAutobanExempt(name).catch(() => {});   // a deliberate ban clears any unban exemption
  // Order matters: ban first, then firewall-block the IP, THEN kick — so a caught
  // player is barred and network-blocked BEFORE they're dropped from the server.
  // Native RCON Ban by USERNAME on every server (no kick yet). NO blacklist.txt.
  let enforced = { servers: 0 };
  try { enforced = await hardEnforce(name, { kick: false }); }
  catch (err) { logger.warn("Bans", `RCON ban failed for "${name}": ${err.message}`); }
  logger.info("Bans", `Native-banned "${name}" on ${enforced.servers}/${ACTIVE_SERVERS.length} server(s)`);
  scheduleBanRecheck(name);                     // 30s: blacklist.txt backup + re-enforce
  // Flag their EXACT confirmed IP(s) + EOS id so an alt/reconnect re-triggers the ban.
  let enf;
  try { enf = ipBans.blacklistPlayer(name, { flagId: true }); }
  catch (err) { logger.warn("IPBan", `IP flag failed for ${name}: ${err.message}`); enf = { ids: [], ips: [], alts: [], field: null }; }
  // Block the IP(s) at the OS firewall on EVERY ban (opt-in, UFW_BLOCK). Removed on unban.
  let firewall = null;
  {
    const ips = [...new Set([...(enf.ips || []), ...(opts.ip ? [opts.ip] : [])])];
    try { firewall = await firewallBlockIps(ips); }
    catch (err) { logger.warn("Firewall", `block failed for ${name}: ${err.message}`); }
  }
  // Now kick — they're banned + firewall-blocked, so this drop is the final step.
  try { await hardEnforce(name, { banToo: false }); }
  catch (err) { logger.warn("Bans", `RCON kick failed for "${name}": ${err.message}`); }
  return { ...enf, blacklist: { name, servers: enforced.servers }, ok: enforced.servers > 0, firewall };
}



/* Force-ban + remove a player by USERNAME on every server — native RCON `Ban` + `Kick`,
   NO blacklist.txt — logging the server's response to each so `pm2 logs` shows exactly
   what landed. Returns { servers } = how many servers accepted a command. */
async function hardEnforce(name, { banToo = true, kick = true } = {}) {
  const target = sanitizeId(name);
  if (!target) return { servers: 0 };
  preserveBalanceAcrossKick(name);             // don't let the kick wipe their caps
  let ok = 0;
  const perServer = async (srv) => {
    const verbs = [];
    if (banToo) verbs.push(`Ban ${target}`);
    if (kick)   verbs.push(`Kick ${target}`);
    if (!verbs.length) return;
    let served = false;
    for (const cmd of verbs) {
      try {
        const r = await sendRcon(cmd, srv, 2500, 1);
        served = true;
        logger.info("IPGuard", `${srv} < ${cmd}  ->  ${String(r ?? "").replace(/\s+/g, " ").trim().slice(0, 140) || "(no response)"}`);
      } catch (e) { logger.warn("IPGuard", `${srv} < ${cmd}  FAILED: ${e.message}`); }
    }
    if (served) ok++;
  };
  await Promise.all(ACTIVE_SERVERS.map(perServer));
  return { servers: ok };
}

/* ---- in-game mute (RCON Gag) ----
   A gag doesn't survive a reconnect, so a muted player is re-gagged on EVERY join
   until their mute expires; the first join AFTER expiry ungags them and clears it. */
const loadMutes = () => safeRead(FILES.MUTES, {});
const getMute   = (name) => loadMutes()[String(name ?? "").toLowerCase()] || null;
function setMute(name, data) { return update(FILES.MUTES, {}, (m) => { m[String(name).toLowerCase()] = data; return m; }); }
function clearMute(name)     { return update(FILES.MUTES, {}, (m) => { delete m[String(name).toLowerCase()]; return m; }); }
// parseDuration ("30s"/"10m"/"2h"/"1d" -> ms) lives in ./utils.
// `Gag <username>` — a bare toggle, confirmed no True/False argument works at all
// (tried both; neither did anything). So both of these send the exact same command -
// correct only when the target's actual gag state is the opposite of what we want,
// which holds for every real call site below. Sent to every server.
const gagEverywhere   = (name) => { const t = sanitizeId(name); if (t) for (const srv of ACTIVE_SERVERS) sendRcon(`Gag ${t}`, srv, 2500, 0).catch(() => {}); };
const ungagEverywhere = (name) => { const t = sanitizeId(name); if (t) for (const srv of ACTIVE_SERVERS) sendRcon(`Gag ${t}`, srv, 2500, 0).catch(() => {}); };
// Called on every live join: re-apply an active gag. A gag doesn't survive a
// reconnect, so a fresh join is never actually gagged yet - an EXPIRED mute needs no
// RCON call at all (there's nothing live to toggle off), just dropping the record.
// Calling the toggle here "just in case" would incorrectly gag someone who was never
// gagged this session.
function applyMuteOnJoin(name) {
  const mute = getMute(name);
  if (!mute) return;
  if (mute.expires && mute.expires <= Date.now()) {
    clearMute(name);
    logger.info("Mute", `${name}'s expired mute record cleared on join (nothing live to un-toggle)`);
  } else {
    gagEverywhere(name);                         // re-apply the mute for this session
    logger.info("Mute", `Re-gagged ${name} on join`);
  }
}

/* 30s after a ban, back it up: write the name into blacklist.txt (reconnect block that
   doesn't depend on the RCON ban sticking) and re-run enforcement in case they slipped
   back in. Requested belt-and-suspenders on top of the native ban. */
function scheduleBanRecheck(name) {
  setTimeout(async () => {
    // Skip if they were unbanned (or their temp ban expired) in the 30s window.
    const stillBanned = loadBans().some(b => _sameId(b.playerId, name) && (b.permanent || (b.expires && b.expires > Date.now())));
    if (!stillBanned) return;
    try { blacklistAdd(name); } catch (e) { logger.warn("Bans", `30s blacklist.txt backup failed for ${name}: ${e.message}`); }
    try { await hardEnforce(name); } catch {}
    logger.info("Bans", `30s recheck for ${name}: blacklist.txt backup written + re-enforced`);
  }, 30_000);
}

/* Safety-net enforcement: RefreshList is the AUTHORITATIVE list of who is actually in
   the game. Every sweep, cross-reference it against the active ban list + IP/EOS flags
   and force-remove anyone banned who's still online — a ban whose join-time kick missed,
   or a player who was already in the game when the ban landed. This is what actually
   gets a stuck banned player OUT; the per-command RCON responses are logged so we can
   see whether the removal takes. */
let _sweepBusy = false;
async function enforceBansSweep() {
  if (_sweepBusy) return;                       // don't overlap a slow sweep
  _sweepBusy = true;
  try {
    const now = Date.now();
    const banned = new Set(loadBans()
      .filter(b => b.permanent || (b.expires && b.expires > now))
      .map(b => String(b.playerId).toLowerCase()));
    for (const srv of ACTIVE_SERVERS) {
      let players;
      try { players = await getOnlinePlayers(srv); } catch { continue; }
      for (const p of players) {
        const nm = p.name;
        if (!nm || isMasterName(nm) || isAutobanExempt(nm)) continue;   // never sweep an unban-exempt player
        const nameBanned = banned.has(nm.toLowerCase());
        let flaggedHit = false;
        if (!nameBanned) { try { flaggedHit = !!ipBans.getRecord(nm)?.flagged; } catch {} }   // flagged IP/EOS
        if (nameBanned || flaggedHit) {
          logger.warn("BanSweep", `${nm} is banned/flagged but ONLINE on ${srv} — force-removing`);
          await hardEnforce(nm);                // native Ban + Kick by username, responses logged
          // NOTE: the sweep does NOT create a ban RECORD — the IP/EOS flag already
          // persists the auto-ban and re-catches them on join. Recording here made a
          // flagged/shared IP mass-create ban entries wrongly attributed to a person.
          continue;
        }
        // NOTE: mutes are applied once on /mute and re-applied once on join
        // (applyMuteOnJoin) - NOT repeated here. Gag <name> is a bare toggle with
        // no explicit True/False, so calling it again on every 60s sweep would flip
        // an already-muted player's state back off instead of safely re-affirming it.
      }
    }
  } finally { _sweepBusy = false; }
}

/* One-time repair: rewrite legacy auto-ban records (moderator "IP-Guard" or a reason
   starting "Auto-ban") so /checkban shows the REAL punishment — the original ban's
   offense + moderator where we can find it, else a clean "Ban evasion". */
async function fixAutoBanReasons() {
  await update(FILES.TEMPBAN, [], (bans) => {
    let n = 0;
    for (const b of bans) {
      if (b.moderator === "IP-Guard" || /^auto-ban/i.test(String(b.reason || ""))) {
        let rec = null; try { rec = ipBans.getRecord(b.playerId); } catch {}
        const src = sourceBanFor(b.playerId, rec?.id);
        b.reason = src?.reason || "Ban evasion";
        b.moderator = src?.moderator || "Ban evasion (auto)";
        n++;
      }
    }
    if (n) logger.info("Bans", `Rewrote ${n} legacy auto-ban record(s) to show the real punishment (was "IP-Guard")`);
    return bans;
  });
}

/* Ban-list reconciliation: the bot's ban DB (tempbans.json) is the source of truth.
   Re-apply every ACTIVE ban to each server (native RCON Ban) and to blacklist.txt so
   the server's ban list is rebuilt and stays complete — even after a SERVER restart
   wipes its native bans. Re-banning an already-banned player is a harmless no-op, so
   this makes bans self-healing: a banned player can't slip in through a lost ban list.

   Every RCON call opens a brand-new TCP connection (see sendRconRaw), so a big ban
   list means a real burst of connections to the game server. That's fine when it's
   actually needed (the game server itself restarted and lost its native bans), but
   this used to run 15s after every BOT restart too - which doesn't touch the game
   server's own ban list at all, so on a large ban list it was hammering RCON with
   dozens of fresh connections for no reason on every single bot deploy. Gate it to
   run at most once per BAN_RECONCILE_MIN_INTERVAL_MS regardless of how often the
   bot restarts. */
const BAN_RECONCILE_MIN_INTERVAL_MS = 30 * 60 * 1000;
let _reconcileBusy = false;
async function reconcileBans() {
  if (_reconcileBusy) return;
  const state = safeRead(FILES.BAN_RECONCILE_STATE, {});
  if (state.lastRun && Date.now() - state.lastRun < BAN_RECONCILE_MIN_INTERVAL_MS) {
    logger.info("Bans", `Skipped ban reconcile - last ran ${Math.round((Date.now() - state.lastRun) / 60000)}m ago (min interval ${BAN_RECONCILE_MIN_INTERVAL_MS / 60000}m). Native bans persist on the game server across bot restarts on their own.`);
    return;
  }
  _reconcileBusy = true;
  try {
    const now = Date.now();
    const active = loadBans().filter(b => b.permanent || (b.expires && b.expires > now));
    let n = 0;
    for (const b of active) {
      const name = sanitizeId(b.playerId);
      if (!name || isMasterName(name) || isAutobanExempt(name)) continue;
      // Re-verify at ISSUE time: an /unban during this pass removes the DB entry, and
      // re-Banning from the stale snapshot would natively re-ban them with nothing left
      // to lift it (their Unban already went out).
      const still = loadBans().some(x => _sameId(x.playerId, b.playerId) && (x.permanent || (x.expires && x.expires > Date.now())));
      if (!still) continue;
      try { blacklistAdd(b.playerId); } catch {}                     // blacklist.txt reconnect-block backup
      for (const srv of ACTIVE_SERVERS) { try { await sendRcon(`Ban ${name}`, srv, 2500, 0); } catch {} }
      n++;
      await new Promise(r => setTimeout(r, 400));                    // gentle pacing so a big list doesn't flood RCON with fresh connections
    }
    safeWrite(FILES.BAN_RECONCILE_STATE, { lastRun: Date.now() });
    if (n) logger.info("Bans", `Reconciled ${n} active ban(s) to native RCON + blacklist.txt across ${ACTIVE_SERVERS.length} server(s)`);
  } finally { _reconcileBusy = false; }
}

/* The original ban whose flag caught this player — so /checkban and the ban record
   show the REAL offense, not "IP-Guard" / "Ban evasion". A real ban is one whose reason
   isn't itself an auto-ban and whose moderator isn't the auto-ban marker. Matched by:
   1) the SAME EOS account (evader kept the account, changed name),
   2) the same name, or a shared-confirmed-IP alt. */
function isRealBan(b) {
  // A per-player punishment — NOT an auto-ban and NOT a broad /configure IP/name
  // blacklist (those aren't a punishment on one person, and copying their moderator
  // makes every match look like that admin banned it).
  return b && b.reason && !/^(auto-ban|ban evasion)/i.test(b.reason)
    && !/blacklisted via/i.test(b.reason)
    && b.moderator !== "IP-Guard" && !/evasion/i.test(String(b.moderator || ""));
}
function sourceBanFor(name, uniqueId) {
  const real = loadBans().filter(isRealBan);
  if (uniqueId) {   // same account (EOS id) under a possibly different name
    const byEos = real.find(b => { try { return ipBans.getRecord(b.playerId)?.id === uniqueId; } catch { return false; } });
    if (byEos) return byEos;
  }
  let related = [name];
  try { related = [name, ...(ipBans.getAltNamesOf(name) || [])]; } catch {}
  const relSet = new Set(related.map(r => String(r).toLowerCase()));
  return real.find(b => relSet.has(String(b.playerId).toLowerCase())) || null;
}
/* What should the IP-Guard do with a join that matched a flagged IP/name/id?
   - "block": an UNEXPIRED temp ban covers this name — the blacklist already bounced
     them; log it, never escalate.
   - "lift":  the covering temp ban has EXPIRED but the entry/flags are still around
     (the 60s lift sweep hasn't run yet, or a stale flag survived). A served temp ban
     must never turn permanent — lift it right now instead.
   - "ban":   nothing covers them (evasion alt / fresh blacklist match) — auto-ban. */
function autoBanDecision(existing, reason, now = Date.now()) {
  if (existing && !existing.permanent && existing.expires) {
    if (existing.expires > now) return "block";
    // Served temp ban: lift on rejoin instead of escalating. IP and EOS-account flags
    // both come from the ban itself (stale after expiry). A "username" match is only
    // ever a deliberate manual flag (/configure), so that still escalates.
    if (/blacklisted (ip|account)/i.test(String(reason || ""))) return "lift";
  }
  return "ban";
}

// Lift a ban: remove the name from blacklist.txt on both installs + clear IP flags.
function unbanEverywhere(playerId) {
  const name = sanitizeBanName(playerId);
  let bl = { name, removed: 0 };
  try { bl = blacklistRemove(name); } catch (err) { logger.error("Bans", `blacklist remove failed for "${name}": ${err.message}`); }
  let cleared = null;
  try {
    cleared = ipBans.unblacklistPlayer(name);
    // Release any OS firewall block on those IPs so the unban actually restores access.
    if (UFW_BLOCK && cleared?.ips?.length) firewallUnblockIps(cleared.ips).catch(() => {});
  } catch {}
  // Lift the native RCON ban too (auto-bans issue `Ban <username>`), by USERNAME.
  const _uname = sanitizeId(name);
  if (_uname) (async () => { for (const srv of ACTIVE_SERVERS) { try { await sendRcon(`Unban ${_uname}`, srv, 2500, 1); } catch {} } })().catch(() => {});
  return { blacklist: bl, cleared: cleared?.cleared ?? { ips: 0, names: 0 } };
}

function parseRcon(raw) {
  if (!raw) return null;
  try {
    const start = raw.indexOf("{");
    const end   = raw.lastIndexOf("}");
    if (start === -1 || end === -1) return null;
    return JSON.parse(raw.slice(start, end + 1));
  } catch { return null; }
}


  return { BAN_RECONCILE_MIN_INTERVAL_MS, _reconcileBusy, _sweepBusy, applyMuteOnJoin, autoBanDecision, banWithIp, clearMute, enforceBansSweep, fixAutoBanReasons, gagEverywhere, getMute, hardEnforce, isRealBan, loadMutes, parseRcon, reconcileBans, scheduleBanRecheck, setMute, sourceBanFor, unbanEverywhere, ungagEverywhere };
};
