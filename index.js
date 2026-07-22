// Pavlov VR moderation bot for RP servers (theme-neutral; skin via BOT_NAME).
// (c) 2026 bs9zw69ff9-source. Private project, please don't redistribute.
require("dotenv").config();
const fs     = require("fs");
const path   = require("path");
const { execFileSync, execFile, spawn } = require("child_process");
const ipBans = require("./ipBans");
const {
  Client,
  GatewayIntentBits,
  PermissionFlagsBits,
  ActivityType,
  REST,
  Routes,
  SlashCommandBuilder,
  EmbedBuilder,
  ActionRowBuilder,
  ButtonBuilder,
  ButtonStyle,
  ComponentType,
  StringSelectMenuBuilder,
  RoleSelectMenuBuilder,
  MessageFlags,
  ModalBuilder,
  TextInputBuilder,
  TextInputStyle,
  WebhookClient,
  disableValidators,
} = require("discord.js");

// Embeds are only text carriers now (rendered to plain content at send time), so
// Discord's embed limits (1024/field, 4096/description) must not throw at BUILD
try { disableValidators(); } catch {}

/* ── Live connection feed ────────────────────────────────────────────
   Posts a fresh message for every player join (name - ID - IP). Paste a
   Discord channel webhook URL into CONNECT_WEBHOOK_URL - works out of the
   box, no bot channel permissions needed. Use a PRIVATE admin channel: it
   exposes player IP addresses. */
let feedHook = null;
if (process.env.CONNECT_WEBHOOK_URL) {
  try { feedHook = new WebhookClient({ url: process.env.CONNECT_WEBHOOK_URL }); console.log("[Feed] connection feed enabled"); }
  catch (e) { console.warn(`[Feed] invalid CONNECT_WEBHOOK_URL: ${e.message}`); feedHook = null; }
}

// ---- version & startup ----
const BOT_VERSION  = "3.2.2";
const BOT_START_MS = Date.now();

/* Authorship / build attribution. Surfaced in /help and /ping; protected by
   the LICENSE. Removing or altering it violates the license terms. */
const BOT_AUTHOR    = "bs9zw69ff9-source";
const BOT_COPYRIGHT = `2026 ${BOT_AUTHOR} - All rights reserved`;
const BUILD_ID      = process.env.BUILD_ID || `v${BOT_VERSION}-${new Date(BOT_START_MS).toISOString().slice(0, 10)}`;

// ---- owners  (super-users - top of every permission) ----
// Overridable via OWNER_IDS / SUPER_OWNER_IDS in .env (comma/space separated);
// the hardcoded sets below are the fallback so a bare checkout still works.
const _envIds = (name) => String(process.env[name] ?? "").split(/[\s,]+/).filter(Boolean);
const OWNER_IDS = new Set(_envIds("OWNER_IDS").length ? _envIds("OWNER_IDS") : [
  "1014251293159731310",
  "678362059905171471",
]);
function isOwner(userId) { return OWNER_IDS.has(String(userId)); }

// ---- super owner  (the very top of the hierarchy) ----
// A super owner outranks everyone. Their moderation actions can't be overridden by
// anyone else; they can override anyone. (Also members of OWNER_IDS above, so every
// existing owner-gated check still passes for them.)
const SUPER_OWNER_IDS = new Set(_envIds("SUPER_OWNER_IDS").length ? _envIds("SUPER_OWNER_IDS") : [
  "1014251293159731310",
]);
function isSuperOwner(userId) { return SUPER_OWNER_IDS.has(String(userId)); }
// A super owner must also pass every owner-gated check even when the sets come from env.
for (const id of SUPER_OWNER_IDS) OWNER_IDS.add(id);

/* Staff command hierarchy: super owner > owner > admin > mod. A lower tier can never
   override (unban / unmute / clear) a moderation action issued by a higher tier.
   The tier logic lives in a pure, unit-tested module - we inject the role predicates. */
const { commandTier: _commandTier, commandTierName, canOverride } = require("./moderation/hierarchy");
function commandTier(member) {
  return _commandTier(member, { isSuperOwner, isOwner, hasAdminRole, hasModRole });
}

/* Master in-game names - the people who run the servers. They are never banned,
   never IP-logged, and are handed a menu automatically on every join. Matched by
   USERNAME (case-insensitive), since that's what RCON and the logs give us. */
const MASTER_NAMES = new Set(["lxpxham", "holosight1"]);
function isMasterName(name) { return MASTER_NAMES.has(String(name ?? "").trim().toLowerCase()); }

/* Master/owner IPs - protected addresses the bot must NEVER block at the OS
   firewall, no matter what flags them. Enforced on every block and re-checked by
   the periodic firewall reconcile (which also removes the rule if it ever appears). */
const MASTER_IPS = new Set(["86.166.107.200"]);
const isMasterIp = (ip) => MASTER_IPS.has(String(ip ?? "").trim());

// ---- structured logger ----
const LOG_FILE = "./bot.log";
const LOG_LEVEL = { DEBUG: 0, INFO: 1, WARN: 2, ERROR: 3 };
const CURRENT_LOG_LEVEL = LOG_LEVEL[process.env.LOG_LEVEL?.toUpperCase()] ?? LOG_LEVEL.INFO;

function log(level, tag, message, extra) {
  const levelName = Object.keys(LOG_LEVEL).find(k => LOG_LEVEL[k] === level) ?? "INFO";
  if (level < CURRENT_LOG_LEVEL) return;
  const ts   = new Date().toISOString();
  const line = `[${ts}] [${levelName.padEnd(5)}] [${tag}] ${message}${extra ? " " + JSON.stringify(extra) : ""}`;
  console.log(line);
  try { fs.appendFileSync(LOG_FILE, line + "\n"); } catch {}
}
/* Diagnostics the owner can actually see: every warn/error is counted and the
   most recent ones kept in a small ring buffer, surfaced by /health. This gives
   eyes on problems the resilient .catch(() => {}) paths would otherwise hide. */
const _diag = { counts: { warn: 0, error: 0 }, recent: [], startedAt: Date.now() };
function _diagRecord(level, tag, message) {
  _diag.counts[level]++;
  _diag.recent.push({ level, tag, message: String(message).slice(0, 160), at: Date.now() });
  if (_diag.recent.length > 25) _diag.recent.shift();
}
const logger = {
  debug: (t, m, e) => log(LOG_LEVEL.DEBUG, t, m, e),
  info:  (t, m, e) => log(LOG_LEVEL.INFO,  t, m, e),
  warn:  (t, m, e) => { _diagRecord("warn",  t, m); log(LOG_LEVEL.WARN,  t, m, e); },
  error: (t, m, e) => { _diagRecord("error", t, m); log(LOG_LEVEL.ERROR, t, m, e); },
};
// Unhandled promise rejections are logged (and counted) instead of vanishing.
process.on("unhandledRejection", (err) => {
  try { logger.error("Unhandled", err?.message || String(err)); } catch {}
});

// ---- config validation ----
function validateConfig() {
  const required = [
    "DISCORD_TOKEN", "CLIENT_ID",
    "RCON_HOST_1", "RCON_PORT_1", "RCON_PASSWORD_1",
  ];
  const optional = [
    "RCON_HOST_2", "RCON_PORT_2", "RCON_PASSWORD_2",
    "RCON_HOST_3", "RCON_PORT_3", "RCON_PASSWORD_3",
    "MODSAVE_PATH", "MOD_LOG_CHANNEL", "BAN_LOG_CHANNEL", "LEADERBOARD_CHANNEL", "LOG_LEVEL",
    "PLAYERLIST_CHANNEL", "DONATOR_PATH", "BLACKLIST_IDS", "BUILD_ID",
    "IPHUB_API_KEY", "IPQS_API_KEY", "KILLFEED_CHANNEL",
  ];
  const missing  = required.filter(k => !process.env[k]);
  const absent   = optional.filter(k => !process.env[k]);

  if (missing.length) {
    logger.error("Config", `Missing REQUIRED env vars: ${missing.join(", ")}`);
    process.exit(1);
  }
  if (absent.length) {
    logger.warn("Config", `Optional env vars not set (features may be limited): ${absent.join(", ")}`);
  }
  logger.info("Config", "Configuration validated.");
}

// Fail fast: validate before creating data files, wiring intervals, or logging in.
validateConfig();

// ---- single-instance lock ----
// Two copies of this process running against the same working directory - a leftover
// pm2/systemd instance that didn't fully die on restart, a stray `node index.js` left
// running in a screen/tmux session, etc. - independently track and edit the same
// Discord messages (leaderboards, player list, dashboard) and race each other. The
// visible symptom is those messages randomly reposting every few minutes with no
// bot restart in sight. Refuse to start a second copy instead of silently running as
// one; a PID that's actually still alive means this really is a duplicate, while a
// leftover lock from a process that's already gone (crash, kill -9) is just stale.
const LOCK_FILE = "./bot.lock";
// Signal 0 is a no-op - it doesn't actually signal the process, just checks whether
// it could be signaled (i.e. whether that pid exists). Throws if it doesn't.
function isPidAlive(pid) {
  try { process.kill(pid, 0); return true; } catch { return false; }
}
function acquireSingleInstanceLock() {
  // Atomic exclusive-create (O_EXCL): if two copies start at the same instant, only one
  // can win the create - there's no read-then-write TOCTOU window where both pass the
  // liveness check and then both write. On EEXIST, decide whether the lock is stale.
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const fd = fs.openSync(LOCK_FILE, "wx");
      fs.writeSync(fd, String(process.pid));
      fs.closeSync(fd);
      return;
    } catch (err) {
      if (err.code !== "EEXIST") { logger.warn("Bot", `Could not write ${LOCK_FILE}: ${err.message}`); return; }
      let pid = 0;
      try { pid = parseInt(fs.readFileSync(LOCK_FILE, "utf8").trim(), 10); } catch {}
      if (pid && pid !== process.pid && isPidAlive(pid)) {
        logger.error("Bot", `Another instance is already running (pid ${pid}) in this directory. Refusing to start a second copy - stop it first (or delete ${LOCK_FILE} if you're sure it's stale).`);
        process.exit(1);
      }
      // Stale lock (owner dead/crashed) or our own pid - remove it and retry the create.
      try { fs.unlinkSync(LOCK_FILE); } catch {}
    }
  }
  logger.warn("Bot", `Could not acquire ${LOCK_FILE} after retry - continuing without a single-instance lock.`);
}
function releaseSingleInstanceLock() {
  try { if (fs.readFileSync(LOCK_FILE, "utf8").trim() === String(process.pid)) fs.unlinkSync(LOCK_FILE); } catch {}
}
acquireSingleInstanceLock();

// ---- data files + SQLite storage layer (extracted to ./database) ----
// Owns bot.db, the JSON->SQLite migration, the cache + write serialization, the
// periodic JSON export, the FILES/DEFAULTS registry and the fs-ownership helpers.
const {
  FILES,
  safeRead, safeWrite, update,
  exportDbToJson, DB_EXPORT_INTERVAL_MS,
  ensureFile, matchTreeOwner, intendedOwner,
} = require("./database")({ logger, baseDir: __dirname });

/* ---- Typed loaders / savers ---- */
const loadBans          = () => safeRead(FILES.TEMPBAN,        []);
/* Serialized temp-ban mutations - go through update() so concurrent commands
   and the 60s expiry sweep can't clobber each other's writes. */
const _sameId = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
function upsertTempBan(entry) {
  // Creating a ban record is a deliberate ban - it must clear any unban exemption,
  // or an exempt player could hold a /banlist entry the sweeps refuse to enforce.
  try { removeAutobanExempt(entry.playerId).catch(() => {}); } catch {}
  return update(FILES.TEMPBAN, [], (bans) => {
    const next = bans.filter(b => !_sameId(b.playerId, entry.playerId));
    next.push(entry);
    return next;
  }).then(r => { syncModsaveBanlist(); return r; });   // refresh the custom ban-message file
}
/** Record a PERMANENT ban in the same ban JSON (no expiry). Upserts by playerId,
    so it supersedes any existing temp ban for that player. */
function upsertPermBan({ playerId, reason, moderator, moderatorRank, moderatorId, server }) {
  return upsertTempBan({ playerId, reason: reason || "Permanent ban", moderator: moderator || "system", moderatorRank, moderatorId, server: server || "both", at: Date.now(), permanent: true });
}
/** One-time/startup import: pull any names already in blacklist.txt into the ban JSON
    (as permanent entries) so /banlist - which reads the JSON - shows pre-existing bans. */
function importBlacklistToBans() {
  let names = [];
  try { names = blacklistAll(); } catch { return; }
  if (!names.length) return;
  return update(FILES.TEMPBAN, [], (bans) => {
    const have = new Set(bans.map(b => String(b.playerId).toLowerCase()));
    let added = 0;
    for (const nm of names) {
      if (have.has(nm.toLowerCase())) continue;
      bans.push({ playerId: nm, reason: "Imported from blacklist.txt", moderator: "system", at: Date.now(), permanent: true });
      try { removeAutobanExempt(nm).catch(() => {}); } catch {}   // a re-appearing blacklist name is a deliberate ban
      added++;
    }
    if (added) logger.info("Bans", `Imported ${added} blacklist.txt name(s) into the ban registry for /banlist`);
    return bans;
  });
}
/** Remove one or more player IDs from the temp-ban list (case-insensitive). */
function removeBans(...ids) {
  const drop = ids.filter(Boolean).map(s => String(s).toLowerCase());
  return update(FILES.TEMPBAN, [], (bans) => bans.filter(b => !drop.includes(String(b.playerId).toLowerCase())))
    .then(r => { syncModsaveBanlist(); return r; });   // refresh the custom ban-message file
}
const loadRoles         = () => safeRead(FILES.ROLES,          { modRoleId: "", adminRoleId: "", factionLeaderRoleId: "", policeRoleId: "", gambinoRoleId: "", colomboRoleId: "", nypdRoleId: "" });
const saveRoles         = (d) => safeWrite(FILES.ROLES,         d);

/* Police warrants: warrants STACK - a player can hold several. Keyed by lowercased
   id to a list: { [idLower]: [ { playerId, reason, by, byId, at }, ... ] }.
   Serialized writes via update(). A pre-stacking single object is coerced to a
   one-element list on read/write so old data keeps working. */
const loadWarrants = () => safeRead(FILES.WARRANTS, {});
const _asList = (v) => Array.isArray(v) ? v : (v ? [v] : []);
const getWarrants = (playerId) => _asList(loadWarrants()[String(playerId).toLowerCase()]);
// Append a warrant. Returns the player's new warrant count.
async function addWarrant(playerId, reason, by, byId) {
  let count = 0;
  await update(FILES.WARRANTS, {}, (m) => {
    const key = String(playerId).toLowerCase();
    const list = _asList(m[key]);
    list.push({ playerId, reason, by, byId, at: Date.now() });
    m[key] = list; count = list.length; return m;
  });
  return count;
}
// Remove one warrant by 1-based index, or ALL when index is null/undefined.
// Returns { removed: [...], remaining: n }.
async function removeWarrant(playerId, index = null) {
  let removed = [], remaining = 0;
  await update(FILES.WARRANTS, {}, (m) => {
    const key  = String(playerId).toLowerCase();
    const list = _asList(m[key]);
    if (index == null) { removed = list.slice(); delete m[key]; return m; }
    const i = Number(index) - 1;
    if (i >= 0 && i < list.length) { removed = [list[i]]; list.splice(i, 1); }
    if (list.length) { m[key] = list; remaining = list.length; } else { delete m[key]; }
    return m;
  });
  return { removed, remaining };
}

/* ---- police RP: arrests / sentences / rank suspensions ---- */
const PENAL = require("./penal/codes");
// Arrest history (permanent), stacked per player.
const loadArrests = () => safeRead(FILES.ARRESTS, {});
const getArrests  = (playerId) => _asList(loadArrests()[String(playerId).toLowerCase()]);
const totalJailServed = (playerId) => getArrests(playerId).reduce((s, a) => s + (Number(a.minutes) || 0), 0);
async function recordArrest(playerId, charges, by) {
  const entry = {
    charges: charges.map(c => ({ code: c.code, name: c.name, cls: c.cls, min: c.min, untilSober: !!c.untilSober })),
    minutes: charges.reduce((s, c) => s + (c.min || 0), 0), untilSober: charges.some(c => c.untilSober), by, at: Date.now(),
  };
  await update(FILES.ARRESTS, {}, (m) => { const k = String(playerId).toLowerCase(); m[k] = _asList(m[k]); m[k].push(entry); return m; });
  return entry;
}
// Active sentences drive the release timer.
const loadSentences = () => safeRead(FILES.SENTENCES, {});
function startSentence(playerId, label, minutes, by) {
  if (!(minutes > 0)) return null;
  const key = `${String(playerId).toLowerCase()}:${Date.now()}:${Math.random().toString(36).slice(2, 6)}`;
  const rec = { playerId, label, minutes, expires: Date.now() + minutes * 60_000, by };
  update(FILES.SENTENCES, {}, (m) => { m[key] = rec; return m; });
  return rec;
}
async function sentenceSweep() {
  const now = Date.now();
  const due = Object.entries(loadSentences()).filter(([, s]) => s.expires <= now);
  if (!due.length) return;
  for (const [, s] of due) announceArrest(`⏰ **${s.playerId}**'s sentence for ${s.label} has ended. They have been released.`);
  await update(FILES.SENTENCES, {}, (m) => { for (const [k] of due) delete m[k]; return m; });
}
// Every police-related log goes to the police channel (fallback mod-log).
const policeChannelId = () => POLICE_LOG_CHANNEL || ARREST_CHANNEL || process.env.MOD_LOG_CHANNEL;
function announceArrest(text) {
  const chId = policeChannelId();
  if (!chId) return;
  client.channels.fetch(chId).then(ch => ch?.isTextBased() && ch.send({ content: text, allowedMentions: { parse: [] } }))
    .catch(err => logger.warn("Police", `announce failed: ${err.message}`));
}
function logPolice(embed) {
  const chId = policeChannelId();
  if (!chId) return;
  client.channels.fetch(chId).then(ch => ch?.isTextBased() && ch.send({ embeds: [embed], allowedMentions: { parse: [] } }))
    .catch(err => logger.warn("Police", `log failed: ${err.message}`));
}

// Suspend a player's whitelist rank for `minutes`, auto-restoring it on expiry.
const loadRankSuspensions = () => safeRead(FILES.RANK_SUSPENSIONS, {});
const getRankSuspension   = (playerId) => loadRankSuspensions()[String(playerId).toLowerCase()] ?? null;
async function suspendRank(playerId, minutes, by) {
  const faction = (getPlayerFactions(playerId) || [])[0];
  if (!faction) return { ok: false, error: "not in a whitelist" };
  const ranks = getPlayerRanks(faction, playerId);
  const rank  = getFactionRank(faction, playerId);
  removePlayerFromAllRankFiles(faction, playerId);
  await removeFactionRank(faction, playerId);
  await update(FILES.RANK_SUSPENSIONS, {}, (m) => { m[String(playerId).toLowerCase()] = { playerId, faction, rank, ranks, minutes, expires: Date.now() + minutes * 60_000, by, at: Date.now() }; return m; });
  return { ok: true, faction, rank };
}
async function restoreRankSuspension(playerId) {
  const key = String(playerId).toLowerCase();
  const rec = loadRankSuspensions()[key];
  if (!rec) return null;
  for (const r of (rec.ranks?.length ? rec.ranks : [rec.rank]).filter(Boolean)) addPlayerToRankFile(rec.faction, rec.playerId, r);
  if (rec.rank) await setFactionRank(rec.faction, rec.playerId, rec.rank);
  await update(FILES.RANK_SUSPENSIONS, {}, (m) => { delete m[key]; return m; });
  return rec;
}
async function rankSuspensionSweep() {
  const now = Date.now();
  for (const s of Object.values(loadRankSuspensions()).filter(s => s.expires <= now)) {
    const r = await restoreRankSuspension(s.playerId);
    if (r) announceArrest(`⏰ **${s.playerId}**'s rank suspension in **${s.faction}** has ended - **${s.rank}** restored.`);
  }
}

const loadPlaytime      = () => safeRead(FILES.PLAYTIME,       {});
const savePlaytime      = (d) => safeWrite(FILES.PLAYTIME,      d);
const loadModLog        = () => safeRead(FILES.MODLOG,         []);
const loadFactionRanks  = () => safeRead(FILES.FACTION_RANKS,  {});
const loadFactionConfig = () => safeRead(FILES.FACTION_CONFIG, {});
const loadFactionAudit  = () => safeRead(FILES.FACTION_AUDIT,  []);
// (writes go through the serialized writeFactionAudit() - no direct saver needed)
const loadMenuGrants    = () => safeRead(FILES.MENU_GRANTS,    {});
const loadLastSeen      = () => safeRead(FILES.LASTSEEN,       {});
const loadKnownPlayers  = () => safeRead(FILES.KNOWN,          {});

// ---- mod log writer  (serialized) ----
function writeModLog(entry) {
  _modLogIndexCache = null;   // invalidate the per-player index on every write
  return update(FILES.MODLOG, [], (log) => {
    log.push({ ...entry, at: Date.now() });
    if (log.length > 10_000) log.splice(0, log.length - 10_000);
    return log;
  });
}

// Lazily-built per-player index over the mod log (up to 10k entries), rebuilt
// after any write - turns the per-command full scan into an O(1) lookup.
let _modLogIndexCache = null;
function getPlayerHistory(playerId) {
  if (!_modLogIndexCache) {
    const idx = new Map();
    for (const e of loadModLog()) {
      const k = e.playerId?.toLowerCase();
      if (!k) continue;
      if (!idx.has(k)) idx.set(k, []);
      idx.get(k).push(e);
    }
    _modLogIndexCache = idx;
  }
  return _modLogIndexCache.get(playerId.toLowerCase()) ?? [];
}

// ---- player notes  (freeform staff notes on any player - serialized) ----

// ---- last-seen tracking  (updated from the player-cache refresh loop) ----
function recordLastSeen(names, now = Date.now()) {
  if (!names || !names.length) return;
  return update(FILES.LASTSEEN, {}, (seen) => {
    for (const name of names) if (name) seen[String(name).toLowerCase()] = now;
    return seen;
  });
}
function getLastSeen(playerId) {
  return loadLastSeen()[playerId.toLowerCase()] ?? null;
}

/* ================================================================
   KNOWN-PLAYER REGISTRY  (everyone who's ever been seen online)
   ================================================================
   Persists each player's display name (original casing), first/last seen.
   Powers autocomplete fallback so offline players can still be picked.
   Only writes when a brand-new name appears, to avoid churning the file. */
function recordKnownPlayers(names, now = Date.now()) {
  if (!names || !names.length) return;
  // Serialized via update(): two overlapping cache refreshes can no longer clobber
  // each other's newly-discovered players (the old load->save could, and the
  // "only write when added" shortcut made the race window worse, not better).
  return update(FILES.KNOWN, {}, (known) => {
    for (const name of names) {
      if (!name) continue;
      const key = String(name).toLowerCase();
      if (known[key]) { known[key].lastSeen = now; }
      else { known[key] = { name: String(name), firstSeen: now, lastSeen: now }; }
    }
    return known;
  });
}

/** Autocomplete choices from the known-player registry (substring match,
    most-recently-seen first). Excludes any names already in `exclude`. */
function getKnownPlayerChoices(query, exclude = new Set(), limit = 25) {
  const q = query.toLowerCase();
  return Object.values(loadKnownPlayers())
    .filter(p => !exclude.has(p.name.toLowerCase()) && (!q || p.name.toLowerCase().includes(q)))
    .sort((a, b) => {
      // names that START with the query beat mid-string matches, then most recent first
      const ap = q && a.name.toLowerCase().startsWith(q) ? 0 : 1;
      const bp = q && b.name.toLowerCase().startsWith(q) ? 0 : 1;
      return ap - bp || (b.lastSeen ?? 0) - (a.lastSeen ?? 0);
    })
    .slice(0, limit)
    .map(p => ({ name: p.name, value: p.name }));
}

/** One-time backfill of the registry from data the bot already has on disk,
    so offline autocomplete works immediately (not just for players seen since
    deployment). Idempotent - recordKnownPlayers only writes new names. */
async function seedKnownPlayers() {
  const names = new Set();
  for (const k of Object.keys(loadPlaytime())) names.add(k);                 // playtime keys (display-cased)
  for (const b of loadBans())      if (b.playerId)   names.add(b.playerId);  // temp bans
  for (const d of (readDonatorFile() ?? [])) names.add(d);                   // donators
  try {                                                                      // every faction spawn + rank file
    for (const f of fs.readdirSync(FACTION_ROLES_PATH).filter(n => n.endsWith(".txt"))) {
      for (const id of (readFactionFile(f) ?? [])) names.add(id);
    }
  } catch { /* faction dir not reachable from here - skip */ }

  const before = Object.keys(loadKnownPlayers()).length;
  await recordKnownPlayers([...names]);
  const total = Object.keys(loadKnownPlayers()).length;
  if (total > before) logger.info("Known", `Seeded ${total - before} player(s) from existing data (registry now ${total})`);
}

/* ================================================================
   CONTEXT-AWARE AUTOCOMPLETE
   ================================================================
   For a player field, return the population that actually makes sense for
   the command (e.g. /stripmenu → only people who hold a menu grant), or null
   to fall back to the default online + known-player list. */
function commandPlayerCandidates(interaction) {
  const cmd = interaction.commandName;
  const sub = (cmd === "whitelist" || cmd === "donator") ? interaction.options.getSubcommand(false) : null;
  const known = loadKnownPlayers();
  const disp  = (k) => known[String(k).toLowerCase()]?.name ?? k;   // recover display casing for lowercased keys

  if (cmd === "unban" || cmd === "checkban")
                              return [...new Set([...loadBans().map(b => b.playerId), ...blacklistAllCached()])];  // temp-banned + blacklist.txt (cached for autocomplete)
  if (cmd === "stripmenu")    return Object.keys(loadMenuGrants()).map(disp);                          // holds a menu grant
  if (cmd === "donator" && sub === "remove") return readDonatorFile() ?? [];                           // in donator file
  if (cmd === "whitelist" && (sub === "remove" || sub === "rank")) {
    const f = interaction.options.getString("whitelist");
    return f ? (readFactionFile(SPAWN_FILE_MAP[f]) ?? []) : null;                                      // members of that faction
  }
  return null;   // default: online + known players
}


// ---- faction audit writer  (serialized) ----
function writeFactionAudit(entry) {
  return update(FILES.FACTION_AUDIT, [], (audit) => {
    audit.push({ ...entry, at: Date.now() });
    if (audit.length > 5_000) audit.splice(0, audit.length - 5_000);
    return audit;
  });
}

// ---- constants ----
const STAFF_MENU_ID = "0011110000000000101000000000010 11101101000001";
const MENUS = [
  { name: "Staff",      value: "staff",     menuId: STAFF_MENU_ID },
  // High Staff uses the SAME bit code as Staff, but the grant also runs AddMod + AddAccessManager.
  { name: "High Staff", value: "highstaff", menuId: STAFF_MENU_ID },
];

/* Self-service RCON-menu panel: a channel where staff enter their in-game name and
   the bot grants the menu that matches their HIGHEST Discord role. The role→menu
   mapping is set with /setrconroles (stored config), falling back to these env/defaults. */
const MENU_PANEL_CHANNEL  = process.env.MENU_PANEL_CHANNEL  || "1520598952670662677";
const MENU_ROLE_DEFAULTS = {
  highstaff: process.env.MENU_ROLE_HIGHSTAFF || "1521827868756152450",
  staff:     process.env.MENU_ROLE_STAFF     || "1520598947180314836",
};
/* RCON blacklist role - anyone holding it is barred from the self-serve menu
   panel, even if they also hold a menu role. */
const RCON_BLACKLIST_ROLE_ID = process.env.MENU_ROLE_BLACKLIST || "1520598947129852078";
// Effective mapping: stored /setrconroles config overrides the env defaults.
function loadMenuRoles() {
  const saved = safeRead(FILES.MENU_ROLES, {}) || {};
  return {
    highstaff: saved.highstaff || MENU_ROLE_DEFAULTS.highstaff,
    staff:     saved.staff     || MENU_ROLE_DEFAULTS.staff,
  };
}
function setMenuRole(menu, roleId) {
  return update(FILES.MENU_ROLES, {}, (m) => { if (roleId) m[menu] = roleId; else delete m[menu]; return m; });
}
// Highest role wins, in this order.
function menuRoleTiers() {
  const r = loadMenuRoles();
  return [
    { role: r.highstaff, menu: "highstaff" },
    { role: r.staff,     menu: "staff"     },
  ];
}

/* Punishment presets - each reason carries its own sentence, so a mod picks the
   offence and the duration is applied automatically (no manually-typed date, except
   "Other", which takes a custom unban date). Hard R is permanent. */
const DAY_MS = 86_400_000;
const PUNISHMENTS = [
  { name: "MassRDM in Protected Zone",     value: "massrdm_protected", ms: 3 * DAY_MS },
  { name: "Spawn Killing - Whitelist Spawn", value: "sk_faction",      ms: 5 * DAY_MS },
  { name: "Spawn Killing - Civ Spawn",     value: "sk_civ",            ms: 7 * DAY_MS },
  { name: "Hard R",                        value: "hard_r",            permanent: true },
  { name: "Soft A",                        value: "soft_a",            ms: 3 * DAY_MS },
  { name: "Slur",                          value: "slur",              ms: 1 * DAY_MS },
  { name: "Exploiting",                    value: "exploiting",        ms: 2 * DAY_MS },
  { name: "Harassment",                    value: "harassment",        ms: 1 * DAY_MS },
  { name: "ERP",                           value: "erp",               ms: 1 * DAY_MS },
  { name: "Sexually Explicit",             value: "sexually_explicit", ms: 7 * DAY_MS },
  { name: "Donator Abuse",                 value: "donator_abuse",     ms: 7 * DAY_MS, donatorSuspendMs: 14 * DAY_MS },
  { name: "Other",                         value: "other",             custom: true },
];
const PUNISH_BY_VALUE = Object.fromEntries(PUNISHMENTS.map(p => [p.value, p]));
// Human sentence for a punishment (embeds/logs): "Permanent", "1 Week", "3 Days", ...
function punishDurationLabel(p) {
  if (!p || p.custom) return "Custom";
  if (p.permanent) return "Permanent";
  const d = Math.round(p.ms / DAY_MS);
  return d % 7 === 0 ? `${d / 7} Week${d / 7 !== 1 ? "s" : ""}` : `${d} Day${d !== 1 ? "s" : ""}`;
}
// Slash-command choices, sentence shown in the label (Discord: ≤25 choices, ≤100 chars).
const PUNISH_CHOICES = PUNISHMENTS.map(p => ({
  name: `${p.name}${p.custom ? " (custom time)" : ` - ${punishDurationLabel(p)}`}`,
  value: p.value,
}));
const BAN_REASON_LABELS = Object.fromEntries(PUNISHMENTS.map(p => [p.value, p.name]));

const BAN_DURATIONS = {
  "1h":  { ms: 3_600_000,          label: "1 Hour"   },
  "6h":  { ms: 21_600_000,         label: "6 Hours"  },
  "1d":  { ms: 86_400_000,         label: "1 Day"    },
  "3d":  { ms: 259_200_000,        label: "3 Days"   },
  "5d":  { ms: 432_000_000,        label: "5 Days"   },
  "1w":  { ms: 604_800_000,        label: "1 Week"   },
  "2w":  { ms: 1_209_600_000,      label: "2 Weeks"  },
  "1mo": { ms: 2_592_000_000,      label: "1 Month"  },
  "3mo": { ms: 7_776_000_000,      label: "3 Months" },
  "6mo": { ms: 15_552_000_000,     label: "6 Months" },
  "1y":  { ms: 31_536_000_000,     label: "1 Year"   },
};

const LEADERBOARD_INTERVAL_MS = 30 * 1000;   // caps + playtime leaderboards refresh every 30s
const LEADERBOARD_TOP_N       = 30;
/* Channel the live player list auto-updates in, every 30s (override with PLAYERLIST_CHANNEL). */
const PLAYERLIST_CHANNEL      = process.env.PLAYERLIST_CHANNEL || "1520598950787158106";
const PLAYERLIST_INTERVAL_MS  = 30 * 1000;
const DASHBOARD_CHANNEL       = process.env.DASHBOARD_CHANNEL || "";
const DASHBOARD_INTERVAL_MS   = 30 * 1000;
const RCON_HEALTH_INTERVAL_MS = 5 * 60 * 1000;
/* Channel a short changelog posts to whenever the bot restarts on a new commit
   (override with UPDATE_LOG_CHANNEL). */
const UPDATE_LOG_CHANNEL      = process.env.UPDATE_LOG_CHANNEL || "1526601109362446377";
/* Channel every live PvP kill posts to, one clean line per kill
   (override with KILLFEED_CHANNEL). */
const KILLFEED_CHANNEL        = process.env.KILLFEED_CHANNEL || "1525801322262167623";

/* ---- verification ----
   VERIFY_CHANNEL: public channel with the Verify button (visible to unverified).
   VERIFY_STAFF_CHANNEL: private channel where staff accept/deny requests.
   VERIFIED_ROLE: the role granted on approval (grants channel access). The
   Unverified role is auto-created and its id stored in FILES.VERIFY_STATE. */
const VERIFY_CHANNEL         = process.env.VERIFY_CHANNEL       || "1529488781458014208";
const VERIFY_STAFF_CHANNEL   = process.env.VERIFY_STAFF_CHANNEL || "1529488962282983485";
const VERIFIED_ROLE          = process.env.VERIFIED_ROLE        || "1528754379417583668";
/* Channel every police-related log posts to - arrests, sentence releases,
   warrants, rank suspensions (override with POLICE_LOG_CHANNEL). ARREST_CHANNEL
   is an optional override just for arrest bookings; both fall back to MOD_LOG. */
const POLICE_LOG_CHANNEL     = process.env.POLICE_LOG_CHANNEL   || "1529535040168398979";
const ARREST_CHANNEL         = process.env.ARREST_CHANNEL       || "";

// ---- faction-specific rank system ----
// Rank registry (order/badges/rankFiles) extracted to ./factions/ranks.
const { FACTION_RANKS } = require("./factions/ranks");

function getFactionRankConfig(faction) { return FACTION_RANKS[faction] ?? null; }
function getFactionRankOrder(faction)  { return FACTION_RANKS[faction]?.order ?? []; }
function getFactionDefaultRank(faction){ return FACTION_RANKS[faction]?.default ?? "Recruit"; }
function getFactionRankBadge(faction, rank) { return FACTION_RANKS[faction]?.badges[rank] ?? ""; }

function rankBadge(faction, rank) {
  const badge = getFactionRankBadge(faction, rank);
  return `${badge}  **${rank ?? getFactionDefaultRank(faction)}**`;
}

function rankWeight(faction, rank) {
  const order = getFactionRankOrder(faction);
  const idx   = order.indexOf(rank);
  return idx === -1 ? -1 : idx;
}

// All ranks a player currently holds in a faction (a player may hold several),
// derived from the rank files (the source of truth), ordered low -> high.
function getPlayerRanks(faction, playerId) {
  const cfg = getFactionRankConfig(faction);
  if (!cfg) return [];
  const id = playerId.toLowerCase();
  return cfg.order.filter(r => {
    const f = cfg.rankFiles[r];
    if (!f || f === SPAWN_FILE_MAP[faction]) return false;   // spawn file is membership, not a rank
    const lines = readFactionFile(f);
    return lines && lines.some(l => l.toLowerCase() === id);
  });
}

// Sub-classes: extra designations (e.g. NYPD Detective / Vice Officer) a member can
// hold alongside their rank. Each is its own FactionRoles .txt, like a rank file.
function getFactionSubclasses(faction) {
  return getFactionRankConfig(faction)?.subclasses ?? {};
}
// The sub-classes a player currently holds in a faction (from the sub-class files).
function getPlayerSubclasses(faction, playerId) {
  const id = playerId.toLowerCase();
  return Object.entries(getFactionSubclasses(faction))
    .filter(([, file]) => { const lines = readFactionFile(file); return lines && lines.some(l => l.toLowerCase() === id); })
    .map(([name]) => name);
}

// The player's highest held rank (for sorting/weight/display), else the stored or default rank.
function getFactionRank(faction, playerId) {
  const held = getPlayerRanks(faction, playerId);
  if (held.length) return held[held.length - 1];
  const ranks = loadFactionRanks();
  return ranks[faction]?.[playerId.toLowerCase()] ?? getFactionDefaultRank(faction);
}

function setFactionRank(faction, playerId, rank) {
  return update(FILES.FACTION_RANKS, {}, (ranks) => {
    if (!ranks[faction]) ranks[faction] = {};
    ranks[faction][playerId.toLowerCase()] = rank;
    return ranks;
  });
}

function removeFactionRank(faction, playerId) {
  return update(FILES.FACTION_RANKS, {}, (ranks) => {
    if (ranks[faction]) delete ranks[faction][playerId.toLowerCase()];
    return ranks;
  });
}

function getFactionCap(faction) {
  const cfg = loadFactionConfig();
  return cfg[faction]?.cap ?? FACTION_DEFAULT_CAP;
}

function setFactionCap(faction, cap) {
  return update(FILES.FACTION_CONFIG, {}, (cfg) => {
    if (!cfg[faction]) cfg[faction] = {};
    cfg[faction].cap = cap;
    return cfg;
  });
}

/** Number of members currently holding `rank` in `faction`. */
function countFactionRank(faction, rank) {
  const members = getFactionMembers(faction);
  if (!members) return 0;
  return members.filter(m => m.rank === rank).length;
}

/* Pavlov server installs, kept in sync. Every game file the bot writes (bans,
   faction roles, donator, economy) is mirrored into EVERY install so all servers
   stay identical. By default we auto-detect every "pavlovserver*" directory next
   to PAVLOV_BASE_1 (pavlovserver, pavlovserver1, pavlovserver2, ...). Override with
   PAVLOV_BASES (comma/colon-separated absolute paths) or PAVLOV_BASE_1. */
const PAVLOV_BASE_1 = process.env.PAVLOV_BASE_1 || "/home/steam/pavlovserver";
function discoverPavlovBases() {
  const explicit = String(process.env.PAVLOV_BASES ?? "").split(/[,:]/).map(s => s.trim()).filter(Boolean);
  if (explicit.length) return [...new Set(explicit)];
  const parent = path.dirname(PAVLOV_BASE_1);
  const prefix = path.basename(PAVLOV_BASE_1);          // e.g. "pavlovserver"
  try {
    const dirs = fs.readdirSync(parent, { withFileTypes: true })
      .filter(d => (d.isDirectory() || d.isSymbolicLink()) && d.name.startsWith(prefix))
      .map(d => path.join(parent, d.name))
      .filter(p => { try { return fs.existsSync(path.join(p, "Pavlov")); } catch { return false; } });   // real installs only
    if (dirs.length) return [...new Set(dirs)].sort();
  } catch (err) { logger.warn("Sync", `install discovery failed: ${err.message}`); }
  return [PAVLOV_BASE_1];
}
const PAVLOV_BASES = discoverPavlovBases();   // resolved once at startup
logger.info("Sync", `Syncing ${PAVLOV_BASES.length} install(s): ${PAVLOV_BASES.map(b => path.basename(b)).join(", ")}`);

/* Startup sync sanity check - turn the silent no-op failure modes into a visible WARN
   so "sync isn't working" is diagnosable from the log instead of guesswork. */
(function checkSyncConfig() {
  if (process.env.MODSAVE_SYNC === "off") {
    logger.warn("Sync", "MODSAVE_SYNC=off - cross-install ModSave sync is DISABLED. Unset it in .env to enable.");
    return;
  }
  if (PAVLOV_BASES.length < 2) {
    logger.warn("Sync", `Cross-install sync is a NO-OP with only ${PAVLOV_BASES.length} install (listed above). If you run multiple installs, set them explicitly: PAVLOV_BASES=/home/steam/pavlovserver,/home/steam/pavlovserver1,/home/steam/pavlovserver2`);
  }
  const mp = process.env.MODSAVE_PATH;
  if (mp && !PAVLOV_BASES.some(b => mp === b || mp.startsWith(b + path.sep))) {
    logger.warn("Sync", `MODSAVE_PATH (${mp}) is NOT under any install base - per-player balance writes will not be mirrored to other installs. Point it inside one of: ${PAVLOV_BASES.join(", ")}`);
  }
  // Read-only probe of each install's ModSave dir - a permission problem is a common
  // silent cause (the bot user can't write into another install owned by steam). This
  // must NOT create anything: a diagnostic that mkdir's would mask a missing dir and
  // could seed ModSave trees in a mis-discovered install.
  for (const base of PAVLOV_BASES) {
    const dir = path.join(base, "Pavlov", "Saved", "Config", "ModSave");   // = MODSAVE_REL (defined below; inlined to avoid TDZ)
    try {
      fs.accessSync(dir, fs.constants.W_OK);   // exists and writable - good
    } catch (err) {
      if (err.code === "ENOENT") {
        // Not created yet - fine, as long as the bot could create it (parent writable).
        try { fs.accessSync(path.dirname(dir), fs.constants.W_OK); }
        catch (e2) { logger.warn("Sync", `Cannot create ModSave dir for ${path.basename(base)} (${dir}): parent not writable (${e2.code || e2.message}).`); }
      } else {
        logger.warn("Sync", `ModSave dir not writable for ${path.basename(base)} (${dir}): ${err.code || err.message} - sync into this install will fail. Check ownership/permissions.`);
      }
    }
  }
})();

// Given a path under one install, return it plus the equivalent path in every other install.
function mirrorPaths(p) {
  const out = new Set([p]);
  const src = PAVLOV_BASES.find(b => p === b || p.startsWith(b + path.sep));
  if (src) for (const b of PAVLOV_BASES) if (b !== src) out.add(b + p.slice(src.length));
  return [...out];
}

// Atomic write: write a temp file then rename over the target. rename(2) is atomic
// on the same filesystem, so a crash/kill mid-write can never leave the game server
function atomicWriteFile(fp, content) {
  try { if (fs.readFileSync(fp, "utf8") === content) { matchTreeOwner(fp); return true; } } catch {}   // already up to date - don't rewrite, but still heal ownership
  const tmp = `${fp}.tmp.${process.pid}.${Date.now()}`;
  try {
    fs.mkdirSync(path.dirname(fp), { recursive: true });
    fs.writeFileSync(tmp, content, "utf8");
    fs.renameSync(tmp, fp);          // atomic swap - never a torn file
    matchTreeOwner(fp);
    return true;
  } catch (err) {
    try { fs.unlinkSync(tmp); } catch {}
    logger.error("Sync", `write failed for ${fp}: ${err.message}`);
    return false;
  }
}
// Write a game file into EVERY install (atomic, dirs created, ownership fixed). True if any succeeded.
function writeGameFile(primaryPath, content) {
  let ok = false;
  for (const fp of mirrorPaths(primaryPath)) if (atomicWriteFile(fp, content)) ok = true;
  return ok;
}
// Write one exact path atomically (blacklist helpers loop the installs themselves).
const writeGameFileSingle = atomicWriteFile;

/* ---------------- whole-ModSave sync across installs ----------------
   Keep the ENTIRE ModSave tree (economy, stats, faction roles, ban-message file,
   any custom gamemode saves) identical across every install. Convergent newest-
   wins per file: the most recently modified copy of each file is propagated to
   every other install. Binary-safe, atomic, ownership-preserving, and it NEVER
   deletes anything. Set MODSAVE_SYNC=off to disable. */
const MODSAVE_REL = path.join("Pavlov", "Saved", "Config", "ModSave");
const MODSAVE_SYNC_INTERVAL_MS = 60 * 1000;   // blanket newest-wins net; per-player sync also fires on join/disconnect
// NEVER sync RCON+ menu-access files. Those are managed live by GiveMenu / RemoveMenu
// / ClearMenuAccess; blindly mirroring them (newest-wins) can wipe a player's menu
const MODSAVE_SYNC_SKIP = new RegExp(
  ["menuaccess", "accessmanager", "rconplus", "rcon_plus",
   ...String(process.env.MODSAVE_SYNC_SKIP_EXTRA ?? "").split(",").map(s => s.trim()).filter(Boolean)]
    .map(s => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|"),
  "i");

// Recursively list every file under root, as paths relative to base.
function listFilesRec(root, base = root, out = []) {
  let entries;
  try { entries = fs.readdirSync(root, { withFileTypes: true }); } catch { return out; }
  for (const e of entries) {
    const abs = path.join(root, e.name);
    if (e.isDirectory()) listFilesRec(abs, base, out);
    else if (e.isFile()) out.push(path.relative(base, abs));
  }
  return out;
}
// Atomic, binary-safe copy that PRESERVES the source mtime - so once two installs
// hold the same file they compare equal and the sync converges (no ping-pong).
function atomicCopyPreservingMtime(dstAbs, contentBuf, mtimeMs) {
  const tmp = `${dstAbs}.tmp.${process.pid}.${Date.now()}`;
  try {
    fs.mkdirSync(path.dirname(dstAbs), { recursive: true });
    fs.writeFileSync(tmp, contentBuf);
    fs.renameSync(tmp, dstAbs);
    try { fs.utimesSync(dstAbs, new Date(), new Date(mtimeMs)); } catch {}
    matchTreeOwner(dstAbs);
    return true;
  } catch (err) {
    try { fs.unlinkSync(tmp); } catch {}
    logger.error("Sync", `ModSave copy failed for ${dstAbs}: ${err.message}`);
    return false;
  }
}
// True when copying `srcContent` over `dstAbs` would replace a positive balance with
// nothing (empty or "0") - a money-losing clobber we must refuse.
function wouldWipeBalance(srcContent, dstAbs) {
  const s = Buffer.isBuffer(srcContent) ? srcContent.toString("utf8").trim() : String(srcContent).trim();
  if (s !== "" && s !== "0") return false;                    // source has real content - fine to copy
  let d; try { d = fs.readFileSync(dstAbs, "utf8").trim(); } catch { return false; }
  return /^\d+$/.test(d) && Number(d) > 0;                    // destination is a positive integer balance - protect it
}
// A bare non-negative integer is what a caps ledger file looks like (see
// getPlayerFilePath/writePlayerBalance) - used to spot ledger files by content
// rather than by guessing their subfolder, since that's mod-specific.
function looksLikeLedgerEntry(buf) {
  const s = Buffer.isBuffer(buf) ? buf.toString("utf8").trim() : String(buf).trim();
  return /^\d+$/.test(s);
}
function isPlayerOnline(name) {
  const key = String(name ?? "").toLowerCase();
  return allCachedPlayers().some(n => n.toLowerCase() === key);
}
function syncAllModSave(bases = PAVLOV_BASES) {
  if (process.env.MODSAVE_SYNC === "off") return { synced: 0, installs: bases.length, off: true };
  if (!Array.isArray(bases) || bases.length < 2) return { synced: 0, installs: bases.length };
  // Find the newest version of every relative file path across all installs.
  const newest = new Map();   // rel -> { mtime, srcBase }
  for (const base of bases) {
    const root = path.join(base, MODSAVE_REL);
    for (const rel of listFilesRec(root)) {
      if (MODSAVE_SYNC_SKIP.test(rel)) continue;             // never mirror menu-access files
      let m; try { m = fs.statSync(path.join(root, rel)).mtimeMs; } catch { continue; }
      const cur = newest.get(rel);
      if (!cur || m > cur.mtime) newest.set(rel, { mtime: m, srcBase: base });
    }
  }
  let synced = 0;
  for (const [rel, { mtime, srcBase }] of newest) {
    let content;
    try { content = fs.readFileSync(path.join(srcBase, MODSAVE_REL, rel)); } catch { continue; }   // Buffer (binary-safe)
    // ONLINE-PLAYER GUARD: a currently-connected player's ledger is still changing in
    // server memory, so "which install's copy has the newest mtime" can't reliably tell
    // us which one is actually more current - an unrelated autosave on one server can
    // stamp a stale balance with a fresher mtime than the other server's correct one,
    // clobbering it. The event-driven join/disconnect sync (syncPlayerLedger) already
    // handles online players correctly; this blanket sweep is only a safety net for
    // everyone else, so skip ledger files for anyone currently online anywhere.
    if (looksLikeLedgerEntry(content) && isPlayerOnline(path.basename(rel, path.extname(rel)))) continue;
    for (const base of bases) {
      if (base === srcBase) continue;
      const dstAbs = path.join(base, MODSAVE_REL, rel);
      let dm = -1; try { dm = fs.statSync(dstAbs).mtimeMs; } catch {}
      if (dm >= mtime - 2000) continue;          // already as new (2s tolerance for fs precision) - skip
      // MONEY GUARD: never overwrite a positive balance with an empty/0 file. A player
      // hopping to a server that just made them a fresh 0-cap file must not wipe the
      // caps they earned elsewhere. (Economy files are a single integer.)
      if (wouldWipeBalance(content, dstAbs)) continue;
      if (atomicCopyPreservingMtime(dstAbs, content, mtime)) synced++;
    }
  }
  if (synced) logger.info("Sync", `ModSave sync - propagated ${synced} file copy(ies) across ${bases.length} installs`);
  return { synced, installs: bases.length };
}

/* Targeted, EVENT-DRIVEN caps sync for one player. The blanket newest-wins pass runs
   on an interval, which is too slow for server hoppers: leave server 1, join server 2
   inside the window, and server 2 loads a stale ledger - then saves it later with a
   newer mtime, clobbering the caps earned on server 1. So we sync the player's ledger
   at the exact moments it matters: right after a disconnect (their balance was just
   saved) and right on join (before the destination server reads it). */
function syncPlayerLedger(name, pathsOverride = null) {
  if (process.env.MODSAVE_SYNC === "off") return 0;
  let paths = pathsOverride;
  if (!paths) {
    const fp = getPlayerFilePath(name);
    if (!fp || PAVLOV_BASES.length < 2) return 0;
    paths = mirrorPaths(fp);
  }
  if (paths.length < 2) return 0;
  let newest = null;
  for (const p of paths) {
    try { const st = fs.statSync(p); if (!newest || st.mtimeMs > newest.mtime) newest = { p, mtime: st.mtimeMs }; } catch {}
  }
  if (!newest) return 0;                        // no ledger anywhere yet
  let content; try { content = fs.readFileSync(newest.p); } catch { return 0; }
  let synced = 0;
  for (const p of paths) {
    if (p === newest.p) continue;
    let dm = -1; try { dm = fs.statSync(p).mtimeMs; } catch {}
    if (dm >= newest.mtime - 2000) continue;    // already as new
    if (wouldWipeBalance(content, p)) continue; // same 0-wipe guard as the blanket sync
    if (atomicCopyPreservingMtime(p, content, newest.mtime)) synced++;
  }
  if (synced) logger.info("Sync", `Credits ledger for ${name} → ${synced} install(s)`);
  return synced;
}

const FACTION_ROLES_PATH  = path.join(PAVLOV_BASE_1, "Pavlov/Saved/Config/ModSave/FactionRoles");
const FACTION_DEFAULT_CAP = 50;

/* ---------------- blacklist.txt (Pavlov Shack name blacklist), synced ---------------- */
// One name per line, kept identical across every install. Names may contain spaces
// (e.g. "Butter Life"), so we preserve them - only control chars are stripped.
const blacklistPathFor = (base) => path.join(base, "Pavlov/Saved/Config/blacklist.txt");
function sanitizeBanName(raw) { return String(raw ?? "").replace(/[\r\n\t]+/g, " ").trim().slice(0, 80); }
function readBlacklist(base) {
  try { return fs.readFileSync(blacklistPathFor(base), "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean); }
  catch (err) { if (err.code === "ENOENT") { ensureFile(blacklistPathFor(base), ""); return []; } logger.error("Blacklist", `read ${base}: ${err.message}`); return null; }
}
// Per-install read status for diagnostics (banlist surfaces this so a permission/path
// failure is visible instead of looking like an empty ban list).
function blacklistStatus() {
  return PAVLOV_BASES.map(base => {
    const fp = blacklistPathFor(base);
    try { const n = fs.readFileSync(fp, "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean).length; return { base, fp, count: n, error: null }; }
    catch (err) { return { base, fp, count: 0, error: err.code === "ENOENT" ? "missing" : (err.code || err.message) }; }
  });
}
// Every blacklisted name across all installs (deduped, case-insensitive, original casing kept).
function blacklistAll() {
  const map = new Map();
  for (const base of PAVLOV_BASES) for (const n of (readBlacklist(base) || [])) map.set(n.toLowerCase(), n);
  return [...map.values()];
}
// Cached union for the autocomplete hot path (fires per keystroke) - avoids reading
// every install's blacklist.txt off disk on every character typed. ~3s staleness is fine.
let _blAllCache = { ts: 0, names: [] };
function blacklistAllCached() {
  if (Date.now() - _blAllCache.ts < 3000) return _blAllCache.names;
  _blAllCache = { ts: Date.now(), names: blacklistAll() };
  return _blAllCache.names;
}
// Which server numbers currently list this name.
function blacklistHas(name) {
  const nm = sanitizeBanName(name).toLowerCase();
  const hits = [];
  PAVLOV_BASES.forEach((base, i) => { const c = readBlacklist(base); if (c && c.some(x => x.toLowerCase() === nm)) hits.push(i + 1); });
  return hits;
}
// Add a name to blacklist.txt on EVERY install (synced). Skips installs already listing it (no rewrite).
function blacklistAdd(name) {
  const nm = sanitizeBanName(name);
  let servers = 0;
  if (!nm) return { name: nm, servers };
  for (const base of PAVLOV_BASES) {
    const cur = readBlacklist(base); if (cur === null) continue;
    if (cur.some(x => x.toLowerCase() === nm.toLowerCase())) { servers++; continue; }   // already present - skip
    cur.push(nm);
    if (writeGameFileSingle(blacklistPathFor(base), cur.join("\n") + "\n")) servers++;
  }
  return { name: nm, servers };
}
// Remove a name from blacklist.txt on every install. Returns { name, removed } (# of installs changed).
function blacklistRemove(name) {
  const nm = sanitizeBanName(name);
  let removed = 0;
  for (const base of PAVLOV_BASES) {
    const cur = readBlacklist(base); if (cur === null) continue;
    const next = cur.filter(x => x.toLowerCase() !== nm.toLowerCase());
    if (next.length !== cur.length && writeGameFileSingle(blacklistPathFor(base), next.join("\n") + "\n")) removed++;
  }
  return { name: nm, removed };
}
// Heal divergence: make both installs' blacklist.txt equal their union. Run on startup,
// so names already on only one server (or bans made directly on one) propagate to both.
function reconcileBlacklists() {
  const union = blacklistAll();
  let wrote = 0;
  for (const base of PAVLOV_BASES) {
    const cur = readBlacklist(base); if (cur === null) continue;
    const set = new Set(cur.map(x => x.toLowerCase()));
    const same = cur.length === union.length && union.every(n => set.has(n.toLowerCase()));
    if (!same && writeGameFileSingle(blacklistPathFor(base), union.join("\n") + "\n")) wrote++;
  }
  if (wrote) logger.info("Blacklist", `Reconciled blacklist.txt across ${wrote} install(s) - ${union.length} name(s)`);
}


/* Donator whitelist file. Lives in the FactionRoles dir alongside the other
   whitelist files. Override the exact path/filename with the DONATOR_PATH env. */
const DONATOR_FILE = process.env.DONATOR_PATH
  || path.join(FACTION_ROLES_PATH, "donator.txt");

// Faction name -> its spawn-file name in FactionRoles/.
const SPAWN_FILE_MAP = {
  "Gambino": "gambinospawn.txt",
  "Colombo": "colombospawn.txt",
  "NYPD":    "policespawn.txt",
};

// Reverse lookup: spawn-file basename (no .txt) -> faction name, used by the log tailer.
const FACTION_SPAWN_MAP = {
  gambinospawn: "Gambino",
  colombospawn: "Colombo",
  policespawn:  "NYPD",
};

const ALL_FACTIONS = Object.keys(SPAWN_FILE_MAP);

/* The "faction.txt" master roster the gamemode reads, plus any other plain
   FactionRoles files the bot never writes but the server still expects to
   exist. These are created empty if missing (never overwritten). */
const EXTRA_FACTION_FILES = ["faction.txt"];

/* Build every FactionRoles whitelist file on startup so the game server always
   finds them, even on a brand-new install. We only CREATE missing files (empty)
   - never touch existing rosters - and we do it in EVERY Pavlov install via
   mirrorPaths. Covers: faction.txt, each faction's spawn (membership) file, and
   every rank file, plus the donator file. */
function ensureFactionFiles() {
  const names = new Set(EXTRA_FACTION_FILES);
  for (const faction of ALL_FACTIONS) {
    if (SPAWN_FILE_MAP[faction]) names.add(SPAWN_FILE_MAP[faction]);
    const cfg = getFactionRankConfig(faction);
    if (cfg && cfg.rankFiles) for (const file of Object.values(cfg.rankFiles)) if (file) names.add(file);
    if (cfg && cfg.subclasses) for (const file of Object.values(cfg.subclasses)) if (file) names.add(file);
  }
  // donator file may live outside FactionRoles (DONATOR_PATH) - handle separately
  let created = 0;
  for (const name of names) {
    for (const fp of mirrorPaths(path.join(FACTION_ROLES_PATH, name))) {
      if (ensureFile(fp, "")) created++;
    }
  }
  for (const fp of mirrorPaths(DONATOR_FILE)) if (ensureFile(fp, "")) created++;
  logger.info("Init", `Whitelist files ensured across ${PAVLOV_BASES.length} install(s)` + (created ? ` - created ${created} missing` : ""));
}

/* One-time migration cleanup, run on every startup (idempotent): the server may
   still hold whitelist files from the OLD Fallout factions (before Gambino /
   Colombo / NYPD). Delete those exact known files from FactionRoles in every
   install. Only these names are touched - current rosters, faction.txt, and the
   donator file are never removed - and a file a CURRENT faction uses is skipped. */
const OBSOLETE_FACTION_FILES = [
  "ncrspawn.txt", "ncrprivate.txt", "ncrcorporal.txt", "ncrsergeant.txt", "ncrmedix.txt", "ncrheavy.txt", "ncrpowerarmor.txt", "ncrmp.txt", "ncrranger.txt", "ncrlieutenant.txt", "ncrofficer.txt",
  "legionspawn.txt", "legionrecruit.txt", "legionlegionnaire.txt", "legionexplorer.txt", "legionslavemaster.txt", "legionprimelegionary.txt", "legionveteranlegionnaire.txt", "legionvexalarius.txt", "legioncenturion.txt", "legionassasin.txt", "legionpraetorian.txt", "legionlegate.txt",
  "enclavespawn.txt", "enclavebrigadeless.txt", "enclavetruthandjustice.txt", "enclaveresearch.txt", "enclave56thriflebrigade.txt", "enclaverecon.txt", "enclavedemolition.txt", "enclavemechanized.txt", "enclavehellfire.txt", "enclaveofficer.txt",
  "khansspawn.txt", "khanspawn.txt", "khansprospect.txt", "khansenforcer.txt", "khansbeserker.txt", "khansseargent.txt", "khansskirmisher.txt", "khansmarksmen.txt",
  "bosspawn.txt", "bosinitiate.txt", "bosknight.txt", "bospaladin.txt", "boselder.txt",
  "kingsspawn.txt", "kingsprospect.txt", "kingssilverace.txt", "kingsguard.txt", "kingshighroller.txt", "kingscrown.txt", "kingstheking.txt",
  "supermutantspawn.txt", "supermutantsuicider.txt", "supermutantinfantry.txt", "supermutantsergeant.txt", "supermutantnightkin.txt", "supermutantbombardier.txt", "supermutantbehemoth.txt",
  "followersspawn.txt", "followersfollower.txt", "followersguard.txt", "followersrecruit.txt", "followersscholar.txt", "followersdirector.txt",
];
function pruneObsoleteFactionFiles() {
  // Defensive: never delete a file a CURRENT faction actually uses (no overlap today).
  const inUse = new Set();
  for (const f of ALL_FACTIONS) {
    if (SPAWN_FILE_MAP[f]) inUse.add(SPAWN_FILE_MAP[f]);
    const cfg = getFactionRankConfig(f);
    if (cfg?.rankFiles)  for (const rf of Object.values(cfg.rankFiles))  inUse.add(rf);
    if (cfg?.subclasses) for (const sf of Object.values(cfg.subclasses)) inUse.add(sf);
  }
  let removed = 0;
  for (const name of OBSOLETE_FACTION_FILES) {
    if (inUse.has(name)) continue;
    for (const fp of mirrorPaths(path.join(FACTION_ROLES_PATH, name))) {
      try { fs.unlinkSync(fp); removed++; }
      catch (e) { if (e.code !== "ENOENT") logger.warn("Prune", `could not delete ${fp}: ${e.message}`); }
    }
  }
  if (removed) logger.info("Init", `Pruned ${removed} obsolete Fallout faction file(s) from FactionRoles`);
  return removed;
}

// One-time startup sweep: hand any already-root-owned files THE BOT WRITES back to
// the game-server user, so accumulated root ownership self-heals on restart.
function healTreeOwnership() {
  try {
    if (!process.getuid || process.getuid() !== 0) return;   // only root can chown
    let fixed = 0;
    const heal = (fp) => {
      try {
        const before = fs.statSync(fp);
        matchTreeOwner(fp);                                  // climbs to the real (non-root) owner of the tree
        const after = fs.statSync(fp);
        if (after.uid !== before.uid || after.gid !== before.gid) fixed++;
      } catch {}
    };
    // Specific bot-written files (the server only READS these).
    const files = new Set();
    for (const base of PAVLOV_BASES) files.add(blacklistPathFor(base));   // Config/blacklist.txt
    for (const fp of mirrorPaths(modsaveBanlistPath())) files.add(fp);    // ModSave/banlist.txt
    for (const fp of mirrorPaths(DONATOR_FILE)) files.add(fp);            // donator whitelist
    for (const fp of files) if (fp && fs.existsSync(fp)) heal(fp);
    // Wholly bot-managed directories - every file in them is written by the bot.
    const dirs = new Set(mirrorPaths(FACTION_ROLES_PATH));               // faction whitelist files
    const mp = getModsavePath(); if (mp) dirs.add(mp);                   // economy balance files
    for (const dir of dirs) {
      let names; try { names = fs.readdirSync(dir); } catch { continue; }
      for (const name of names) {
        const fp = path.join(dir, name);
        let st; try { st = fs.statSync(fp); } catch { continue; }
        if (st.isFile()) heal(fp);
      }
    }
    if (fixed) logger.info("Init", `Ownership heal: returned ${fixed} bot-written file(s) to the game-server user`);
  } catch (e) { logger.warn("Init", `ownership heal failed: ${e.message}`); }
}

// ---- visual theme + embed builders (RP-neutral) ----
// Extracted to ./discord/theme.js. getClient is lazy so this module loads before
// the Discord client exists; the avatar/version footer resolve at send time.
const {
  NV, CLIN, QUOTES, randomQuote,
  DIVIDER, RULE, BRAND_NAME, GLYPH,
  brandIcon, brand, clampEmbed,
  bar, meter, pip, cell, hero, clinical,
  successEmbed, errorEmbed, warningEmbed, deniedEmbed,
  adminOnlyEmbed, ownerOnlyEmbed, modOnlyEmbed,
  factionLeaderOnlyEmbed, factionLeaderStrictEmbed, policeOnlyEmbed,
  blacklistedEmbed, emptyIdEmbed, rateLimitEmbed,
} = require("./discord/theme")({ getClient: () => client, buildId: BUILD_ID });

// ---- rate limiter ----
const rateLimits = new Map();
function checkRateLimit(userId, command, limitMs = 3000) {
  const key = `${userId}:${command}`;
  const now = Date.now();
  const last = rateLimits.get(key) ?? 0;
  if (now - last < limitMs) return false;
  rateLimits.set(key, now);
  return true;
}
setInterval(() => {
  const cutoff = Date.now() - 30_000;
  for (const [k, v] of rateLimits) if (v < cutoff) rateLimits.delete(k);
}, 600_000);

// ---- input sanitization + pure helpers (extracted to ./utils) ----
const {
  sanitizeId, sanitizeMessage, md5, formatTimeLeft,
  parseDuration, easternClock, parseClockTime, redactPrivateInfo,
} = require("./utils");

// ---- role checks ----
// Role test that survives a null member (DM invocation) and both member shapes:
// a GuildMember (roles.cache collection) or the raw interaction payload (array).
function _hasRole(member, roleId) {
  if (!member || !roleId) return false;
  const r = member.roles;
  if (r?.cache && typeof r.cache.has === "function") return r.cache.has(roleId);
  if (Array.isArray(r)) return r.includes(roleId);
  return false;
}
function hasAdminRole(member) {
  if (isOwner(member?.id ?? member?.user?.id)) return true;
  const { adminRoleId } = loadRoles();
  if (!adminRoleId) return !!member;              // unrestricted only for real guild members, never DMs
  return _hasRole(member, adminRoleId);
}
function hasModRole(member) {
  if (isOwner(member?.id ?? member?.user?.id)) return true;
  const { modRoleId, adminRoleId } = loadRoles();
  if (_hasRole(member, adminRoleId)) return true;   // admins outrank mods
  if (!modRoleId) return !!member;
  return _hasRole(member, modRoleId);
}
function hasFactionLeaderRole(member) {
  if (isOwner(member?.id ?? member?.user?.id)) return true;
  const { factionLeaderRoleId } = loadRoles();
  return _hasRole(member, factionLeaderRoleId);
}
function hasPoliceRole(member) {
  if (isOwner(member?.id ?? member?.user?.id)) return true;
  const { policeRoleId, adminRoleId } = loadRoles();
  if (_hasRole(member, adminRoleId)) return true;   // admins can manage warrants too
  return _hasRole(member, policeRoleId);
}
// Per-faction whitelist management: the general Whitelist Leader role manages every
// whitelist; a faction-specific role (Gambino/Colombo) manages only its own.
// Owners/admins/mods are handled by the caller (they pass hasModRole).
const FACTION_ROLE_KEY = { "Gambino": "gambinoRoleId", "Colombo": "colomboRoleId", "NYPD": "nypdRoleId" };
function hasWhitelistManageRole(member, faction) {
  if (hasFactionLeaderRole(member)) return true;   // general whitelist leader (and owners)
  const roleId = loadRoles()[FACTION_ROLE_KEY[faction]];
  return _hasRole(member, roleId);
}

// ---- command blacklist  (discord users barred from all bot commands) ----
const UNBARRED_IDS = new Set((safeRead(FILES.USER_UNBARRED, []) || []).map(String));
const BLACKLIST_IDS = new Set([
  ...String(process.env.BLACKLIST_IDS ?? "").split(/[\s,]+/).map(s => s.trim()).filter(Boolean),
  ...(safeRead(FILES.USER_BLACKLIST, []) || []).map(String),
].filter(id => !UNBARRED_IDS.has(id)));   // anything explicitly un-barred stays un-barred
function isBlacklisted(userId)  { return BLACKLIST_IDS.has(String(userId)); }
function saveUserBlacklist()    { safeWrite(FILES.USER_BLACKLIST, [...BLACKLIST_IDS]); }
function saveUnbarred()         { safeWrite(FILES.USER_UNBARRED, [...UNBARRED_IDS]); }
function addUserBlacklist(id)   {
  id = String(id).trim();
  const added = !!id && !BLACKLIST_IDS.has(id);
  if (added) { BLACKLIST_IDS.add(id); UNBARRED_IDS.delete(id); saveUserBlacklist(); saveUnbarred(); }
  return added;
}
function removeUserBlacklist(id) {
  id = String(id).trim();
  const removed = BLACKLIST_IDS.delete(id);
  // remember the un-bar even if they were only in the env seed, so it sticks on restart
  if (id && !UNBARRED_IDS.has(id)) { UNBARRED_IDS.add(id); saveUnbarred(); }
  if (removed) saveUserBlacklist();
  return removed || !!id;
}

// ---- utility helpers ----
function formatKD(playerId) {
  let k = null;
  try { k = ipBans.getKD(playerId); } catch {}
  if (!k || !(k.kills + k.deaths)) return "*No record*";
  const ratio = (k.deaths ? k.kills / k.deaths : k.kills).toFixed(2);
  return `**${k.kills}** / **${k.deaths}**  -  ${ratio}`;
}

function formatPlaytime(minutes) {
  if (minutes < 60)  return `${minutes}m`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h < 24) return `${h}h ${m}m`;
  const d = Math.floor(h / 24);
  return `${d}d ${h % 24}h ${m}m`;
}

function formatUptime(ms) {
  const s   = Math.floor(ms / 1000);
  const min = Math.floor(s / 60) % 60;
  const h   = Math.floor(s / 3600) % 24;
  const d   = Math.floor(s / 86400);
  if (d > 0) return `${d}d ${h}h ${min}m`;
  if (h > 0) return `${h}h ${min}m`;
  return `${min}m`;
}

function serverLabel(server) {
  return server === "server2" ? "Server 2" : server === "server3" ? "Server 3" : server === "both" ? "All Servers" : "Server 1";
}
function serverEmoji() { return ""; }   // emoji-free; servers shown via serverLabel()

function chunkFields(lines, firstLabel, contLabel = firstLabel + " (cont.)", maxLen = 1020) {
  const fields = [];
  let block = "", chunk = 1;
  for (const line of lines) {
    const next = block ? `${block}\n${line}` : line;
    if (next.length > maxLen) {
      fields.push({ name: chunk === 1 ? firstLabel : contLabel, value: block });
      block = line; chunk++;
    } else { block = next; }
  }
  if (block) fields.push({ name: chunk === 1 ? firstLabel : contLabel, value: block });
  return fields;
}

/** Split an array of single-line strings into pages of at most `perPage`. */
function splitPages(lines, perPage) {
  const pages = [];
  for (let i = 0; i < lines.length; i += perPage) pages.push(lines.slice(i, i + perPage));
  return pages.length ? pages : [[]];
}

// ---- interactive paginator ----
// Reusable confirm/cancel dialog. Returns true if confirmed, false otherwise
// (cancel, timeout, or a non-owner clicking). Only the invoker can answer.
async function confirmDialog(interaction, { title, body, confirmLabel = "Confirm", danger = true, idleMs = 30_000 } = {}) {
  const row = new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("cd_yes").setLabel(confirmLabel).setStyle(danger ? ButtonStyle.Danger : ButtonStyle.Success),
    new ButtonBuilder().setCustomId("cd_no").setLabel("Cancel").setStyle(ButtonStyle.Secondary),
  );
  const embed = brand(new EmbedBuilder().setColor(danger ? NV.RUST_RED : NV.AMBER).setTitle(title)
    .setDescription(`${body}\n\n-# This prompt expires in ${Math.round(idleMs / 1000)}s.`));
  const payload = { embeds: [embed], components: [row], keepEmbeds: true, flags: MessageFlags.Ephemeral };
  if (interaction.deferred || interaction.replied) await interaction.editReply(payload);
  else await interaction.reply(payload);
  const msg = await interaction.fetchReply();
  let btn;
  try { btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: idleMs, filter: i => i.user.id === interaction.user.id }); }
  catch { try { await interaction.editReply({ components: [] }); } catch {} return false; }
  const yes = btn.customId === "cd_yes";
  await btn.update({ embeds: [brand(new EmbedBuilder().setColor(yes ? NV.IRRAD_GREEN : NV.DEAD_GREY)
    .setTitle(yes ? "Confirmed" : "Cancelled").setDescription(yes ? "Proceeding..." : "No changes made."))], components: [], keepEmbeds: true }).catch(() => {});
  return yes;
}

/* Waits for a button click from EXACTLY ownerId before `deadline` (an absolute
   Date.now()-style timestamp, not a duration - so a bystander repeatedly mashing
   the buttons can't keep extending the clock). Anyone else who clicks gets a clear
   ephemeral "not your game" reply instead of Discord's silent "interaction failed",
   and the wait continues. Returns the accepted interaction, or null on timeout. */
async function awaitOwnedComponent(msg, ownerId, deadline, notYoursText = "This isn't your game.") {
  for (;;) {
    const remaining = deadline - Date.now();
    if (remaining <= 0) return null;
    let btn;
    try { btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: remaining }); }
    catch { return null; }
    if (btn.user.id !== ownerId) {
      await btn.reply({ content: notYoursText, flags: MessageFlags.Ephemeral }).catch(() => {});
      continue;
    }
    return btn;
  }
}

async function paginate(interaction, lines, buildEmbed, { perPage = 12, ephemeral = false, idleMs = 120_000 } = {}) {
  let filter = "";
  let page   = 0;
  const pagesOf = () => {
    const active = filter ? lines.filter(l => l.toLowerCase().includes(filter.toLowerCase())) : lines;
    const pages  = splitPages(active.length ? active : [filter ? `*No entries match \`${filter}\`.*` : "*Nothing here.*"], perPage);
    return pages;
  };
  const controls = (p, total) => {
    const rows = [new ActionRowBuilder().addComponents(
      new ButtonBuilder().setCustomId("pg_first").setLabel("<<").setStyle(ButtonStyle.Secondary).setDisabled(p === 0),
      new ButtonBuilder().setCustomId("pg_prev").setLabel("< Prev").setStyle(ButtonStyle.Primary).setDisabled(p === 0),
      new ButtonBuilder().setCustomId("pg_ind").setLabel(`${p + 1} / ${total}`).setStyle(ButtonStyle.Secondary).setDisabled(true),
      new ButtonBuilder().setCustomId("pg_next").setLabel("Next >").setStyle(ButtonStyle.Primary).setDisabled(p >= total - 1),
      new ButtonBuilder().setCustomId("pg_last").setLabel(">>").setStyle(ButtonStyle.Secondary).setDisabled(p >= total - 1),
    )];
    const extras = [new ButtonBuilder().setCustomId("pg_search")
      .setLabel(filter ? `Search: "${filter}" (tap to clear)` : "Search")
      .setStyle(filter ? ButtonStyle.Success : ButtonStyle.Secondary)];
    const row2 = new ActionRowBuilder().addComponents(...extras);
    rows.push(row2);
    if (total >= 4) {
      rows.push(new ActionRowBuilder().addComponents(
        new StringSelectMenuBuilder().setCustomId("pg_jump").setPlaceholder("Jump to page...")
          .addOptions(Array.from({ length: Math.min(total, 25) }, (_, i) => ({ label: `Page ${i + 1} of ${total}`, value: String(i) })))));
    }
    return rows;
  };
  const render = () => {
    const pages = pagesOf();
    const total = pages.length;
    if (page >= total) page = total - 1;
    return { embeds: [brand(buildEmbed(pages[page], page, total))], components: (total > 1 || filter) ? controls(page, total) : [], keepEmbeds: true };
  };

  if (interaction.deferred || interaction.replied) await interaction.editReply(render());
  else await interaction.reply({ ...render(), ...(ephemeral ? { flags: MessageFlags.Ephemeral } : {}) });
  if (pagesOf().length <= 1 && !lines.some(l => true)) return;
  if (pagesOf().length <= 1) { /* still keep the Search control alive when it could matter */ }

  const msg = await interaction.fetchReply();
  for (;;) {
    let sel;
    try {
      sel = await msg.awaitMessageComponent({ time: idleMs, filter: i => i.user.id === interaction.user.id });
    } catch {
      try { await interaction.editReply({ components: [] }); } catch {}
      return;
    }
    const total = pagesOf().length;
    if (sel.customId === "pg_first")      { page = 0; await sel.update(render()); }
    else if (sel.customId === "pg_prev")  { page = Math.max(0, page - 1); await sel.update(render()); }
    else if (sel.customId === "pg_next")  { page = Math.min(total - 1, page + 1); await sel.update(render()); }
    else if (sel.customId === "pg_last")  { page = total - 1; await sel.update(render()); }
    else if (sel.customId === "pg_jump")  { page = Number(sel.values[0]) || 0; await sel.update(render()); }
    else if (sel.customId === "pg_search") {
      if (filter) { filter = ""; page = 0; await sel.update(render()); continue; }   // active filter: tap clears
      const modal = new ModalBuilder().setCustomId("pg_search_modal").setTitle("Search this list")
        .addComponents(new ActionRowBuilder().addComponents(
          new TextInputBuilder().setCustomId("pg_q").setLabel("Show only lines containing...")
            .setStyle(TextInputStyle.Short).setRequired(true).setMaxLength(50)));
      await sel.showModal(modal).catch(() => {});
      let sub;
      try { sub = await sel.awaitModalSubmit({ time: 60_000, filter: i => i.user.id === interaction.user.id && i.customId === "pg_search_modal" }); }
      catch { continue; }
      filter = sanitizeMessage(sub.fields.getTextInputValue("pg_q")).trim();
      page = 0;
      await sub.deferUpdate().catch(() => {});
      try { await interaction.editReply(render()); } catch {}
    }
  }
}

// ---- rcon ----
// Servers come online by setting RCON_HOST_N (+ port/password). Everything that
// fans out over "all servers" iterates ACTIVE_SERVERS so adding a server is just env.
const hasServer2 = !!process.env.RCON_HOST_2;
const hasServer3 = !!process.env.RCON_HOST_3;
const ACTIVE_SERVERS = ["server1", ...(hasServer2 ? ["server2"] : []), ...(hasServer3 ? ["server3"] : [])];
// RCON transport lives in ./rcon (raw TCP + md5 auth). It only needs the logger
// and the live ACTIVE_SERVERS list; everything else here calls these unchanged.
const { getServerConfig, sendRconRaw, sendRcon, sendRconBoth } =
  require("./rcon")({ logger, activeServers: ACTIVE_SERVERS });

/* Pavlov's economy mod can wipe a player's saved caps when they're force-kicked
   (it writes their save on the abrupt disconnect). Snapshot the balance before a
   kick and restore it afterwards if it got wiped/lowered, so a kick or ban never
   costs the player their money. Only restores when the value DROPPED, so legit
   in-game earnings (if they reconnect) are never clobbered. */
function preserveBalanceAcrossKick(name) {
  if (!getModsavePath()) return;
  let before;
  try { before = readPlayerBalance(name); } catch { return; }
  if (before == null || before <= 0) return;        // nothing worth preserving
  // Re-check several times over ~90s: the economy mod wipes the save at disconnect,
  // but a laggy disconnect (or the game re-writing 0 after our restore) can land late.
  for (const delay of [8000, 20000, 40000, 65000, 90000]) {
    setTimeout(() => {
      try {
        const after = readPlayerBalance(name);
        // Only restore a WIPE (file gone or zeroed). Restoring any drop would refund
        // caps a rejoining player legitimately spent within the 25s window.
        if (after == null || after === 0) {
          writePlayerBalance(name, before);
          logger.info("Credits", `Restored ${name}'s credits after kick: ${after ?? "missing"} -> ${before}`);
        }
      } catch (e) { logger.warn("Credits", `balance restore failed for ${name}: ${e.message}`); }
    }, delay);
  }
}


/* Ban a player by writing their name to blacklist.txt on EVERY install (synced),
   kick them off now (RCON) so they don't keep playing, AND flag every IP we've ever
   seen them connect from for alt enforcement. The blacklist write is file-based so it
   never hangs; the kick is fire-and-forget.
   Returns ipBans' enforcement summary plus the blacklist outcome:
   { ids, ips, alts, field, blacklist: { name, servers }, ok }. */
// Every ban flags the account's EOS id so an evader who keeps their account but
// changes name AND IP is still caught. This is safe for temp bans because a served
// temp ban is lifted on rejoin by autoBanDecision (and cleared outright by the 60s
// expiry sweep), so the flag never outlives the ban.
/* ---- OS-level firewall block (ufw) - extracted to ./moderation/firewall ----
   Blocks/unblocks IPs at the OS firewall (opt-in via UFW_BLOCK=1). Used on bans
   and by the owner /firewall command. See moderation/firewall.js for details. */
const { UFW_BLOCK, _IPV4_RE, firewallBlockIps, firewallUnblockIps, firewallStatus } =
  require("./moderation/firewall")({ logger, ipBans, masterIps: MASTER_IPS });

// ---- moderation/bans: native RCON ban/kick enforcement, reconcile, unban (extracted to ./moderation/bans) ----
const { BAN_RECONCILE_MIN_INTERVAL_MS, _reconcileBusy, _sweepBusy, autoBanDecision, banWithIp, enforceBansSweep, fixAutoBanReasons, hardEnforce, isRealBan, parseRcon, reconcileBans, scheduleBanRecheck, sourceBanFor, unbanEverywhere } = require("./moderation/bans")({
  ACTIVE_SERVERS, FILES, UFW_BLOCK, _sameId, blacklistAdd, blacklistRemove,
  firewallBlockIps, firewallUnblockIps, ipBans, isMasterName, loadBans, logger,
  preserveBalanceAcrossKick, safeRead, safeWrite, sanitizeBanName, sanitizeId, sendRcon,
  update, getOnlinePlayers: (...a) => getOnlinePlayers(...a), isAutobanExempt: (...a) => isAutobanExempt(...a), removeAutobanExempt: (...a) => removeAutobanExempt(...a),
});

// ---- discord client & log channel ----
const client = new Client({ intents: [GatewayIntentBits.Guilds] });

/* ── Second bot: faction commands ─────────────────────────────────
   Set FACTION_BOT_TOKEN + FACTION_CLIENT_ID in .env to run a dedicated faction
   bot (own Discord application, invited to each faction's guild). It registers
   ONLY the /whitelist command; the main
   bot keeps everything else. Runs in THIS process, sharing all state - no file
   races. Leave the env vars unset and everything stays on the main bot. */
const FACTION_BOT = !!(process.env.FACTION_BOT_TOKEN && process.env.FACTION_CLIENT_ID);
const factionClient = FACTION_BOT ? new Client({ intents: [GatewayIntentBits.Guilds] }) : null;
// The client that lives in the faction guilds (falls back to the main bot).
const fclient = () => (factionClient && factionClient.isReady() ? factionClient : client);

// ---- plain-text reply rendering (extracted to ./discord/textify) ----
const { embedToText, textifyChunks, textify, patchInteractionOutput } = require("./discord/textify");

function logAction(embed) {
  // Always returns a promise that never rejects, so callers can `await` it OR
  // chain `.catch` without risking "Cannot read properties of undefined". It's
  // still fire-and-forget - never block a command's reply on a log post.
  if (!process.env.MOD_LOG_CHANNEL) return Promise.resolve();
  return client.channels.fetch(process.env.MOD_LOG_CHANNEL)
    .then(ch => ch?.isTextBased() && ch.send({ embeds: [embed] }))
    .catch(err => logger.warn("Log", `Failed to post mod log: ${err.message}`));
}
// Connection feed: post via the webhook if CONNECT_WEBHOOK_URL is set (no-op otherwise).
function postFeed(embed) {
  if (!feedHook) return;
  // Webhook feed keeps REAL embeds (everything else in the bot is plain text).
  feedHook.send({ embeds: [embed] }).catch(err => logger.warn("Feed", `webhook post failed: ${err.message}`));
}
// Looks up a custom guild emoji by name (from any guild the bot's in) and returns
// its Discord markup (<:name:id>), or null if the bot has no emoji with that name -
// callers should always have a plain-unicode fallback ready.
function customEmoji(name) {
  try { const e = client.emojis.cache.find(em => em.name === name); return e ? e.toString() : null; }
  catch { return null; }
}
// Kill feed: one clean line per live PvP kill (fired from ipBans' onKill - already
// filtered to a real kill with a distinct killer, no suicides/environmental deaths).
function postKillFeed(killer, killed) {
  if (!KILLFEED_CHANNEL) return;
  // Plain text, no embed and no emoji - one clean line per kill.
  const content = `**${killer}** eliminated **${killed}**`;
  client.channels.fetch(KILLFEED_CHANNEL)
    .then(ch => ch?.isTextBased() && ch.send({ content, allowedMentions: { parse: [] } }))
    .catch(err => logger.warn("KillFeed", `post failed: ${err.message}`));
}
// Ban actions go to a dedicated ban-log channel (BAN_LOG_CHANNEL). If that isn't
// set, they fall back to the regular mod-log channel.
function logBan(embed) {
  const channelId = process.env.BAN_LOG_CHANNEL;
  if (!channelId) return logAction(embed);
  // fire-and-forget (see logAction)
  client.channels.fetch(channelId)
    .then(ch => ch?.isTextBased() && ch.send({ embeds: [embed] }))
    .catch(err => logger.warn("Log", `Failed to post ban log: ${err.message}`));
}

// ---- moderation/vpn: IPHub/IPQS proxy detection + geolocation + auto-ban (extracted to ./moderation/vpn) ----
const { IPHUB_API_KEY, IPINFO_TOKEN, IPQS_API_KEY, _backfillGeo, _doVpnCheck, _regionName, _vpnInFlight, checkVpn, checkVpnAndAlert, formatFullLocation, geoLookup, loadVpnChecks, saveVpnCheck } = require("./moderation/vpn")({
  ACTIVE_SERVERS, CLIN, DIVIDER, EmbedBuilder, FILES, NV,
  banWithIp, brand, clinical, hero, isMasterName,
  logAction, logBan, logger, postFeed, randomQuote, safeRead,
  update, upsertPermBan, writeModLog, isAutobanExempt: (...a) => isAutobanExempt(...a),
});

/* ---------------- update log ----------------
   On every startup, if the checked-out commit has moved since the last
   startup we recorded, post a short "here's what changed" embed to
   UPDATE_LOG_CHANNEL - basically an auto-posted changelog for every deploy.
   Silent no-op if this isn't a git checkout (e.g. a stripped Docker image). */
// -c safe.directory=* sidesteps git's "dubious ownership" guard, which trips on
// a repo owned by a different user than the process (common under Docker, or a
// systemd unit running as a game-server user) - harmless here since we only ever
// read commit metadata, never execute anything from the checkout.
const GIT_SAFE = ["-c", "safe.directory=*"];
function currentGitCommit() {
  try { return execFileSync("git", [...GIT_SAFE, "rev-parse", "HEAD"], { cwd: __dirname, encoding: "utf8", timeout: 5000 }).trim() || null; }
  catch { return null; }
}
// Commit subject lines between two commits, oldest first. Empty on any git error
// (e.g. history was rewritten and `from` no longer exists on this branch).
function commitSubjectsBetween(from, to) {
  try {
    const out = execFileSync("git", [...GIT_SAFE, "log", "--reverse", "--pretty=format:%s", `${from}..${to}`], { cwd: __dirname, encoding: "utf8", timeout: 5000 });
    return out.split("\n").map(s => s.trim()).filter(Boolean);
  } catch { return []; }
}
/* The update log is a PUBLIC changelog - it must never leak private info.
   redactPrivateInfo (in ./utils, unit-tested) scrubs IPs and long ids. */
// Scrub every text surface of an embed (title/description/fields/footer) in place.
// Mutates embed.data directly - NOT via setTitle/etc, whose validators can throw
// and leave private text un-scrubbed. Nothing here can throw, so it can't fail open.
function redactEmbedPrivateInfo(embed) {
  try {
    const d = embed?.data;
    if (!d) return embed;
    if (d.title)        d.title       = redactPrivateInfo(d.title);
    if (d.description)  d.description = redactPrivateInfo(d.description);
    if (d.author?.name) d.author.name = redactPrivateInfo(d.author.name);
    if (d.footer?.text) d.footer.text = redactPrivateInfo(d.footer.text);
    if (Array.isArray(d.fields)) for (const f of d.fields) {
      if (f.name)  f.name  = redactPrivateInfo(f.name);
      if (f.value) f.value = redactPrivateInfo(f.value);
    }
  } catch {}
  return embed;
}
async function postToUpdateLogChannel(embed) {
  try {
    const ch = await client.channels.fetch(UPDATE_LOG_CHANNEL);
    if (!ch?.isTextBased()) { logger.warn("UpdateLog", `Channel ${UPDATE_LOG_CHANNEL} wasn't found or isn't text-based - check UPDATE_LOG_CHANNEL and that the bot can see it.`); return; }
    redactEmbedPrivateInfo(embed);   // public changelog: no IPs / tokens, ever
    await ch.send({ embeds: [embed], allowedMentions: { parse: [] } });
    logger.info("UpdateLog", "Posted.");
  } catch (err) {
    logger.warn("UpdateLog", `Failed to post: ${err.message}`);
  }
}
async function postUpdateLogIfChanged() {
  const commit = currentGitCommit();
  if (!commit) { logger.info("UpdateLog", "Not a git checkout (or `git` isn't on PATH) - nothing to track."); return; }
  const state = safeRead(FILES.UPDATE_LOG_STATE, {});
  safeWrite(FILES.UPDATE_LOG_STATE, { lastCommit: commit });

  if (!state.lastCommit) {
    // First run ever - nothing to diff against yet. Post a one-time confirmation
    // instead of staying silent, so it's obvious the feature is actually wired up.
    logger.info("UpdateLog", `First run - now tracking from ${commit.slice(0, 7)}.`);
    return postToUpdateLogChannel(brand(new EmbedBuilder().setColor(NV.GOLD).setTitle("Update log online")
      .setDescription(`I'll post here whenever I restart on a new commit.`)
      .setFooter({ text: `${commit.slice(0, 7)} - v${BOT_VERSION}` })));
  }
  if (state.lastCommit === commit) { logger.info("UpdateLog", "Commit unchanged since last restart - nothing to post."); return; }

  const subjects = commitSubjectsBetween(state.lastCommit, commit);
  if (!subjects.length) { logger.warn("UpdateLog", `No commit log between ${state.lastCommit.slice(0, 7)} and ${commit.slice(0, 7)} - skipping (rebase/force-push?).`); return; }

  const SHOWN = 10;
  // Redact at the SOURCE - scrub each commit subject before it ever enters the
  // embed, so the public changelog can't leak an IP/id even if the embed-level
  // pass hit an edge case. (redactEmbedPrivateInfo below is the belt-and-braces.)
  const lines = subjects.slice(-SHOWN).map(s => `- ${redactPrivateInfo(s)}`);
  if (subjects.length > SHOWN) lines.unshift(`- ...and ${subjects.length - SHOWN} earlier change${subjects.length - SHOWN === 1 ? "" : "s"}`);

  logger.info("UpdateLog", `Posting ${subjects.length} change(s) since ${state.lastCommit.slice(0, 7)}.`);
  return postToUpdateLogChannel(brand(new EmbedBuilder().setColor(NV.GOLD).setTitle("The bot just updated")
    .setDescription(`${lines.join("\n")}`)
    .setFooter({ text: `${commit.slice(0, 7)} - v${BOT_VERSION}` })));
}

// ---- punishment dm notice ----
async function dmPunishmentNotice(discordUser, { action, color, playerId, reason, fields = [] }) {
  if (!discordUser) return null;
  const embed = brand(new EmbedBuilder().setColor(color)
    .setTitle(`Moderation Notice - ${action}`)
    .setDescription(`${hero("A moderation action has been taken on your account.")}\n\n**${playerId}** - ${action}${reason ? `: ${reason}` : ""}`)
    .addFields(...fields),
    { thumb: true, footer: { text: "You received this because a moderator linked your Discord account to this action." } });
  try {
    await discordUser.send(textify({ embeds: [embed] }));
    return true;
  } catch (err) {
    logger.warn("DM", `Could not DM ${discordUser.id}: ${err.message}`);
    return false;
  }
}

/** Builds the "Player Notified" status field for the moderator's reply. */
function dmStatusField(sent, discordUser) {
  if (sent === null) return null;
  return {
    name: "Player Notified",
    value: sent
      ? `DM delivered to <@${discordUser.id}>`
      : `Couldn't DM <@${discordUser.id}> - their DMs are closed or the bot is blocked.`,
    inline: false,
  };
}

/** Send a single branded embed as a DM. Returns true (sent) or false (failed). */

// ---- player cache ----
const playerCache = {
  server1: [], server2: [], server3: [],
  lastUpdated: { server1: 0, server2: 0, server3: 0 },
};
/** Every online name across all active servers (deduped, case preserved). */
function allCachedPlayers() {
  return [...new Set(ACTIVE_SERVERS.flatMap(s => playerCache[s]))].filter(Boolean);
}
/** Which active servers a name is currently on, e.g. ["Server 1", "Server 3"]. */
function onlineServersOf(name) {
  const key = String(name ?? "").toLowerCase();
  return ACTIVE_SERVERS.filter(s => playerCache[s].some(n => n.toLowerCase() === key)).map(serverLabel);
}
const CACHE_TTL_MS = 90_000;

/** Pull trimmed, non-empty player names out of a parsed RefreshList payload. */
function extractPlayerNames(data) {
  return (data?.PlayerList ?? [])
    .map(p => String(p.Username ?? p.username ?? p.PlayerName ?? p.name ?? p.Name ?? "").trim())
    .filter(Boolean);
}

/** Live online players for a server as { name, id } - id is the Pavlov UniqueId,
    which is what RCON Kick actually targets (NOT the display name). */
async function getOnlinePlayers(server) {
  let data;
  try { data = parseRcon(await sendRcon("RefreshList", server, 3000, 1)); } catch { return []; }
  return (data?.PlayerList ?? []).map(p => ({
    name: String(p.Username ?? p.username ?? p.PlayerName ?? p.name ?? p.Name ?? "").trim(),
    id:   String(p.UniqueId ?? p.uniqueId ?? p.UniqueID ?? p.Id ?? p.id ?? "").trim(),
  })).filter(p => p.name || p.id);
}


/** Update the cached player list for one server from a parsed payload. */
function setPlayerCacheFromData(server, data) {
  if (!data?.Successful) return false;
  playerCache[server] = extractPlayerNames(data);
  playerCache.lastUpdated[server] = Date.now();
  recordKnownPlayers(playerCache[server]);   // remember everyone who's ever been seen
  try { recordServerPeaks(); } catch {}      // all-time peak player tracking
  return true;
}

/* All-time peak concurrent players - per server and combined across every active
   server. Recomputed from the live cache on each roster refresh; only writes when a
   peak is newly exceeded, so it's cheap even at poll frequency. Peaks are monotonic. */
const { reducePeaks } = require("./stats/peaks");
const loadServerStats = () => safeRead(FILES.SERVER_STATS, {});
function recordServerPeaks() {
  const counts = {};
  for (const s of ACTIVE_SERVERS) counts[s] = playerCache[s].length;
  const now = Date.now();
  const date = easternClock().date;   // day key for the daily peak (bot's Eastern day)
  // Cheap pre-check against the in-memory cache; only take the write path on a new peak
  // or a day rollover (which resets the daily bucket).
  if (!reducePeaks(counts, safeRead(FILES.SERVER_STATS, {}), now, date).changed) return;
  return update(FILES.SERVER_STATS, {}, (stats) => reducePeaks(counts, stats, now, date).stats);
}

async function refreshPlayerCache(server = "server1") {
  try {
    const data = parseRcon(await sendRcon("RefreshList", server));
    if (setPlayerCacheFromData(server, data)) {
      logger.debug("Cache", `${server}: ${playerCache[server].length} players`);
    }
  } catch (err) {
    logger.warn("Cache", `${server} refresh failed: ${err.message}`);
    // A roster we can't refresh goes stale fast. Past the TTL, treat the server as
    // empty - otherwise a crashed server's last players keep earning playtime and
    // fresh last-seen stamps indefinitely and stay "online" in every list.
    if (playerCache[server].length && Date.now() - playerCache.lastUpdated[server] > CACHE_TTL_MS) {
      playerCache[server] = [];
      logger.warn("Cache", `${server} roster cleared - unreachable beyond cache TTL`);
    }
  }
}

function getPlayerChoices(server, focused = "") {
  const now     = Date.now();
  const servers = (!server || server === "both") ? ACTIVE_SERVERS : [server];
  const seen    = new Set();
  const choices = [];
  // 1) currently-online players first
  for (const srv of servers) {
    if (now - playerCache.lastUpdated[srv] > CACHE_TTL_MS) refreshPlayerCache(srv);
    for (const name of playerCache[srv]) {
      const key = name.toLowerCase();
      if (seen.has(key) || (focused && !key.includes(focused.toLowerCase()))) continue;
      seen.add(key);
      choices.push({ name, value: name });
    }
  }
  // 2) fall back to previously-seen (offline) players so anyone who's ever
  //    joined can still be picked - e.g. typing "ncr_" surfaces "ncr_private"
  if (choices.length < 25) {
    for (const c of getKnownPlayerChoices(focused, seen, 25 - choices.length)) {
      seen.add(c.value.toLowerCase());
      choices.push(c);
    }
  }
  return choices.slice(0, 25);
}

// ---- playtime tracking ----
function tickPlaytime() {
  const online = allCachedPlayers();
  if (!online.length) return;
  update(FILES.PLAYTIME, {}, (pt) => {
    for (const id of online) pt[id] = (pt[id] ?? 0) + 1;
    return pt;
  });
  recordLastSeen(online);
}

// ---- factions/files: rank/spawn file read/write, membership index (extracted to ./factions/files) ----
const { FACTION_BAK_DIR, FACTION_BULK_DROP_LIMIT, backupFactionFile, readFactionFile, writeFactionFile } = require("./factions/files")({
  FACTION_ROLES_PATH, ensureFile, fs, logger, path, writeGameFile,
});

// ---- donator whitelist file  (one player id per line in donator_file) ----
function readDonatorFile() {
  try {
    return fs.readFileSync(DONATOR_FILE, "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  } catch (err) {
    if (err.code === "ENOENT") { ensureFile(DONATOR_FILE, ""); return []; }   // create it empty
    logger.error("Donator", `Read failed: ${err.message}`);
    return null;                                    // real I/O error
  }
}

function writeDonatorFile(lines, opts = {}) {
  if (!Array.isArray(lines)) { logger.error("Donator", "Refused write: payload is not an array"); return false; }
  if (!opts.allowBulk) {   // same destruction guard as faction files
    let raw = null;
    try { raw = fs.readFileSync(DONATOR_FILE, "utf8"); }
    catch (e) { if (e.code !== "ENOENT") { logger.error("Donator", `Refused write: cannot read current contents (${e.code})`); return false; } }
    if (raw !== null) {
      const current = raw.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
      backupFactionFile("donator.txt", raw);
      const keep    = new Set(lines.map(l => String(l).toLowerCase()));
      const dropped = current.filter(l => !keep.has(l.toLowerCase())).length;
      if (dropped > FACTION_BULK_DROP_LIMIT) {
        logger.error("Donator", `REFUSED write: would remove ${dropped} of ${current.length} entries (limit ${FACTION_BULK_DROP_LIMIT}). Suspected corruption.`);
        return false;
      }
    }
  }
  if (writeGameFile(DONATOR_FILE, lines.join("\n") + "\n")) {   // mirrored to both installs
    logger.info("Donator", `Wrote ${lines.length} entr${lines.length === 1 ? "y" : "ies"} to ${DONATOR_FILE} (both installs)`);
    return true;
  }
  logger.error("Donator", "Write failed");
  return false;
}

function isDonator(playerId) {
  const id = playerId.toLowerCase();
  return (readDonatorFile() ?? []).some(l => l.toLowerCase() === id);
}

/** Returns { ok, already } - already=true if the player was already listed. */
function addDonator(playerId) {
  const lines = readDonatorFile();
  if (lines === null) return { ok: false, already: false };
  if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) return { ok: true, already: true };
  lines.push(playerId);
  return { ok: writeDonatorFile(lines), already: false };
}

/** Returns { ok, missing } - missing=true if the player wasn't listed. */
function removeDonator(playerId) {
  const lines = readDonatorFile();
  if (lines === null) return { ok: false, missing: false };
  const filtered = lines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
  if (filtered.length === lines.length) return { ok: true, missing: true };
  return { ok: writeDonatorFile(filtered), missing: false };
}

/* ---- timed donator-perk suspension (e.g. Donator Abuse punishment) ----
   Pull a player's donator perks now and auto-restore them after `ms`. Keyed by
   lowercased playerId; re-issuing resets the timer. Restore only re-adds players
   who were actually donators when suspended (never grants perks they never had). */
const loadDonatorSuspends = () => safeRead(FILES.DONATOR_SUSPEND, {});
async function suspendDonator(playerId, ms, by) {
  const wasDonator = isDonator(playerId);
  if (wasDonator) removeDonator(playerId);                     // pull perks now
  const restoreAt = Date.now() + ms;
  // Only track a restore if they had perks to give back.
  if (wasDonator) {
    await update(FILES.DONATOR_SUSPEND, {}, (m) => { m[playerId.toLowerCase()] = { playerId, restoreAt, by, at: Date.now() }; return m; });
  }
  return { wasDonator, restoreAt };
}
async function processDonatorRestores() {
  const susp = loadDonatorSuspends();
  const now  = Date.now();
  for (const [key, s] of Object.entries(susp)) {
    if (!s || s.restoreAt > now) continue;
    try {
      addDonator(s.playerId);
      logger.info("Donator", `Restored donator perks for ${s.playerId} - suspension served`);
    } catch (e) { logger.warn("Donator", `Restore failed for ${s.playerId}: ${e.message}`); continue; }
    await update(FILES.DONATOR_SUSPEND, {}, (m) => { delete m[key]; return m; });
    try { await logBan(clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Donator Perks Restored")
      .setDescription(`\`${s.playerId}\`'s donator perks were auto-restored - suspension served.`))); } catch {}
  }
}

function getPlayerFactions(playerId) {
  const id = playerId.toLowerCase();
  let files;
  try { files = fs.readdirSync(FACTION_ROLES_PATH).filter(f => f.endsWith("spawn.txt")); }
  catch { return null; }
  const result = [], seen = new Set();
  for (const file of files) {
    const key  = path.basename(file, ".txt");
    const name = FACTION_SPAWN_MAP[key] ?? key;
    if (seen.has(name)) continue;
    const lines = readFactionFile(file);
    if (lines?.some(l => l.toLowerCase() === id)) { result.push(name); seen.add(name); }
  }
  return result;
}

/* Read every faction spawn (membership) file once and build a reverse index:
   lowercased member name -> [faction names]. Used to roll a player's per-victim
   kills up into per-faction totals without re-reading the files for each victim.
   Returns null if the FactionRoles folder can't be read. */
function buildFactionMembershipIndex() {
  let files;
  try { files = fs.readdirSync(FACTION_ROLES_PATH).filter(f => f.endsWith("spawn.txt")); }
  catch { return null; }
  const index = new Map();
  for (const file of files) {
    const key  = path.basename(file, ".txt");
    const name = FACTION_SPAWN_MAP[key] ?? key;
    const lines = readFactionFile(file);
    if (!lines) continue;
    for (const l of lines) {
      const memberId = l.toLowerCase();
      if (!memberId) continue;
      const arr = index.get(memberId) || [];
      if (!arr.includes(name)) arr.push(name);
      index.set(memberId, arr);
    }
  }
  return index;
}

/* Roll a killer's per-victim tally up into per-faction kill counts. Returns
   { [faction]: { total, members: [{ name, count }] } }, members sorted most-killed
   first. A victim in two factions counts toward each. Killers' own faction is not
   excluded - the tally reflects exactly who they killed. */
function factionKillBreakdown(killerId) {
  const membership = buildFactionMembershipIndex();
  if (!membership) return null;
  let victims = [];
  try { victims = ipBans.getKills(killerId); } catch { victims = []; }
  const out = {};
  for (const v of victims) {
    const factions = membership.get(v.name.toLowerCase());
    if (!factions) continue;                              // victim isn't in any faction
    for (const f of factions) {
      const bucket = out[f] || (out[f] = { total: 0, members: [] });
      bucket.total += v.count;
      bucket.members.push({ name: v.name, count: v.count });
    }
  }
  for (const f of Object.keys(out)) out[f].members.sort((a, b) => b.count - a.count);
  return out;
}

function addPlayerToRankFile(faction, playerId, rank) {
  const cfg = getFactionRankConfig(faction);
  if (!cfg) return true;
  const rankFile = cfg.rankFiles[rank];
  if (!rankFile) {
    logger.warn("Faction", `No rank file mapped for ${faction}/${rank}`);
    return true;
  }
  const lines = readFactionFile(rankFile);
  if (lines === null) {   // real I/O error - do NOT treat as empty (that would wipe the file)
    logger.error("Faction", `Cannot read ${rankFile} to add ${playerId}; aborting to protect the roster`);
    return false;
  }
  if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) return true;
  lines.push(playerId);
  return writeFactionFile(rankFile, lines);
}

function removePlayerFromRankFile(faction, playerId, rank) {
  const cfg = getFactionRankConfig(faction);
  if (!cfg) return true;
  const rankFile = cfg.rankFiles[rank];
  if (!rankFile) return true;
  if (rankFile === SPAWN_FILE_MAP[faction]) return true;   // never drop membership via a rank op (Khans High Rank maps to the spawn file)
  const lines = readFactionFile(rankFile);
  if (!lines) return true;
  const updated = lines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
  if (updated.length === lines.length) return true;
  return writeFactionFile(rankFile, updated);
}

function removePlayerFromAllRankFiles(faction, playerId) {
  const cfg = getFactionRankConfig(faction);
  if (!cfg) return;
  // Rank files ONLY - sub-classes are cleared separately so a promotion/demotion
  // (which clears rank files before setting the new rank) keeps a member's sub-class.
  for (const rankFile of Object.values(cfg.rankFiles)) {
    if (rankFile === SPAWN_FILE_MAP[faction]) continue;   // membership roster is handled by the caller, never here
    const lines = readFactionFile(rankFile);
    if (!lines) continue;
    const updated = lines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
    if (updated.length !== lines.length) writeFactionFile(rankFile, updated);
  }
}
// Clear a player from every sub-class file - used on /whitelist remove (leaving the faction).
function removePlayerFromAllSubclassFiles(faction, playerId) {
  for (const file of Object.values(getFactionSubclasses(faction))) {
    const lines = readFactionFile(file);
    if (!lines) continue;
    const updated = lines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
    if (updated.length !== lines.length) writeFactionFile(file, updated);
  }
}

// Add/remove a player to one sub-class file (its own FactionRoles .txt).
function addPlayerToSubclassFile(faction, playerId, subclass) {
  const file = getFactionSubclasses(faction)[subclass];
  if (!file) return false;
  const lines = readFactionFile(file);
  if (lines === null) { logger.error("Faction", `Cannot read ${file} to add ${playerId}; aborting`); return false; }
  if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) return true;
  lines.push(playerId);
  return writeFactionFile(file, lines);
}
function removePlayerFromSubclassFile(faction, playerId, subclass) {
  const file = getFactionSubclasses(faction)[subclass];
  if (!file) return false;
  const lines = readFactionFile(file);
  if (!lines) return true;
  const updated = lines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
  if (updated.length === lines.length) return true;
  return writeFactionFile(file, updated);
}

function getFactionMembers(faction) {
  const spawn = SPAWN_FILE_MAP[faction];
  if (!spawn) return null;
  const lines = readFactionFile(spawn);
  if (!lines) return null;
  const cfg          = getFactionRankConfig(faction);
  const factionRanks = loadFactionRanks()[faction] ?? {};
  const defaultRank  = getFactionDefaultRank(faction);
  // read each rank file ONCE -> rank -> Set of lowercased names (members can hold several ranks)
  const rankMembers = {};
  if (cfg) for (const r of cfg.order) {
    const f = cfg.rankFiles[r];
    if (!f || f === spawn) continue;
    rankMembers[r] = new Set((readFactionFile(f) || []).map(x => x.toLowerCase()));
  }
  return lines
    .map(id => {
      const key  = id.toLowerCase();
      const held = cfg ? cfg.order.filter(r => rankMembers[r]?.has(key)) : [];
      const ranks = held.length ? held : [factionRanks[key] ?? defaultRank];
      const top   = ranks[ranks.length - 1];
      return { playerId: id, rank: top, ranks, weight: rankWeight(faction, top) };
    })
    .sort((a, b) => b.weight - a.weight || a.playerId.localeCompare(b.playerId));
}

// ---- modsave / balance helpers ----
function getModsavePath()            { return process.env.MODSAVE_PATH || null; }
function getPlayerFilePath(playerId) {
  const base = getModsavePath();
  if (!base) return null;
  // Ledger filenames must match what the GAME writes - names may contain spaces
  // ("Butter Life.txt"). Strip only path separators / control chars, never spaces,
  // else wages and adjustments land in a phantom "ButterLife.txt" file.
  const fname = String(playerId ?? "").replace(/[\\/:*?"<>|\r\n\t]/g, "").trim().slice(0, 80);
  return fname ? path.join(base, `${fname}.txt`) : null;
}
function readPlayerBalance(playerId) {
  const fp = getPlayerFilePath(playerId);
  if (!fp) return null;
  try { const n = parseInt(fs.readFileSync(fp, "utf8").trim(), 10); return isNaN(n) ? null : n; }
  catch { return null; }
}
function writePlayerBalance(playerId, amount) {
  const fp = getPlayerFilePath(playerId);
  if (!fp) return false;
  if (writeGameFile(fp, String(Math.max(0, Math.floor(amount))))) return true;   // mirrored to both installs
  logger.error("Balance", `Write failed for ${playerId}`);
  return false;
}
// Owner-only money wipe: DELETE every player's ledger .txt from ModSave, across
// every install. Folders (FactionRoles, RCON Plus, ...) are ignored - only top-
// level files are touched, never recursed into. The ban-message file and the
// RCON+/menu-access files are skipped. The cross-install sync never recreates a
// deleted file, but a surviving copy in another install would propagate back, so
// we unlink each ledger in every mirrored install.
function wipeAllMoney() {
  const base = getModsavePath();
  if (!base) return { ok: false, error: "MODSAVE_PATH not set" };
  let entries;
  try { entries = fs.readdirSync(base, { withFileTypes: true }); }
  catch (e) { return { ok: false, error: e.code || e.message }; }
  let wiped = 0, failed = 0;
  for (const ent of entries) {
    if (!ent.isFile()) continue;                            // ignore folders (FactionRoles, RCON Plus, ...)
    const lower = ent.name.toLowerCase();
    if (!lower.endsWith(".txt")) continue;                  // ledgers are .txt
    if (lower === "banlist.txt") continue;                  // ban-message file, not a ledger
    if (MODSAVE_SYNC_SKIP.test(ent.name)) continue;         // RCON+ / menu-access files
    const primary = path.join(base, ent.name);
    try {
      fs.unlinkSync(primary);
      wiped++;
      // drop the mirrored copies too, so newest-wins sync can't restore the ledger
      for (const fp of mirrorPaths(primary)) if (fp !== primary) { try { fs.unlinkSync(fp); } catch {} }
    } catch (e) {
      if (e.code === "ENOENT") { wiped++; }                 // already gone - fine
      else { failed++; logger.warn("WipeMoney", `unlink failed for ${ent.name}: ${e.message}`); }
    }
  }
  return { ok: true, wiped, failed, total: wiped + failed };
}

/* Owner "full registration wipe": clear all accumulated PLAYER TRACKING data from
   bot.db - the ipBans connection registry (EOS/IP/name records + flags), K/D and
   kill tallies, playtime, last-seen, the known-player list, and activity peaks.
   Does NOT touch bans, blacklist, warrants, whitelists/ranks, donators, RCON menu
   grants, or any configuration - only the "who has the bot seen" telemetry. */
function wipeAllPlayerData() {
  const r = { registry: 0, flagged: 0, kd: 0, playtime: 0, lastseen: 0, known: 0 };
  try { const c = ipBans.clearAll(); r.registry = c.ids; r.flagged = c.flagged; } catch (e) { logger.warn("Wipe", `registry clear failed: ${e.message}`); }
  try { r.kd = ipBans.clearKD(); } catch (e) { logger.warn("Wipe", `kd clear failed: ${e.message}`); }
  const clearMap = (file) => { const before = Object.keys(safeRead(file, {}) || {}).length; safeWrite(file, {}); return before; };
  try { r.playtime = clearMap(FILES.PLAYTIME); } catch (e) { logger.warn("Wipe", `playtime clear failed: ${e.message}`); }
  try { r.lastseen = clearMap(FILES.LASTSEEN); } catch (e) { logger.warn("Wipe", `lastseen clear failed: ${e.message}`); }
  try { r.known    = clearMap(FILES.KNOWN);    } catch (e) { logger.warn("Wipe", `known clear failed: ${e.message}`); }
  try { safeWrite(FILES.SERVER_STATS, {}); } catch (e) { logger.warn("Wipe", `server stats clear failed: ${e.message}`); }
  return r;
}

// ---- casino/ledger: atomic caps debit/credit (extracted to ./casino/ledger) ----
const { creditCaps, debitCaps, mutateBalance } = require("./casino/ledger")({
  logger, readPlayerBalance, writePlayerBalance,
});

/* ---------------- modsave ban-message file (custom ban screen) ----------------
   Config/blacklist.txt stays NAMES-ONLY (what Pavlov matches on). Separately we
   write a rich banlist into the ModSave tree that the custom game mode reads to
   show the player WHY they're banned: reason, unban date, and an appeal link.
   Built from the ban JSON; mirrored to every install. */
const APPEAL_LINK = process.env.APPEAL_LINK || "discord.gg/newvegasrp";
function modsaveBanlistPath() {
  return process.env.MODSAVE_BLACKLIST_PATH || path.join(PAVLOV_BASE_1, "Pavlov/Saved/Config/ModSave/banlist.txt");
}
function buildModsaveBanlist() {
  const fmtDate = (ms) => { try { return new Date(ms).toISOString().slice(0, 10); } catch { return "unknown"; } };
  const blocks = [];
  for (const b of loadBans()) {
    const unban = (b.permanent || !b.expires) ? "Permanent" : fmtDate(b.expires);
    blocks.push([
      b.playerId,
      `Reason: ${b.reason || "No reason provided"}`,
      `Unban: ${unban}`,
      `Appeal: ${APPEAL_LINK}`,
    ].join("\n"));
  }
  return blocks.join("\n\n") + (blocks.length ? "\n" : "");
}
function syncModsaveBanlist() {
  try { writeGameFile(modsaveBanlistPath(), buildModsaveBanlist()); }   // atomic, mirrored, skips if unchanged
  catch (e) { logger.warn("Banlist", `modsave banlist write failed: ${e.message}`); }
}
// UTC epoch ms for 12:00 PM America/New_York on a YYYY-MM-DD date (DST-aware:
// noon EST = 17:00 UTC, noon EDT = 16:00 UTC). Date-based bans lift at noon Eastern.
function easternNoonUTC(dateStr) {
  let y, mo, d;
  const m0 = String(dateStr).match(/(\d{4})-(\d{1,2})-(\d{1,2})/);
  if (m0) { y = +m0[1]; mo = +m0[2]; d = +m0[3]; }
  else {                                   // accept other parseable formats (e.g. MM/DD/YYYY)
    const t = Date.parse(dateStr); if (isNaN(t)) return null;
    const dt = new Date(t); y = dt.getUTCFullYear(); mo = dt.getUTCMonth() + 1; d = dt.getUTCDate();
  }
  const guess = Date.UTC(y, mo - 1, d, 12, 0, 0);
  try {
    const fmt = new Intl.DateTimeFormat("en-US", { timeZone: "America/New_York", hour12: false,
      year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit" });
    const p = {}; for (const part of fmt.formatToParts(guess)) p[part.type] = part.value;
    const nyAsUTC = Date.UTC(+p.year, +p.month - 1, +p.day, +(p.hour === "24" ? 0 : p.hour), +p.minute, +p.second);
    return guess + (guess - nyAsUTC);          // shift so NY wall-clock reads 12:00
  } catch { return Date.UTC(y, mo - 1, d, 17, 0, 0); }   // fallback: noon EST
}
// Read the modsave banlist (parsing Name / Reason / Unban blocks) and add any entry
// the database doesn't already have - so bans made from the in-game admin menu show
// up in /banlist with their reason/unban date. Idempotent (skips known names).
function importModsaveBanlist() {
  let text;
  try { text = fs.readFileSync(modsaveBanlistPath(), "utf8"); } catch { return; }
  if (!text.trim()) return;
  const parsed = [];
  for (const block of text.split(/\n\s*\n/).map(b => b.trim()).filter(Boolean)) {
    const lines = block.split(/\r?\n/).map(l => l.trim());
    const name = lines[0];
    if (!name || /^(reason|unban|appeal)\s*:/i.test(name)) continue;     // first line must be the player name
    let reason = "Imported from banlist", unban = "Permanent";
    for (const l of lines.slice(1)) {
      const m = l.match(/^(reason|unban)\s*:\s*(.*)$/i);
      if (m) (m[1].toLowerCase() === "reason" ? (reason = m[2] || reason) : (unban = m[2] || unban));
    }
    parsed.push({ name, reason, unban });
  }
  if (!parsed.length) return;
  return update(FILES.TEMPBAN, [], (bans) => {
    const have = new Set(bans.map(b => String(b.playerId).toLowerCase()));
    let added = 0;
    for (const p of parsed) {
      if (have.has(p.name.toLowerCase())) continue;
      const wantsPerm = /^perm/i.test(p.unban) || !p.unban;
      const expires   = wantsPerm ? null : easternNoonUTC(p.unban);   // lift at noon Eastern that day
      if (!wantsPerm && !expires) {
        // Date we can't parse - keep the ban (safe direction) but say so loudly
        // instead of silently escalating a dated ban to permanent.
        logger.warn("Bans", `Imported ban for "${p.name}" has an unparseable unban date "${p.unban}" - recorded as permanent; /unban and re-ban with a YYYY-MM-DD date to fix`);
        p.reason = `${p.reason} [unparseable unban date: ${p.unban}]`;
      }
      bans.push(expires
        ? { playerId: p.name, reason: p.reason, moderator: "in-game", at: Date.now(), expires, durationLabel: "until " + p.unban }
        : { playerId: p.name, reason: p.reason, moderator: "in-game", at: Date.now(), permanent: true });
      try { removeAutobanExempt(p.name).catch(() => {}); } catch {}   // an in-game ban is deliberate - clears any exemption
      added++;
    }
    if (added) logger.info("Bans", `Imported ${added} ban(s) from the modsave banlist into the database`);
    return bans;
  });
}

// ---- factions/whitelist: snapshot save/load of faction rosters (extracted to ./factions/whitelist) ----
const { loadFactionBackup, memberHasRoleId, saveFactionBackup, wipeFaction } = require("./factions/whitelist")({
  FACTION_ROLES_PATH, FILES, SPAWN_FILE_MAP, backupFactionFile, fs, getFactionRankConfig,
  path, readFactionFile, safeRead, safeWrite, spawn, update,
  writeFactionFile,
});

// ---- temp ban expiry ----
async function processExpiredBans() {
  const now = Date.now();
  const lifted = [];
  for (const ban of loadBans()) {
    if (ban.permanent || !ban.expires || ban.expires > now) continue;   // never auto-lift permanent bans
    try {
      unbanEverywhere(ban.playerId);   // remove from blacklist.txt (both installs) + clear IP flags
      await addAutobanExempt(ban.playerId, "sentence served");   // a served ban must never re-catch them via a shared flag
      lifted.push(ban.playerId);
      logger.info("Bans", `Expired ban lifted: ${ban.playerId}`);
      writeModLog({ action: "auto-unban", playerId: ban.playerId, reason: "Sentence served" });
      await logBan(
        clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Sentence Served - Player Released")
          .setDescription(`> *"Every soul deserves a second chance in the server."*\n\n**${ban.playerId}** served **${ban.durationLabel ?? "Unknown"}** for ${ban.reason} - originally banned by ${ban.moderator}.`),
          "Exile expired - access restored automatically")
      );
    } catch (err) {
      logger.error("Bans", `Unban failed for ${ban.playerId}: ${err.message}`);
      // leave it on the list; retried next sweep
    }
  }
  // Remove only entries that are STILL expired at write time. Deleting by name alone
  // would also delete a ban a mod re-issued for the same player while the awaits above
  // were in flight - leaving them blacklisted with no /banlist record and no expiry.
  if (lifted.length) {
    const liftedSet = new Set(lifted.map(n => n.toLowerCase()));
    await update(FILES.TEMPBAN, [], (bans) =>
      bans.filter(b => !(liftedSet.has(String(b.playerId).toLowerCase()) && !b.permanent && b.expires && b.expires <= now))
    ).then(() => { syncModsaveBanlist(); });
  }
}

// ---- leaderboards: caps/playtime boards, player list, live dashboard (extracted to ./leaderboards) ----
const { buildDashboardEmbed, buildLeaderboardData, buildLeaderboardEmbed, buildPlayerListEmbed, dashboardSnapshots, getAutopostMsgId, hudRow, loadAutopostState, postDashboard, postLeaderboard, postPlayerList, purgeChannel, rankLabel, refreshLeaderboardChannels, serverSnapshot, setAutopostMsgId } = require("./leaderboards")({
  ACTIVE_SERVERS, DASHBOARD_CHANNEL, DASHBOARD_INTERVAL_MS, DIVIDER, EmbedBuilder, FILES,
  GLYPH, LEADERBOARD_TOP_N, NV, PLAYERLIST_CHANNEL, allCachedPlayers,
  bar, brand, cell, client, loadMenuGrants,
  fs, getModsavePath, hero, logger, meter,
  parseRcon, path, refreshPlayerCache, safeRead, safeWrite, sendRcon,
  serverLabel,
  playerCache, easternClock,
});

/* Find the Discord user to DM for a Pavlov username, by matching the guild member
   whose server NICKNAME (or display name) equals the name. Returns a User or null. */
async function dmUserForPavlov(name, guild) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key || !guild) return null;
  // Match a guild member's nickname / display name.
  try {
    const found = await guild.members.fetch({ query: name, limit: 100 });   // query (op8) - no privileged intent needed
    const m = found.find(mm => (mm.nickname && mm.nickname.toLowerCase() === key) || mm.displayName.toLowerCase() === key);
    if (m) return m.user;
  } catch { /* ignore */ }
  return null;
}

/* ================================================================
   SELF-SERVICE RCON MENU PANEL
   ================================================================
   A channel with a button. Staff press it, enter their in-game name, and the bot
   grants the RCON menu matching their HIGHEST Discord role (High Staff > Staff).
   High Staff also gets Mod + Access Manager. */
async function ensureMenuPanel() {
  if (!MENU_PANEL_CHANNEL) return;
  let ch; try { ch = await client.channels.fetch(MENU_PANEL_CHANNEL); } catch { return; }
  if (!ch?.isTextBased()) return;
  const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN)
    .setTitle("🎛️ RCON Menu Access")
    .setDescription(`Tools for trusted staff.\n\nPress **Get Menu** and enter your **exact** Pavlov in-game name. The bot grants the menu that matches your highest staff role automatically - no admin needed.\n\nOne RCON name per Discord account. Enter **your own name again** any time to remove your menu and redo it.`));
  const row = new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("menu_start").setLabel("Get Menu").setStyle(ButtonStyle.Success));
  // Real embed (not through textify) so the panel renders as a card. content:"" clears
  // any old plain-text body when refreshing a panel that predates this format.
  const payload = { content: "", embeds: [embed], components: [row] };
  const saved = safeRead(FILES.MENU_PANEL, {});
  if (saved.id) {
    // Refresh the existing panel in place so a format change takes effect without
    // an admin deleting the old message.
    try { const m = await ch.messages.fetch(saved.id); await m.edit(payload); return; }
    catch { /* message gone - fall through and repost */ }
  }
  try { const m = await ch.send(payload); safeWrite(FILES.MENU_PANEL, { id: m.id }); }
  catch (e) { logger.warn("MenuPanel", `panel post failed: ${e.message}`); }
}

/* Log linking a Discord user to the RCON name they claimed a menu for. One active
   menu per Discord user - they can't claim a second/other name while their grant is
   on record; re-entering their own name removes it (toggle), same as whitelists. */
const loadMenuLinks = () => safeRead(FILES.MENU_LINKS, {});
function setMenuLink(discordId, data) { return update(FILES.MENU_LINKS, {}, (m) => { m[discordId] = data; return m; }); }
function clearMenuLink(discordId)     { return update(FILES.MENU_LINKS, {}, (m) => { delete m[discordId]; return m; }); }
// Active only while their name still holds a recorded grant (an admin /stripmenu frees them).
function menuLinkActive(link) { return !!(link && link.name && (loadMenuGrants()[String(link.name).toLowerCase()] || []).length); }

async function handleMenuPanelSubmit(interaction) {
  // RCON blacklist role - self-serve is entirely off for these members.
  if (memberHasRoleId(interaction.member, RCON_BLACKLIST_ROLE_ID)) {
    logger.info("MenuPanel", `${interaction.user.tag} blocked by RCON blacklist role`);
    return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Menu Denied")
      .setDescription("Your self-serve RCON menu access has been revoked. Contact an admin if you believe this is a mistake."))], flags: MessageFlags.Ephemeral });
  }
  const name = sanitizeMessage(interaction.fields.getTextInputValue("menu_name")).trim();
  if (!name) {
    return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Menu Denied")
      .setDescription("Enter your exact Pavlov in-game name."))], flags: MessageFlags.Ephemeral });
  }
  // One RCON name per Discord user.
  const link = loadMenuLinks()[interaction.user.id];
  if (menuLinkActive(link)) {
    if (name.toLowerCase() === link.name.toLowerCase()) {
      // Re-entered their own name -> strip their menu so they can redo it.
      await interaction.deferReply({ flags: MessageFlags.Ephemeral });
      const t = sanitizeId(link.name);
      const hadHS = (loadMenuGrants()[link.name.toLowerCase()] || []).some(g => g.menuValue === "highstaff");
      await sendRconBoth(`RemoveMenu ${t}`, "both");
      if (hadHS) { await sendRconBoth(`RemoveMod ${t}`, "both"); await sendRconBoth(`RemoveAccessManager ${t}`, "both"); }
      for (const m of MENUS) for (const srv of [...ACTIVE_SERVERS, "both"]) await removeMenuGrant(link.name, srv, m.value);
      await clearMenuLink(interaction.user.id);
      logAction(clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Menu Removed (self)")
        .setDescription(`**${interaction.user.username}** removed their own menu (was \`${link.name}\`).`)));
      return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Menu Removed")
        .setDescription(`Removed the menu from \`${link.name}\`. Press **Get Menu** again to re-claim.`))] });
    }
    return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Already Claimed")
      .setDescription(`You already hold a menu as \`${link.name}\`. One RCON name per Discord account.\n\nTo change it, press **Get Menu** and enter **${link.name}** to remove it first.`))], flags: MessageFlags.Ephemeral });
  }
  // Highest role wins (handles GuildMember .cache and raw role-array shapes).
  const tier = menuRoleTiers().find(t => t.role && memberHasRoleId(interaction.member, t.role));
  if (!tier) {
    return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("No Menu Role")
      .setDescription("You don't hold a High Staff, Staff, or Whitelist RCON role, so there's no menu to grant. Ask an admin if this is wrong."))], flags: MessageFlags.Ephemeral });
  }
  const meta = MENUS.find(m => m.value === tier.menu);
  await interaction.deferReply({ flags: MessageFlags.Ephemeral });
  const target = sanitizeId(name);              // menus target the USERNAME on this gamemode
  if (tier.menu === "highstaff") {
    await sendRconBoth(`AddMod ${target}`, "both");
    await sendRconBoth(`AddAccessManager ${target}`, "both");
    await sendRconBoth(`GiveMenu ${target} ${meta.menuId}`, "both");
  } else {
    await sendRconBoth(`GiveMenu ${target} ${meta.menuId}`, "both");
  }
  await addMenuGrant(name, "both", tier.menu, meta.menuId, `self-service:${interaction.user.tag}`);
  await setMenuLink(interaction.user.id, { name, menu: tier.menu, at: Date.now() });   // Discord user <-> RCON name
  logger.info("MenuPanel", `${interaction.user.tag} self-granted ${meta.name} to "${name}" (${target})`);
  const embed = clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Menu Granted")
    .setDescription(`Granted the **${meta.name}** menu to \`${name}\` on both servers. Open your in-game menu to use it.${tier.menu === "highstaff" ? "\nAlso granted Mod + Access Manager." : ""}`));
  // mirror to the mod log for an audit trail
  logAction(clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Menu Self-Service")
    .setDescription(`**${interaction.user.username}** self-granted **${meta.name}** to \`${name}\`.`)));
  return interaction.editReply({ embeds: [embed] });
}

/* ================================================================
   MEMBER VERIFICATION
   ================================================================
   #verify has a Verify button -> modal for the user's exact Pavlov name -> the bot
   links that name to their confirmed IP (from ipBans) and sends an accept/deny
   request to the staff channel. On accept: grant the Verified role, store the link,
   and log Discord->IP to the connection webhook. One person per name, and no alts
   (a confirmed IP already tied to another verified member is rejected). Access
   gating is via the Verified role - every channel except #verify is locked to it. */
const { verificationConflict } = require("./verify/rules");
const loadVerifications = () => safeRead(FILES.VERIFICATIONS, {});
const getVerification   = (discordId) => loadVerifications()[String(discordId)] ?? null;
// Reverse lookup: which Discord user verified this Pavlov name (case-insensitive)?
function verifiedDiscordForName(name) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key) return null;
  for (const [discordId, rec] of Object.entries(loadVerifications()))
    if (String(rec?.name ?? "").toLowerCase() === key) return { discordId, ...rec };
  return null;
}
function setVerification(discordId, data) { return update(FILES.VERIFICATIONS, {}, (m) => { m[String(discordId)] = data; return m; }); }
const loadVerifyState = () => safeRead(FILES.VERIFY_STATE, {});
function saveVerifyState(patch) { return update(FILES.VERIFY_STATE, {}, (m) => ({ ...m, ...patch })); }
// Pending requests keyed by a short token (kept out of the button customId, which
// Discord caps at 100 chars - a long Pavlov name would blow the budget).
function addPendingVerify(token, data) { return update(FILES.VERIFY_STATE, {}, (m) => { m.pending = m.pending || {}; m.pending[token] = data; return m; }); }
const getPendingVerify = (token) => loadVerifyState().pending?.[token] ?? null;
function removePendingVerify(token) { return update(FILES.VERIFY_STATE, {}, (m) => { if (m.pending) delete m.pending[token]; return m; }); }
// Confirmed IPs for a Pavlov name (trustworthy same-line id<->ip pairings only).
function confirmedIpsForName(name) {
  try { const rec = ipBans.getRecord(name); return rec ? (rec.cips || []) : []; }
  catch { return []; }
}

// Post/refresh the Verify button panel in #verify (idempotent).
async function ensureVerifyPanel() {
  if (!VERIFY_CHANNEL) return;
  let ch; try { ch = await client.channels.fetch(VERIFY_CHANNEL); } catch { return; }
  if (!ch?.isTextBased()) return;
  const embed = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN)
    .setTitle("✅ Verify to unlock the server")
    .setDescription(`Press **Verify** and enter your **exact** Pavlov in-game name.\n\nA staff member reviews each request; once approved you get access to the rest of the server.\n\nOne account per person - alts can't be verified.`));
  const row = new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("verify_start").setLabel("Verify").setStyle(ButtonStyle.Success));
  const payload = { content: "", embeds: [embed], components: [row] };
  const saved = loadVerifyState();
  if (saved.panelMsgId) { try { const m = await ch.messages.fetch(saved.panelMsgId); await m.edit(payload); return; } catch { /* gone - repost */ } }
  try { const m = await ch.send(payload); await saveVerifyState({ panelMsgId: m.id }); }
  catch (e) { logger.warn("Verify", `panel post failed: ${e.message}`); }
}

// Create the Unverified role if missing, then gate WITHOUT making channels private:
// channels stay visible to @everyone; only the Unverified role is denied View on
// every channel except #verify (where it's allowed, so they can verify). Also
// undoes the old Verified-lock overwrites (@everyone deny / Verified allow) so
// everyone can view again. Idempotent. Needs Manage Roles + Manage Channels.
async function ensureUnverifiedSetup() {
  if (!VERIFY_CHANNEL) return;
  let verifyCh; try { verifyCh = await client.channels.fetch(VERIFY_CHANNEL); } catch { return; }
  const guild = verifyCh?.guild;
  if (!guild) return;
  const me = guild.members.me;
  if (!me?.permissions.has(PermissionFlagsBits.ManageRoles) || !me?.permissions.has(PermissionFlagsBits.ManageChannels)) {
    logger.warn("Verify", "skipping verification setup - bot needs Manage Roles + Manage Channels");
    return;
  }
  let { unverifiedRoleId } = loadVerifyState();
  if (!unverifiedRoleId || !guild.roles.cache.has(unverifiedRoleId)) {
    try {
      const role = await guild.roles.create({ name: "Unverified", color: 0x2b2d31, hoist: false, mentionable: false, reason: "Verification gating" });
      unverifiedRoleId = role.id; await saveVerifyState({ unverifiedRoleId });
      logger.info("Verify", `created Unverified role ${role.id}`);
    } catch (e) { logger.warn("Verify", `could not create Unverified role: ${e.message}`); }
  }
  if (!unverifiedRoleId) return;
  const V = PermissionFlagsBits.ViewChannel;
  const everyone = guild.roles.everyone.id;
  let updated = 0;
  for (const ch of guild.channels.cache.values()) {
    if (typeof ch.permissionOverwrites?.edit !== "function") continue;   // e.g. threads
    const wantUnverifiedView = ch.id === VERIFY_CHANNEL;   // Unverified can see ONLY #verify
    const evOw  = ch.permissionOverwrites.cache.get(everyone);
    const vfOw  = ch.permissionOverwrites.cache.get(VERIFIED_ROLE);
    const unOw  = ch.permissionOverwrites.cache.get(unverifiedRoleId);
    // Only UNDO channels the bot's own old lock privated - it always paired an
    // @everyone View deny WITH a Verified-role View allow. A channel the admin
    // made private themselves (no Verified allow) is left private - we only
    // unlock previously-public channels.
    const botLocked = !!evOw && evOw.deny.has(V) && !!vfOw && vfOw.allow.has(V);
    const unOk = unOw && (wantUnverifiedView ? unOw.allow.has(V) : unOw.deny.has(V));
    if (!botLocked && unOk) continue;                                    // already correct
    try {
      if (botLocked) {
        await ch.permissionOverwrites.edit(everyone, { ViewChannel: null });        // restore public view
        await ch.permissionOverwrites.edit(VERIFIED_ROLE, { ViewChannel: null });   // drop the paired allow
        try { await ch.permissionOverwrites.edit(me.id, { ViewChannel: null }); } catch {}   // and the bot self-allow
      }
      await ch.permissionOverwrites.edit(unverifiedRoleId, { ViewChannel: wantUnverifiedView });
      updated++;
    } catch (e) { logger.warn("Verify", `perms on ${ch.name} failed: ${e.message}`); }
  }
  if (updated) logger.info("Verify", `set ${updated} channel(s): restored public view where the bot had locked it, Unverified role blocked`);
}

// Modal submit: validate the name + alt rules, then send a staff request.
async function handleVerifySubmit(interaction) {
  const name  = sanitizeMessage(interaction.fields.getTextInputValue("verify_name")).trim();
  const reply = (embed) => interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
  if (!name) return reply(warningEmbed("Need your name", "Enter your exact Pavlov in-game name."));
  const already = getVerification(interaction.user.id);
  if (already) return reply(warningEmbed("Already Verified", `You're already verified as \`${already.name}\`.`));
  const ips = confirmedIpsForName(name);
  if (!ips.length) return reply(warningEmbed("Join the server first", `We couldn't find a confirmed connection for \`${name}\` yet. Connect to the Pavlov server once so we can confirm your identity, then verify.`));
  const conflict = verificationConflict(loadVerifications(), interaction.user.id, name, ips);
  if (conflict) return reply(errorEmbed("Can't Verify", conflict.reason));
  let staff; try { staff = await client.channels.fetch(VERIFY_STAFF_CHANNEL); } catch {}
  if (!staff?.isTextBased()) return reply(errorEmbed("Verification Unavailable", "The staff review channel isn't set up. Tell an admin."));
  const token = `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;
  await addPendingVerify(token, { uid: interaction.user.id, name, at: Date.now() });
  const embed = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Verification Request")
    .setDescription(`<@${interaction.user.id}> wants to verify as \`${name}\`.`)
    .addFields(
      { name: "Discord", value: `<@${interaction.user.id}> \`${interaction.user.id}\``, inline: false },
      { name: "Pavlov name", value: `\`${name}\``, inline: true },
      { name: "IP(s)", value: ips.map(x => `\`${x}\``).join(", ").slice(0, 1000), inline: true },
    ).setFooter({ text: "Sensitive - staff only" }));
  const row = new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId(`verifyreq_ok:${token}`).setLabel("Accept").setStyle(ButtonStyle.Success),
    new ButtonBuilder().setCustomId(`verifyreq_no:${token}`).setLabel("Deny").setStyle(ButtonStyle.Danger));
  try { await staff.send({ embeds: [embed], components: [row], allowedMentions: { parse: [] } }); }
  catch (e) { logger.warn("Verify", `staff post failed: ${e.message}`); return reply(errorEmbed("Couldn't Submit", "Something went wrong sending your request. Try again.")); }
  return reply(successEmbed("Request Sent", `Your verification as \`${name}\` was sent to staff. You'll get the role once it's approved.`));
}

// Staff accept / deny button in the staff channel.
async function handleVerifyDecision(interaction) {
  if (!hasModRole(interaction.member)) return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral }).catch(() => {});
  const [tag, token] = interaction.customId.split(":");
  const pending = getPendingVerify(token);
  if (!pending) {
    const gone = brand(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Request Expired").setDescription("This verification request is no longer valid (already handled or the bot restarted)."));
    return interaction.update({ embeds: [gone], components: [] }).catch(() => {});
  }
  const { uid, name } = pending;
  await removePendingVerify(token);
  if (tag === "verifyreq_ok") {
    const ips = confirmedIpsForName(name);
    const conflict = verificationConflict(loadVerifications(), uid, name, ips);
    if (conflict && conflict.code !== "self") {
      const stale = brand(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Verification Void").setDescription(`${conflict.reason}\nNothing was changed.`));
      return interaction.update({ embeds: [stale], components: [] }).catch(() => {});
    }
    try {
      const member = await interaction.guild.members.fetch(uid);
      await member.roles.add(VERIFIED_ROLE, "Verified");
      const { unverifiedRoleId } = loadVerifyState();
      if (unverifiedRoleId && member.roles.cache.has(unverifiedRoleId)) { try { await member.roles.remove(unverifiedRoleId, "Verified"); } catch {} }
      await setVerification(uid, { name, ips, at: Date.now(), by: interaction.user.tag });
      writeModLog({ action: "verify", targetUserId: uid, playerId: name, ips: ips.join(","), by: interaction.user.tag });
      if (feedHook) { feedHook.send({ embeds: [brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Verified")
        .setDescription(`<@${uid}> \`${uid}\` verified as \`${name}\``)
        .addFields({ name: "IP(s)", value: (ips.map(x => `\`${x}\``).join(", ") || "*none*").slice(0, 1000), inline: false }))], allowedMentions: { parse: [] } }).catch(() => {}); }
      try { const u = await client.users.fetch(uid); await u.send(`You're verified as \`${name}\` - welcome in.`); } catch {}
      const done = brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Verification Approved").setDescription(`**${interaction.user.username}** approved <@${uid}> as \`${name}\`.`));
      return interaction.update({ embeds: [done], components: [] }).catch(() => {});
    } catch (e) {
      logger.warn("Verify", `approve failed: ${e.message}`);
      return interaction.reply({ embeds: [errorEmbed("Approve Failed", e.message)], flags: MessageFlags.Ephemeral }).catch(() => {});
    }
  }
  try { const u = await client.users.fetch(uid); await u.send(`Your verification as \`${name}\` was denied by staff.`); } catch {}
  const done = brand(new EmbedBuilder().setColor(NV.RUST_RED).setTitle("Verification Denied").setDescription(`**${interaction.user.username}** denied <@${uid}>'s request to verify as \`${name}\`.`));
  return interaction.update({ embeds: [done], components: [] }).catch(() => {});
}

// ---- menu grant persistence ----
function addMenuGrant(playerId, server, menuValue, menuId, grantedBy) {
  const key = playerId.toLowerCase();
  return update(FILES.MENU_GRANTS, {}, (grants) => {
    if (!grants[key]) grants[key] = [];
    grants[key] = grants[key].filter(g => !(g.menuValue === menuValue && g.server === server));
    grants[key].push({ server, menuValue, menuId, grantedBy, at: Date.now() });
    return grants;
  });
}

function removeMenuGrant(playerId, server, menuValue) {
  const key = playerId.toLowerCase();
  return update(FILES.MENU_GRANTS, {}, (grants) => {
    if (!grants[key]) return grants;
    grants[key] = grants[key].filter(g => !(g.menuValue === menuValue && g.server === server));
    if (!grants[key].length) delete grants[key];
    return grants;
  });
}

/* Re-grant recorded menus when a granted player REJOINS. The server drops a
   player's live menu on disconnect, so without this a /givemenu (or self-service
   claim) only lasted until they left. Fired from the connection log (onConnect);
   waits a few seconds so the player shows up in RefreshList, then re-sends the
   exact grant(s) on record - nothing is granted that an admin didn't grant. */
const _recentRegrant = new Map();   // nameLower -> ts (don't re-grant more than once per 2 min)

/* Master names get a menu handed to them automatically on join - GiveMenu with NO
   bit code (the server grants its default menu). Debounced like the re-grant path so
   a reconnect flurry doesn't spam RCON. */
function grantMasterMenu(name) {
  const key = String(name ?? "").toLowerCase();
  if (!key) return;
  if (Date.now() - (_recentRegrant.get(key) ?? 0) < 120_000) return;
  _recentRegrant.set(key, Date.now());
  setTimeout(async () => {
    try {
      const target = sanitizeId(name);              // menus target the USERNAME on this gamemode
      await sendRconBoth(`GiveMenu ${target}`, "both");
      logger.info("Menus", `Granted master menu to ${name} on join (${target})`);
    } catch (e) { logger.warn("Menus", `master menu grant failed for ${name}: ${e.message}`); }
  }, 8_000);   // let the join settle so RefreshList lists them
}

/* Players immune to /flush's random kick: master names, donators (donator.txt), and
   Staff/High Staff menu holders. NOTE: this is /flush immunity ONLY - it does NOT
   exempt anyone from ban-evasion auto-bans. Only MASTER names bypass auto-ban (see
   onAutoBan); staff/donators sharing an IP with an evader are still enforced. */
function isProtectedPlayer(name) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key) return false;
  if (isMasterName(key)) return true;
  if (isDonator(key)) return true;
  const grants = loadMenuGrants()[key] || [];
  return grants.some(g => g.menuValue === "staff" || g.menuValue === "highstaff");
}

/* Auto-ban exemption: a player who was explicitly UNBANNED is never auto-banned again
   (even if a lingering flag - e.g. a shared IP with a still-banned evader - would match),
   until they're deliberately re-banned. Keyed by lowercased Pavlov name. */
const loadAutobanExempt = () => safeRead(FILES.AUTOBAN_EXEMPT, {});
const isAutobanExempt   = (name) => { const k = String(name ?? "").trim().toLowerCase(); return !!k && !!loadAutobanExempt()[k]; };
function addAutobanExempt(name, by) { const k = String(name).toLowerCase(); return update(FILES.AUTOBAN_EXEMPT, {}, (m) => { m[k] = { name, at: Date.now(), by }; return m; }); }
function removeAutobanExempt(name)  { const k = String(name).toLowerCase(); return update(FILES.AUTOBAN_EXEMPT, {}, (m) => { delete m[k]; return m; }); }

function scheduleMenuRegrant(name) {
  const key = String(name ?? "").toLowerCase();
  if (!key) return;
  const grants = loadMenuGrants()[key];
  if (!grants || !grants.length) return;
  if (Date.now() - (_recentRegrant.get(key) ?? 0) < 120_000) return;
  _recentRegrant.set(key, Date.now());
  if (_recentRegrant.size > 500) { const cut = Date.now() - 600_000; for (const [k, t] of _recentRegrant) if (t < cut) _recentRegrant.delete(k); }
  setTimeout(async () => {
    try {
      const target = sanitizeId(name);              // menus target the USERNAME on this gamemode
      // collapse duplicate menus: one send per menu, to "both" if any record says both
      const byMenu = new Map();
      for (const g of grants) {
        const cur = byMenu.get(g.menuValue);
        const srv = (cur && cur.server !== g.server) || g.server === "both" ? "both" : g.server;
        byMenu.set(g.menuValue, { menuId: g.menuId, server: cur ? "both" : srv });
      }
      for (const [menuValue, g] of byMenu) {
        if (menuValue === "highstaff") {
          await sendRconBoth(`AddMod ${target}`, g.server);
          await sendRconBoth(`AddAccessManager ${target}`, g.server);
        }
        await sendRconBoth(`GiveMenu ${target} ${g.menuId}`, g.server);
      }
      logger.info("Menus", `Re-granted ${[...byMenu.keys()].join(" + ")} to ${name} on rejoin (${target})`);
    } catch (e) { logger.warn("Menus", `menu re-grant failed for ${name}: ${e.message}`); }
  }, 8_000);   // let the join settle so RefreshList lists them (live UniqueId resolves)
}

async function rconHealthCheck() {
  for (const srv of ACTIVE_SERVERS) {
    try {
      const r = await sendRcon("RefreshList", srv, 3000, 1);
      const d = parseRcon(r);
      if (!d?.Successful) logger.warn("Health", `${srv} RCON responded but Successful=false`);
      else logger.debug("Health", `${srv} OK`);
    } catch (err) {
      logger.warn("Health", `${srv} unreachable: ${err.message}`);
    }
  }
}

// ---- intervals  - started from startintervals(), which runs only when the ----
function startIntervals() {
setInterval(processExpiredBans,      60_000);
setInterval(processDonatorRestores,  60_000);   // auto-restore timed donator-perk suspensions
if (DB_EXPORT_INTERVAL_MS > 0) {
  setInterval(exportDbToJson, DB_EXPORT_INTERVAL_MS);   // periodic SQLite -> JSON backup
  setTimeout(exportDbToJson, 60_000);                   // one fresh snapshot shortly after startup
}
setInterval(enforceBansSweep,        30_000);   // remove banned players who are still online
setInterval(reconcileBans,          300_000);   // rebuild the server ban list from the DB every 5 min
setInterval(() => { sentenceSweep().catch(() => {}); }, 30_000);          // announce ended jail sentences
setInterval(() => { rankSuspensionSweep().catch(() => {}); }, 30_000);    // restore expired rank suspensions
// (ufw is manual-only via /firewall - no periodic auto-block/reconcile of ban IPs)
setInterval(postLeaderboard,         LEADERBOARD_INTERVAL_MS);
setInterval(postPlayerList,          PLAYERLIST_INTERVAL_MS);
if (DASHBOARD_CHANNEL) setInterval(postDashboard, DASHBOARD_INTERVAL_MS);
setInterval(rconHealthCheck,         RCON_HEALTH_INTERVAL_MS);
setInterval(async () => {
  for (const s of ACTIVE_SERVERS) await refreshPlayerCache(s);
  tickPlaytime();
}, 60_000);

setInterval(autoBackupFactions, 24 * 60 * 60 * 1000);
setInterval(async () => {        // capture any in-game-menu bans, then rebuild the file from the DB
  try { await importModsaveBanlist(); } catch {}
  syncModsaveBanlist();
}, 5 * 60 * 1000);
// Keep the whole ModSave tree identical across all installs (newest-wins).
setInterval(() => { try { syncAllModSave(); } catch (e) { logger.warn("Sync", `ModSave sync failed: ${e.message}`); } }, MODSAVE_SYNC_INTERVAL_MS);
// Seed a snapshot shortly after startup only if none exists yet (don't clobber an
// existing/manual backup on every restart).
setTimeout(() => {
  const b = safeRead(FILES.FACTION_BACKUP, {});
  if (!b || !b.files || !Object.keys(b.files).length) autoBackupFactions();
}, 30_000);

setTimeout(postLeaderboard, 20_000);
}


// Daily faction-whitelist auto-backup, so there's always a recent snapshot to /configure -> Load.
function autoBackupFactions() {
  const r = saveFactionBackup();
  if (r.ok) logger.info("Backup", `Auto-saved whitelists - ${r.count} file(s)`);
  else logger.warn("Backup", `Auto faction backup skipped: ${r.error}`);
}

// ---- commands/definitions: every slash-command builder (registration payload) (extracted to ./commands/definitions) ----
const { ALL_RANK_NAMES, commands, factionCommands, mainCommands } = require("./commands/definitions")({
  ALL_FACTIONS, FACTION_BOT, FACTION_RANKS, PermissionFlagsBits, SlashCommandBuilder,
  PUNISH_CHOICES, MENUS,
});

// ---- events: clientReady handler + ipBans join/leave/kill/auto-ban callbacks (extracted to ./events) ----
const {  } = require("./events")({
  ACTIVE_SERVERS, ActivityType, BOT_VERSION, CLIN, EmbedBuilder, IPHUB_API_KEY,
  PAVLOV_BASES, REST, Routes, UFW_BLOCK, _sameId, addAutobanExempt,
  autoBanDecision, banWithIp, checkVpn, checkVpnAndAlert, client,
  clinical, commands, enforceBansSweep, ensureFactionFiles, pruneObsoleteFactionFiles, ensureMenuPanel, ensureVerifyPanel, ensureUnverifiedSetup, feedHook,
  fixAutoBanReasons, formatFullLocation, grantMasterMenu, hardEnforce, hasServer2,
  hasServer3, healTreeOwnership, hero, importBlacklistToBans, importModsaveBanlist, ipBans,
  isAutobanExempt, isMasterName, loadBans, log, logBan, logger,
  mainCommands, path, postFeed, postKillFeed, postUpdateLogIfChanged, randomQuote,
  rconHealthCheck, reconcileBans, reconcileBlacklists, refreshLeaderboardChannels, refreshPlayerCache, removeBans,
  scheduleMenuRegrant, seedKnownPlayers, sourceBanFor, syncAllModSave, syncModsaveBanlist, syncPlayerLedger,
  unbanEverywhere, upsertPermBan, writeModLog,
  MASTER_NAMES,
});

// ---- graceful shutdown ----
async function shutdown(signal) {
  logger.info("Bot", `${signal} received - draining write queues...`);
  // _queues holds the tail promise of every per-file write chain; waiting on them
  // means pm2 restarts can't kill an in-flight atomic write mid-rename.
  try { await Promise.allSettled([..._queues.values()]); } catch {}
  try { ipBans.flushAll(); } catch {}   // registry / K-D / kill-log throttled writes
  try { if (DB_EXPORT_INTERVAL_MS > 0) exportDbToJson(); } catch {}   // fresh JSON backup on clean exit
  releaseSingleInstanceLock();
  logger.info("Bot", "Queues drained - exiting.");
  process.exit(0);
}
process.on("SIGINT",  () => { shutdown("SIGINT").catch(() => process.exit(1)); });
process.on("SIGTERM", () => { shutdown("SIGTERM").catch(() => process.exit(1)); });
process.on("uncaughtException",  err => logger.error("Uncaught",  err.message, { stack: err.stack }));
process.on("unhandledRkick", r   => logger.error("Unhandled", String(r)));

// ---- interactions ----
// ---- interactions (handler extracted to ./commands) ----
const { onInteraction } = require("./commands")({
  ACTIVE_SERVERS, ALL_FACTIONS, ALL_RANK_NAMES, ActionRowBuilder, BAN_REASON_LABELS, BLACKLIST_IDS,
  BOT_COPYRIGHT, BOT_START_MS, ButtonBuilder, ButtonStyle, CLIN, ComponentType, _diag, redactPrivateInfo,
  DASHBOARD_INTERVAL_MS, DAY_MS, DIVIDER, DONATOR_FILE, EmbedBuilder, FACTION_BAK_DIR,
  FILES, GLYPH, IPHUB_API_KEY,
  MENUS, MessageFlags,
  ModalBuilder, NV, PUNISH_BY_VALUE, SPAWN_FILE_MAP,
  StringSelectMenuBuilder, TextInputBuilder, TextInputStyle, UFW_BLOCK,
  _IPV4_RE, addAutobanExempt, addDonator, addMenuGrant, addPlayerToRankFile,
  addUserBlacklist, adminOnlyEmbed, awaitOwnedComponent, banWithIp, bar, blacklistHas,
  blacklistedEmbed, brand, buildDashboardEmbed, buildFactionMembershipIndex,
  cell, checkRateLimit, checkVpn, client,
  clinical, commandPlayerCandidates, commands, confirmDialog, countFactionRank, creditCaps,
  dashboardSnapshots, debitCaps, mutateBalance, deniedEmbed, dmPunishmentNotice,
  dmStatusField, dmUserForPavlov, easternClock, easternNoonUTC, emptyIdEmbed,
  enforceBansSweep, errorEmbed, factionKillBreakdown, factionLeaderOnlyEmbed, factionLeaderStrictEmbed, policeOnlyEmbed, firewallBlockIps,
  hasPoliceRole, hasWhitelistManageRole, loadWarrants, getWarrants, addWarrant, removeWarrant,
  recordArrest, startSentence, announceArrest, logPolice, getArrests, totalJailServed, suspendRank,
  firewallStatus, firewallUnblockIps, formatKD, formatPlaytime, formatTimeLeft,
  formatUptime, fs, getFactionCap,
  getFactionDefaultRank, getFactionMembers, getFactionRank, getFactionRankBadge, getFactionRankConfig,
  getFactionRankOrder, getFactionSubclasses, getPlayerSubclasses, addPlayerToSubclassFile, removePlayerFromSubclassFile, removePlayerFromAllSubclassFiles,
  verifiedDiscordForName,
  getLastSeen, getOnlinePlayers, getPlayerChoices, getPlayerFactions,
  getPlayerFilePath, getPlayerHistory, getPlayerRanks, handleMenuPanelSubmit, handleVerifySubmit, handleVerifyDecision, hasAdminRole,
  hasFactionLeaderRole, hasModRole, hero, ipBans, isAutobanExempt,
  isBlacklisted, isDonator, isMasterName, isMasterIp, isOwner, isSuperOwner, commandTier, commandTierName, canOverride, isProtectedPlayer,
  loadBans, loadFactionAudit, loadFactionBackup, loadMenuGrants,
  loadMenuRoles, loadModLog, loadPlaytime, loadRoles, loadServerStats, loadVpnChecks,
  extractPlayerNames,
  log, logAction, logBan, logger, memberHasRoleId, meter,
  modOnlyEmbed, ownerOnlyEmbed, paginate, parseDuration, parseRcon,
  patchInteractionOutput, path, playerCache, preserveBalanceAcrossKick, punishDurationLabel, randomQuote,
  rankBadge, rankLabel, rateLimitEmbed, readDonatorFile, readFactionFile,
  readPlayerBalance, refreshPlayerCache, removeBans, removeDonator, removeFactionRank,
  removeMenuGrant, removePlayerFromAllRankFiles, removePlayerFromRankFile, removeUserBlacklist, sanitizeBanName, sanitizeId,
  sanitizeMessage, saveFactionBackup, saveRoles, sendRcon,
  sendRconBoth, serverLabel, setFactionCap, setFactionRank,
  setMenuRole, spawn,
  successEmbed, suspendDonator, textify, unbanEverywhere, update,
  upsertPermBan, upsertTempBan, warningEmbed, wipeAllMoney, wipeAllPlayerData, wipeFaction, writeFactionAudit,
  writeFactionFile, writeModLog, writePlayerBalance,
  blacklistAll,
});

client.on("interactionCreate", onInteraction);
if (factionClient) factionClient.on("interactionCreate", onInteraction);

/* Faction bot startup: register its command set + own the whitelist panels. */
if (factionClient) {
  factionClient.once("clientReady", async () => {
    logger.info("FactionBot", `${factionClient.user.tag} online (faction commands)`);
    try {
      factionClient.user.setPresence({ activities: [{ name: "whitelist rosters  -  /whitelist", type: ActivityType.Watching }], status: "online" });
    } catch {}
    try {
      const frest = new REST({ version: "10" }).setToken(process.env.FACTION_BOT_TOKEN);
      const r = await frest.put(Routes.applicationCommands(process.env.FACTION_CLIENT_ID), { body: factionCommands });
      logger.info("FactionBot", `${r.length} faction commands registered`);
    } catch (err) { logger.error("FactionBot", `command registration failed: ${err.message}`); }
  });
}

// ---- startup ----
// Only log in when run directly (`node index.js`). When required as a
// module (e.g. from tests) the helpers are exported instead, so unit tests
// can exercise the pure logic without opening a Discord connection.
if (require.main === module) {
  startIntervals();
  client.login(process.env.DISCORD_TOKEN);
  if (factionClient) factionClient.login(process.env.FACTION_BOT_TOKEN).catch(e => logger.error("FactionBot", `login failed: ${e.message}`));
}

module.exports = {
  FILES,
  safeWrite,
  // player notes
  // last seen
  recordLastSeen, getLastSeen,
  // known-player registry
  recordKnownPlayers, getKnownPlayerChoices, loadKnownPlayers, seedKnownPlayers,
  // context-aware autocomplete
  commandPlayerCandidates,
  sanitizeId,
  // leaderboards
  savePlaytime,
  // warnings
  // bans (serialized)
  loadBans, upsertTempBan, upsertPermBan, removeBans, autoBanDecision, isRealBan, sourceBanFor,
  // donators
  DONATOR_FILE, readDonatorFile, writeDonatorFile, isDonator, addDonator, removeDonator,
  // owner / access
  isOwner, isBlacklisted, isMasterName, isProtectedPlayer,
  parseClockTime, easternClock, parseDuration,
  isAutobanExempt, addAutobanExempt, removeAutobanExempt,
  // ui / parsing helpers
  splitPages, extractPlayerNames, bar, dmStatusField,
  embedToText, textify, textifyChunks,
  // faction caps
  getFactionCap, setFactionCap,
  // faction file safety
  readFactionFile, writeFactionFile, FACTION_BULK_DROP_LIMIT, FACTION_ROLES_PATH,
  SPAWN_FILE_MAP, ALL_FACTIONS, wipeFaction, loadFactionRanks, setFactionRank,
  // modsave sync
  syncAllModSave, syncPlayerLedger, looksLikeLedgerEntry, isPlayerOnline, playerCache,
  // rcon menu roles
  loadMenuRoles, setMenuRole,
  // economy ledger
  mutateBalance, debitCaps, creditCaps,
  awaitOwnedComponent,
  getAutopostMsgId, setAutopostMsgId,
  isPidAlive, acquireSingleInstanceLock, releaseSingleInstanceLock, LOCK_FILE,
  checkVpn, checkVpnAndAlert, loadVpnChecks, saveVpnCheck,
  // update log
  currentGitCommit, commitSubjectsBetween, postUpdateLogIfChanged,
  // ban reconciliation
  reconcileBans, BAN_RECONCILE_MIN_INTERVAL_MS,
};
