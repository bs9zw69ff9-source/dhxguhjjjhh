// Mojave Authority - our Pavlov VR moderation bot for the New Vegas RP servers.
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
   Posts a fresh message for every player join (name · ID · IP). Paste a
   Discord channel webhook URL into CONNECT_WEBHOOK_URL — works out of the
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
const BOT_COPYRIGHT = `2026 ${BOT_AUTHOR} · All rights reserved`;
const BUILD_ID      = process.env.BUILD_ID || `v${BOT_VERSION}-${new Date(BOT_START_MS).toISOString().slice(0, 10)}`;

// ---- hardcoded owners  (super-users - top of every permission) ----
const OWNER_IDS = new Set([
  "1014251293159731310",
  "678362059905171471",
]);
function isOwner(userId) { return OWNER_IDS.has(String(userId)); }

/* Master in-game names — the people who run the servers. They are never banned,
   never IP-logged, and are handed a menu automatically on every join. Matched by
   USERNAME (case-insensitive), since that's what RCON and the logs give us. */
const MASTER_NAMES = new Set(["lxpxham", "holosight1"]);
function isMasterName(name) { return MASTER_NAMES.has(String(name ?? "").trim().toLowerCase()); }

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
const logger = {
  debug: (t, m, e) => log(LOG_LEVEL.DEBUG, t, m, e),
  info:  (t, m, e) => log(LOG_LEVEL.INFO,  t, m, e),
  warn:  (t, m, e) => log(LOG_LEVEL.WARN,  t, m, e),
  error: (t, m, e) => log(LOG_LEVEL.ERROR, t, m, e),
};

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
    "PLAYTIME_LB_CHANNEL", "PLAYERLIST_CHANNEL", "DONATOR_PATH", "BLACKLIST_IDS", "BUILD_ID",
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
  // can win the create — there's no read-then-write TOCTOU window where both pass the
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
      // Stale lock (owner dead/crashed) or our own pid — remove it and retry the create.
      try { fs.unlinkSync(LOCK_FILE); } catch {}
    }
  }
  logger.warn("Bot", `Could not acquire ${LOCK_FILE} after retry — continuing without a single-instance lock.`);
}
function releaseSingleInstanceLock() {
  try { if (fs.readFileSync(LOCK_FILE, "utf8").trim() === String(process.pid)) fs.unlinkSync(LOCK_FILE); } catch {}
}
acquireSingleInstanceLock();

// ---- data files + SQLite storage layer (extracted to ./database) ----
// Owns bot.db, the JSON->SQLite migration, the cache + write serialization, the
// periodic JSON export, the FILES/DEFAULTS registry and the fs-ownership helpers.
const {
  FILES, CASINO_CONFIG_DEFAULTS,
  safeRead, safeWrite, update,
  exportDbToJson, DB_EXPORT_INTERVAL_MS,
  ensureFile, matchTreeOwner, intendedOwner,
} = require("./database")({ logger, baseDir: __dirname });

/* ---- Typed loaders / savers ---- */
const loadBans          = () => safeRead(FILES.TEMPBAN,        []);
/* Serialized temp-ban mutations — go through update() so concurrent commands
   and the 60s expiry sweep can't clobber each other's writes. */
const _sameId = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
function upsertTempBan(entry) {
  // Creating a ban record is a deliberate ban — it must clear any unban exemption,
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
function upsertPermBan({ playerId, reason, moderator, server }) {
  return upsertTempBan({ playerId, reason: reason || "Permanent ban", moderator: moderator || "system", server: server || "both", at: Date.now(), permanent: true });
}
/** One-time/startup import: pull any names already in blacklist.txt into the ban JSON
    (as permanent entries) so /banlist — which reads the JSON — shows pre-existing bans. */
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
const loadRoles         = () => safeRead(FILES.ROLES,          { modRoleId: "", adminRoleId: "", factionLeaderRoleId: "" });
const saveRoles         = (d) => safeWrite(FILES.ROLES,         d);
const loadWages         = () => safeRead(FILES.WAGES,          []);
const saveWages         = (d) => safeWrite(FILES.WAGES,         d);
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
const loadCasinoConfig  = () => ({ ...CASINO_CONFIG_DEFAULTS, ...safeRead(FILES.CASINO_CONFIG, CASINO_CONFIG_DEFAULTS) });
const saveCasinoConfig  = (d) => safeWrite(FILES.CASINO_CONFIG, d);

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

// ---- player notes  (freeform staff notes on any courier - serialized) ----

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
    deployment). Idempotent — recordKnownPlayers only writes new names. */
async function seedKnownPlayers() {
  const names = new Set();
  for (const k of Object.keys(loadPlaytime())) names.add(k);                 // playtime keys (display-cased)
  for (const w of loadWages())     if (w.playerId)   names.add(w.playerId);  // payroll
  for (const b of loadBans())      if (b.playerId)   names.add(b.playerId);  // temp bans
  for (const d of (readDonatorFile() ?? [])) names.add(d);                   // donators
  try {                                                                      // every faction spawn + rank file
    for (const f of fs.readdirSync(FACTION_ROLES_PATH).filter(n => n.endsWith(".txt"))) {
      for (const id of (readFactionFile(f) ?? [])) names.add(id);
    }
  } catch { /* faction dir not reachable from here — skip */ }

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
  const sub = (cmd === "faction" || cmd === "donator") ? interaction.options.getSubcommand(false) : null;
  const known = loadKnownPlayers();
  const disp  = (k) => known[String(k).toLowerCase()]?.name ?? k;   // recover display casing for lowercased keys

  if (cmd === "unban" || cmd === "checkban")
                              return [...new Set([...loadBans().map(b => b.playerId), ...blacklistAllCached()])];  // temp-banned + blacklist.txt (cached for autocomplete)
  if (cmd === "stripmenu")    return Object.keys(loadMenuGrants()).map(disp);                          // holds a menu grant
  if (cmd === "unmute")       return Object.values(loadMutes()).map(m => m.name);                      // currently muted
  if (cmd === "removewage")   return loadWages().map(w => w.playerId);                                 // on payroll
  if (cmd === "donator" && sub === "remove") return readDonatorFile() ?? [];                           // in donator file
  if (cmd === "faction" && (sub === "remove" || sub === "rank")) {
    const f = interaction.options.getString("faction");
    return f ? (readFactionFile(SPAWN_FILE_MAP[f]) ?? []) : null;                                      // members of that faction
  }
  if (cmd === "faction" && sub === "transfer") {
    const f = interaction.options.getString("from_faction");
    return f ? (readFactionFile(SPAWN_FILE_MAP[f]) ?? []) : null;                                      // members of source faction
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
  { name: "Faction",    value: "faction",   menuId: "0000010000000000000000000000010 00100001000000" },
];

/* Self-service RCON-menu panel: a channel where staff enter their in-game name and
   the bot grants the menu that matches their HIGHEST Discord role. The role→menu
   mapping is set with /setrconroles (stored config), falling back to these env/defaults. */
const MENU_PANEL_CHANNEL  = process.env.MENU_PANEL_CHANNEL  || "1520598952670662677";
const MENU_ROLE_DEFAULTS = {
  highstaff: process.env.MENU_ROLE_HIGHSTAFF || "1521827868756152450",
  staff:     process.env.MENU_ROLE_STAFF     || "1520598947180314836",
  faction:   process.env.MENU_ROLE_FACTION   || "1520598947129852082",
};
/* RCON blacklist role — anyone holding it is barred from the self-serve menu
   panel, even if they also hold a menu role. */
const RCON_BLACKLIST_ROLE_ID = process.env.MENU_ROLE_BLACKLIST || "1520598947129852078";
/* Public /link add requests get posted here with Accept/Deny buttons; only holders
   of the approver role can act on them. */
const LINK_REQUEST_CHANNEL = process.env.LINK_REQUEST_CHANNEL || "1525436831435591710";
const LINK_APPROVER_ROLE   = process.env.LINK_APPROVER_ROLE   || "1521933974744858745";
// Effective mapping: stored /setrconroles config overrides the env defaults.
function loadMenuRoles() {
  const saved = safeRead(FILES.MENU_ROLES, {}) || {};
  return {
    highstaff: saved.highstaff || MENU_ROLE_DEFAULTS.highstaff,
    staff:     saved.staff     || MENU_ROLE_DEFAULTS.staff,
    faction:   saved.faction   || MENU_ROLE_DEFAULTS.faction,
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
    { role: r.faction,   menu: "faction"   },
  ];
}

/* Punishment presets — each reason carries its own sentence, so a mod picks the
   offence and the duration is applied automatically (no manually-typed date, except
   "Other", which takes a custom unban date). Hard R is permanent. */
const DAY_MS = 86_400_000;
const PUNISHMENTS = [
  { name: "MassRDM in Protected Zone",     value: "massrdm_protected", ms: 3 * DAY_MS },
  { name: "Spawn Killing — Faction Spawn", value: "sk_faction",        ms: 5 * DAY_MS },
  { name: "Spawn Killing — Civ Spawn",     value: "sk_civ",            ms: 7 * DAY_MS },
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
// Human sentence for a punishment (embeds/logs): "Permanent", "1 Week", "3 Days", …
function punishDurationLabel(p) {
  if (!p || p.custom) return "Custom";
  if (p.permanent) return "Permanent";
  const d = Math.round(p.ms / DAY_MS);
  return d % 7 === 0 ? `${d / 7} Week${d / 7 !== 1 ? "s" : ""}` : `${d} Day${d !== 1 ? "s" : ""}`;
}
// Slash-command choices, sentence shown in the label (Discord: ≤25 choices, ≤100 chars).
const PUNISH_CHOICES = PUNISHMENTS.map(p => ({
  name: `${p.name}${p.custom ? " (custom time)" : ` — ${punishDurationLabel(p)}`}`,
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

/* Warn auto-escalation thresholds */

const WAGE_TIERS = {
  low_rank:  { label: "Low Rank",  amount: 400,  weekly: true  },
  mid_rank:  { label: "Mid Rank",  amount: 500,  weekly: true  },
  high_rank: { label: "High Rank", amount: 650,  weekly: true  },
  mercenary: { label: "Mercenary", amount: 200,  weekly: false },
};

const WAGE_INTERVAL_MS        = 7 * 24 * 60 * 60 * 1000;
const LEADERBOARD_INTERVAL_MS = 30 * 1000;   // caps + playtime leaderboards refresh every 30s
const LEADERBOARD_TOP_N       = 30;
/* Channel the playtime leaderboard auto-posts to (override with PLAYTIME_LB_CHANNEL). */
const PLAYTIME_LB_CHANNEL     = process.env.PLAYTIME_LB_CHANNEL || "1520598950787158107";
/* Channel the live player list auto-updates in, every 30s (override with PLAYERLIST_CHANNEL). */
const PLAYERLIST_CHANNEL      = process.env.PLAYERLIST_CHANNEL || "1520598950787158106";
const PLAYERLIST_INTERVAL_MS  = 30 * 1000;
const DASHBOARD_CHANNEL       = process.env.DASHBOARD_CHANNEL || "";
const DASHBOARD_INTERVAL_MS   = 30 * 1000;
const RCON_HEALTH_INTERVAL_MS = 5 * 60 * 1000;
/* Channel a short changelog posts to whenever the bot restarts on a new commit
   (override with UPDATE_LOG_CHANNEL). */
const UPDATE_LOG_CHANNEL      = process.env.UPDATE_LOG_CHANNEL || "1520598949667410021";
/* Channel every live PvP kill posts to, one clean line per kill
   (override with KILLFEED_CHANNEL). */
const KILLFEED_CHANNEL        = process.env.KILLFEED_CHANNEL || "1525801322262167623";

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

/* ---- Per-rank caps (within a faction) ----
   Stored under cfg[faction].rankCaps = { [rankName]: number }.
   A missing entry (or 0) means that rank is uncapped. */
function getFactionRankCap(faction, rank) {
  const cap = loadFactionConfig()[faction]?.rankCaps?.[rank];
  return cap > 0 ? cap : null;                       // null = unlimited
}

function getFactionRankCaps(faction) {
  return loadFactionConfig()[faction]?.rankCaps ?? {};
}

function setFactionRankCap(faction, rank, cap) {
  return update(FILES.FACTION_CONFIG, {}, (cfg) => {
    if (!cfg[faction]) cfg[faction] = {};
    if (!cfg[faction].rankCaps) cfg[faction].rankCaps = {};
    if (cap > 0) cfg[faction].rankCaps[rank] = cap;
    else delete cfg[faction].rankCaps[rank];          // 0 clears the cap
    return cfg;
  });
}

/** Number of members currently holding `rank` in `faction`. */
function countFactionRank(faction, rank) {
  const members = getFactionMembers(faction);
  if (!members) return 0;
  return members.filter(m => m.rank === rank).length;
}

/**
 * Returns { ok, cap, count } describing whether one more member can be
 * assigned `rank`. ok=true when uncapped or below the cap.
 */
function rankHasRoom(faction, rank) {
  const cap = getFactionRankCap(faction, rank);
  if (cap === null) return { ok: true, cap: null, count: 0 };
  const count = countFactionRank(faction, rank);
  return { ok: count < cap, cap, count };
}

/* Pavlov server installs, kept in sync. Every game file the bot writes (bans,
   faction roles, donator, economy) is mirrored into EVERY install so all servers
   stay identical. By default we auto-detect every "pavlovserver*" directory next
   to PAVLOV_BASE_1 (pavlovserver, pavlovserver1, pavlovserver2, …). Override with
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

/* Startup sync sanity check — turn the silent no-op failure modes into a visible WARN
   so "sync isn't working" is diagnosable from the log instead of guesswork. */
(function checkSyncConfig() {
  if (process.env.MODSAVE_SYNC === "off") {
    logger.warn("Sync", "MODSAVE_SYNC=off — cross-install ModSave sync is DISABLED. Unset it in .env to enable.");
    return;
  }
  if (PAVLOV_BASES.length < 2) {
    logger.warn("Sync", `Cross-install sync is a NO-OP with only ${PAVLOV_BASES.length} install (listed above). If you run multiple installs, set them explicitly: PAVLOV_BASES=/home/steam/pavlovserver,/home/steam/pavlovserver1,/home/steam/pavlovserver2`);
  }
  const mp = process.env.MODSAVE_PATH;
  if (mp && !PAVLOV_BASES.some(b => mp === b || mp.startsWith(b + path.sep))) {
    logger.warn("Sync", `MODSAVE_PATH (${mp}) is NOT under any install base — per-player balance writes will not be mirrored to other installs. Point it inside one of: ${PAVLOV_BASES.join(", ")}`);
  }
  // Read-only probe of each install's ModSave dir — a permission problem is a common
  // silent cause (the bot user can't write into another install owned by steam). This
  // must NOT create anything: a diagnostic that mkdir's would mask a missing dir and
  // could seed ModSave trees in a mis-discovered install.
  for (const base of PAVLOV_BASES) {
    const dir = path.join(base, "Pavlov", "Saved", "Config", "ModSave");   // = MODSAVE_REL (defined below; inlined to avoid TDZ)
    try {
      fs.accessSync(dir, fs.constants.W_OK);   // exists and writable — good
    } catch (err) {
      if (err.code === "ENOENT") {
        // Not created yet — fine, as long as the bot could create it (parent writable).
        try { fs.accessSync(path.dirname(dir), fs.constants.W_OK); }
        catch (e2) { logger.warn("Sync", `Cannot create ModSave dir for ${path.basename(base)} (${dir}): parent not writable (${e2.code || e2.message}).`); }
      } else {
        logger.warn("Sync", `ModSave dir not writable for ${path.basename(base)} (${dir}): ${err.code || err.message} — sync into this install will fail. Check ownership/permissions.`);
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
  if (synced) logger.info("Sync", `ModSave sync — propagated ${synced} file copy(ies) across ${bases.length} installs`);
  return { synced, installs: bases.length };
}

/* Targeted, EVENT-DRIVEN caps sync for one player. The blanket newest-wins pass runs
   on an interval, which is too slow for server hoppers: leave server 1, join server 2
   inside the window, and server 2 loads a stale ledger — then saves it later with a
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
  if (synced) logger.info("Sync", `Caps ledger for ${name} → ${synced} install(s)`);
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
  if (wrote) logger.info("Blacklist", `Reconciled blacklist.txt across ${wrote} install(s) — ${union.length} name(s)`);
}


/* Donator whitelist file. Lives in the FactionRoles dir alongside the other
   whitelist files. Override the exact path/filename with the DONATOR_PATH env. */
const DONATOR_FILE = process.env.DONATOR_PATH
  || path.join(FACTION_ROLES_PATH, "donator.txt");

const SPAWN_FILE_MAP = {
  "NCR":                 "ncrspawn.txt",
  "Legion":              "legionspawn.txt",
  "Enclave":             "enclavespawn.txt",
  "Khans":               "khansspawn.txt",
  "Brotherhood of Steel":"bosspawn.txt",
  "Kings":               "kingsspawn.txt",
  "Super Mutants":       "supermutantspawn.txt",
  "Followers":           "followersspawn.txt",
};

const FACTION_SPAWN_MAP = {
  ncrspawn:          "NCR",
  legionspawn:       "Legion",
  enclavespawn:      "Enclave",
  khanspawn:         "Khans",
  khansspawn:        "Khans",
  bosspawn:          "Brotherhood of Steel",
  kingsspawn:        "Kings",
  supermutantspawn:  "Super Mutants",
  followersspawn:    "Followers",
};

const ALL_FACTIONS = Object.keys(SPAWN_FILE_MAP);

/* The "faction.txt" master roster the gamemode reads, plus any other plain
   FactionRoles files the bot never writes but the server still expects to
   exist. These are created empty if missing (never overwritten). */
const EXTRA_FACTION_FILES = ["faction.txt"];

/* Build every FactionRoles whitelist file on startup so the game server always
   finds them, even on a brand-new install. We only CREATE missing files (empty)
   — never touch existing rosters — and we do it in EVERY Pavlov install via
   mirrorPaths. Covers: faction.txt, each faction's spawn (membership) file, and
   every rank file, plus the donator file. */
function ensureFactionFiles() {
  const names = new Set(EXTRA_FACTION_FILES);
  for (const faction of ALL_FACTIONS) {
    if (SPAWN_FILE_MAP[faction]) names.add(SPAWN_FILE_MAP[faction]);
    const cfg = getFactionRankConfig(faction);
    if (cfg && cfg.rankFiles) for (const file of Object.values(cfg.rankFiles)) if (file) names.add(file);
  }
  // donator file may live outside FactionRoles (DONATOR_PATH) - handle separately
  let created = 0;
  for (const name of names) {
    for (const fp of mirrorPaths(path.join(FACTION_ROLES_PATH, name))) {
      if (ensureFile(fp, "")) created++;
    }
  }
  for (const fp of mirrorPaths(DONATOR_FILE)) if (ensureFile(fp, "")) created++;
  logger.info("Init", `Faction files ensured across ${PAVLOV_BASES.length} install(s)` + (created ? ` — created ${created} missing` : ""));
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

// ---- fallout: new vegas theme + visual system + embed builders ----
// Extracted to ./discord/theme.js. getClient is lazy so this module loads before
// the Discord client exists; the avatar/version footer resolve at send time.
const {
  NV, CLIN, QUOTES, randomQuote,
  DIVIDER, RULE, BRAND_NAME, GLYPH,
  brandIcon, brand, clampEmbed,
  bar, meter, pip, cell, hero, clinical,
  successEmbed, errorEmbed, warningEmbed, deniedEmbed,
  adminOnlyEmbed, ownerOnlyEmbed, modOnlyEmbed,
  factionLeaderOnlyEmbed, factionLeaderStrictEmbed,
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
  parseDuration, easternClock, parseClockTime,
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
  return `**${k.kills}** / **${k.deaths}**  ·  ${ratio}`;
}

/* ---- scheduled map rotation (Eastern time) ----
   easternClock/parseClockTime live in ./utils (pure). */
const loadAutoRotate = () => safeRead(FILES.AUTOROTATE, {});
function setAutoRotate(cfg) { return safeWrite(FILES.AUTOROTATE, cfg); }
// Runs every minute: rotate the map when the scheduled Eastern time is hit (once/day).
async function checkAutoRotate() {
  const cfg = loadAutoRotate();
  if (!cfg.time) return;
  const { date, hm } = easternClock();
  if (hm !== cfg.time || cfg.lastRun === date) return;   // not the minute, or already fired today
  setAutoRotate({ ...cfg, lastRun: date });              // mark first so overlapping ticks can't double-fire
  const server = cfg.server || "both";
  try {
    await sendRconBoth("RotateMap", server);
    logger.info("AutoRotate", `RotateMap fired at ${hm} EST on ${serverLabel(server)}`);
    logAction(brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Scheduled Map Rotation")
      .setDescription(`Automatic map rotation ran at **${hm} Eastern** on ${serverLabel(server)}.`)
      .setTimestamp()));
  } catch (e) { logger.warn("AutoRotate", `RotateMap failed: ${e.message}`); }
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
    .setTitle(yes ? "Confirmed" : "Cancelled").setDescription(yes ? "Proceeding…" : "No changes made."))], components: [], keepEmbeds: true }).catch(() => {});
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
        new StringSelectMenuBuilder().setCustomId("pg_jump").setPlaceholder("Jump to page…")
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
          new TextInputBuilder().setCustomId("pg_q").setLabel("Show only lines containing…")
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
          logger.info("Caps", `Restored ${name}'s caps after kick: ${after ?? "missing"} -> ${before}`);
        }
      } catch (e) { logger.warn("Caps", `balance restore failed for ${name}: ${e.message}`); }
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
/* ---- OS-level firewall block (ufw) — extracted to ./moderation/firewall ----
   Blocks/unblocks IPs at the OS firewall (opt-in via UFW_BLOCK=1). Used on bans
   and by the owner /firewall command. See moderation/firewall.js for details. */
const { UFW_BLOCK, _IPV4_RE, firewallBlockIps, firewallUnblockIps, firewallResyncAll, firewallField } =
  require("./moderation/firewall")({ logger, loadBans, ipBans });

// ---- moderation/bans: native RCON ban/kick enforcement, reconcile, unban (extracted to ./moderation/bans) ----
const { BAN_RECONCILE_MIN_INTERVAL_MS, _reconcileBusy, _sweepBusy, applyMuteOnJoin, autoBanDecision, banWithIp, clearMute, enforceBansSweep, fixAutoBanReasons, gagEverywhere, getMute, hardEnforce, isRealBan, loadMutes, parseRcon, reconcileBans, scheduleBanRecheck, setMute, sourceBanFor, unbanEverywhere, ungagEverywhere } = require("./moderation/bans")({
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
  getFactionRankOrder, getKnownPlayerChoices, getLastSeen, getPlayerHistory, getPlayerRanks, getServerConfig,
  hasAdminRole, hasFactionLeaderRole, hasModRole, hasServer2, hasServer3, healTreeOwnership,
  hero, importBlacklistToBans, intendedOwner, ipBans, isBlacklisted, isMasterName,
  isOwner, isPidAlive, isPlayerOnline, listFilesRec, loadAutoRotate, loadBans,
  loadCasinoConfig, loadFactionAudit, loadFactionConfig, loadFactionRanks, loadKnownPlayers, loadLastSeen,
  loadMenuGrants, loadMenuRoles, loadModLog, loadPlaytime, loadRoles, loadWages,
  log, logger, looksLikeLedgerEntry, matchTreeOwner, md5, menuRoleTiers,
  meter, mirrorPaths, modOnlyEmbed, ownerOnlyEmbed, paginate, parseClockTime,
  parseDuration, path, pip, preserveBalanceAcrossKick, punishDurationLabel, randomQuote,
  rankBadge, rankHasRoom, rankWeight, rateLimitEmbed, rateLimits, readBlacklist,
  reconcileBlacklists, recordKnownPlayers, recordLastSeen, releaseSingleInstanceLock, removeBans, removeFactionRank,
  removeUserBlacklist, safeRead, safeWrite, sanitizeBanName, sanitizeId, sanitizeMessage,
  saveCasinoConfig, savePlaytime, saveRoles, saveUnbarred, saveUserBlacklist, saveWages,
  seedKnownPlayers, sendRcon, sendRconBoth, sendRconRaw, serverEmoji, serverLabel,
  setAutoRotate, setFactionCap, setFactionRank, setFactionRankCap, setMenuRole, spawn,
  splitPages, successEmbed, syncAllModSave, syncPlayerLedger, update, upsertPermBan,
  upsertTempBan, validateConfig, warningEmbed, wouldWipeBalance, writeFactionAudit, writeGameFile,
  writeGameFileSingle, writeModLog,
  getOnlinePlayers: (...a) => getOnlinePlayers(...a),
  isAutobanExempt: (...a) => isAutobanExempt(...a),
  removeAutobanExempt: (...a) => removeAutobanExempt(...a),
});

// ---- discord client & log channel ----
const client = new Client({ intents: [GatewayIntentBits.Guilds] });

/* ── Second bot: faction commands ─────────────────────────────────
   Set FACTION_BOT_TOKEN + FACTION_CLIENT_ID in .env to run a dedicated faction
   bot (own Discord application, invited to each faction's guild). It registers
   ONLY the /faction command; the main
   bot keeps everything else. Runs in THIS process, sharing all state — no file
   races. Leave the env vars unset and everything stays on the main bot. */
const FACTION_BOT = !!(process.env.FACTION_BOT_TOKEN && process.env.FACTION_CLIENT_ID);
const factionClient = FACTION_BOT ? new Client({ intents: [GatewayIntentBits.Guilds] }) : null;
// The client that lives in the faction guilds (falls back to the main bot).
const fclient = () => (factionClient && factionClient.isReady() ? factionClient : client);

/* ================================================================
   PLAIN-TEXT OUTPUT  (no embeds anywhere)
   ================================================================
   Every reply/log/DM in this codebase is still BUILT as an EmbedBuilder (that
   keeps ~250 call sites untouched), but at send time it is rendered to plain
   markdown text and the embed is dropped. One conversion layer covers all
   interaction replies (patched per interaction), channel logs, panels,
   leaderboards, DMs, and the webhook feed. */
function embedToText(e) {
  let d; try { d = typeof e?.toJSON === "function" ? e.toJSON() : e; } catch { d = e; }
  if (!d || typeof d !== "object") return "";
  // Strip embed-era decoration that reads as clutter in a plain message: divider /
  // rule lines (▓▒░, ----, ───, ====, ▔▔▔ …) and stacked blank lines. Code-fence
  const decor = /^[\s>*_`~|·▓▒░─━▔═▬⎯=—–-]+$/;
  const tidy  = (s) => String(s).split("\n")
    .filter(l => l.trim().startsWith("```") || !decor.test(l))
    .join("\n").replace(/\n{2,}/g, "\n").trim();
  const parts = [];
  if (d.title) parts.push(`### ${d.title}`);               // real Discord heading
  if (d.description) { const t = tidy(d.description); if (t) parts.push(t); }
  for (const f of d.fields ?? []) {
    const name = String(f.name ?? "").trim();
    const val  = tidy(f.value ?? "");
    if (!name && !val) continue;
    parts.push(val.includes("\n") ? `**${name}**\n${val}` : `**${name}:** ${val}`);
  }
  if (d.footer?.text) parts.push(`-# ${d.footer.text}`);   // Discord small-text markdown
  return parts.filter(Boolean).join("\n");
}
// payload {content?, embeds?, ...} -> { first: payload-without-embeds, extra: [overflow strings] }
// Messages cap at 2000 chars (embeds allowed ~6000), so long output splits by line.
function textifyChunks(payload) {
  const text = [payload.content, ...(payload.embeds ?? []).map(embedToText)].filter(Boolean).join("\n\n");
  const chunks = [];
  let cur = "";
  for (let line of String(text).split("\n")) {
    while (line.length > 1900) { chunks.push(line.slice(0, 1900)); line = line.slice(1900); }
    if (cur && cur.length + 1 + line.length > 1900) { chunks.push(cur); cur = line; }
    else cur = cur ? `${cur}\n${line}` : line;
  }
  if (cur) chunks.push(cur);
  const { embeds, ...rest } = payload;
  return { first: { ...rest, content: chunks[0] || "​" }, extra: chunks.slice(1) };
}
// One-message form for channel sends / edits / DMs (overflow truncated with a marker).
// Embeds everywhere. textify() and patchInteractionOutput() are now pass-throughs:
// the strip-a-keepEmbeds-flag is all that's left so premium-UI payloads keep working.
function textify(payload) {
  if (payload && typeof payload === "object" && payload.keepEmbeds) { const { keepEmbeds, ...rest } = payload; return rest; }
  return payload;
}
function patchInteractionOutput(interaction) {
  for (const m of ["reply", "editReply", "followUp", "update"]) {
    const orig = typeof interaction[m] === "function" ? interaction[m].bind(interaction) : null;
    if (!orig) continue;
    interaction[m] = (payload, ...args) => orig(textify(payload), ...args);
  }
}

function logAction(embed) {
  if (!process.env.MOD_LOG_CHANNEL) return;
  // Fire-and-forget: never block a command's reply on a log post. Several
  // non-deferred handlers call this before their first interaction.reply(); if we
  client.channels.fetch(process.env.MOD_LOG_CHANNEL)
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
  const icon = customEmoji("vaultshotgun") || "☠️";
  const embed = brand(new EmbedBuilder().setColor(NV.RUST_RED)
    .setDescription(`${icon} **${killer}** eliminated **${killed}**`));
  client.channels.fetch(KILLFEED_CHANNEL)
    .then(ch => ch?.isTextBased() && ch.send({ embeds: [embed], allowedMentions: { parse: [] } }))
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
  ACTIVE_SERVERS, ALL_FACTIONS, ActionRowBuilder, ActivityType, BAN_DURATIONS, BAN_REASON_LABELS,
  BAN_RECONCILE_MIN_INTERVAL_MS, BLACKLIST_IDS, BOT_AUTHOR, BOT_COPYRIGHT, BOT_START_MS, BOT_VERSION,
  BRAND_NAME, BUILD_ID, ButtonBuilder, ButtonStyle, CASINO_CONFIG_DEFAULTS, CLIN,
  CURRENT_LOG_LEVEL, Client, ComponentType, DASHBOARD_CHANNEL, DASHBOARD_INTERVAL_MS, DAY_MS,
  DB_EXPORT_INTERVAL_MS, DIVIDER, DONATOR_FILE, EXTRA_FACTION_FILES, EmbedBuilder, FACTION_BOT,
  FACTION_DEFAULT_CAP, FACTION_RANKS, FACTION_ROLES_PATH, FACTION_SPAWN_MAP, FILES, GLYPH,
  GatewayIntentBits, KILLFEED_CHANNEL, LEADERBOARD_INTERVAL_MS, LEADERBOARD_TOP_N, LINK_APPROVER_ROLE, LINK_REQUEST_CHANNEL,
  LOCK_FILE, LOG_FILE, LOG_LEVEL, MASTER_NAMES, MENUS, MENU_PANEL_CHANNEL,
  MENU_ROLE_DEFAULTS, MODSAVE_REL, MODSAVE_SYNC_INTERVAL_MS, MODSAVE_SYNC_SKIP, MessageFlags, ModalBuilder,
  NV, OWNER_IDS, PAVLOV_BASES, PAVLOV_BASE_1, PLAYERLIST_CHANNEL, PLAYERLIST_INTERVAL_MS,
  PLAYTIME_LB_CHANNEL, PUNISHMENTS, PUNISH_BY_VALUE, PUNISH_CHOICES, PermissionFlagsBits, QUOTES,
  RCON_BLACKLIST_ROLE_ID, RCON_HEALTH_INTERVAL_MS, REST, RULE, RoleSelectMenuBuilder, Routes,
  SPAWN_FILE_MAP, STAFF_MENU_ID, SlashCommandBuilder, StringSelectMenuBuilder, TextInputBuilder, TextInputStyle,
  UFW_BLOCK, UNBARRED_IDS, UPDATE_LOG_CHANNEL, WAGE_INTERVAL_MS, WAGE_TIERS, WebhookClient,
  _IPV4_RE, __dirname, _blAllCache, _hasRole, _modLogIndexCache, _reconcileBusy,
  _sameId, _sweepBusy, acquireSingleInstanceLock, addUserBlacklist, adminOnlyEmbed, applyMuteOnJoin,
  atomicCopyPreservingMtime, atomicWriteFile, autoBanDecision, awaitOwnedComponent, banWithIp, bar,
  blacklistAdd, blacklistAll, blacklistAllCached, blacklistHas, blacklistPathFor, blacklistRemove,
  blacklistStatus, blacklistedEmbed, brand, brandIcon, cell, checkAutoRotate,
  checkRateLimit, chunkFields, clampEmbed, clearMute, client, clinical,
  commandPlayerCandidates, confirmDialog, countFactionRank, customEmoji, deniedEmbed, disableValidators,
  discoverPavlovBases, easternClock, embedToText, emptyIdEmbed, enforceBansSweep, ensureFactionFiles,
  ensureFile, errorEmbed, execFile, execFileSync, exportDbToJson, factionClient,
  factionLeaderOnlyEmbed, factionLeaderStrictEmbed, fclient, feedHook, firewallBlockIps, firewallField,
  firewallResyncAll, firewallUnblockIps, fixAutoBanReasons, formatKD, formatPlaytime, formatTimeLeft,
  formatUptime, fs, gagEverywhere, getFactionCap, getFactionDefaultRank, getFactionRank,
  getFactionRankBadge, getFactionRankCap, getFactionRankCaps, getFactionRankConfig, getFactionRankOrder, getKnownPlayerChoices,
  getLastSeen, getMute, getPlayerHistory, getPlayerRanks, getServerConfig, hardEnforce,
  hasAdminRole, hasFactionLeaderRole, hasModRole, hasServer2, hasServer3, healTreeOwnership,
  hero, importBlacklistToBans, intendedOwner, ipBans, isBlacklisted, isMasterName,
  isOwner, isPidAlive, isPlayerOnline, isRealBan, listFilesRec, loadAutoRotate,
  loadBans, loadCasinoConfig, loadFactionAudit, loadFactionConfig, loadFactionRanks, loadKnownPlayers,
  loadLastSeen, loadMenuGrants, loadMenuRoles, loadModLog, loadMutes, loadPlaytime,
  loadRoles, loadWages, log, logAction, logBan, logger,
  looksLikeLedgerEntry, matchTreeOwner, md5, menuRoleTiers, meter, mirrorPaths,
  modOnlyEmbed, ownerOnlyEmbed, paginate, parseClockTime, parseDuration, parseRcon,
  patchInteractionOutput, path, pip, postFeed, postKillFeed, preserveBalanceAcrossKick,
  punishDurationLabel, randomQuote, rankBadge, rankHasRoom, rankWeight, rateLimitEmbed,
  rateLimits, readBlacklist, reconcileBans, reconcileBlacklists, recordKnownPlayers, recordLastSeen,
  releaseSingleInstanceLock, removeBans, removeFactionRank, removeUserBlacklist, safeRead, safeWrite,
  sanitizeBanName, sanitizeId, sanitizeMessage, saveCasinoConfig, savePlaytime, saveRoles,
  saveUnbarred, saveUserBlacklist, saveWages, scheduleBanRecheck, seedKnownPlayers, sendRcon,
  sendRconBoth, sendRconRaw, serverEmoji, serverLabel, setAutoRotate, setFactionCap,
  setFactionRank, setFactionRankCap, setMenuRole, setMute, sourceBanFor, spawn,
  splitPages, successEmbed, syncAllModSave, syncPlayerLedger, textify, textifyChunks,
  unbanEverywhere, ungagEverywhere, update, upsertPermBan, upsertTempBan, validateConfig,
  warningEmbed, wouldWipeBalance, writeFactionAudit, writeGameFile, writeGameFileSingle, writeModLog,
  isAutobanExempt: (...a) => isAutobanExempt(...a),
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
async function postToUpdateLogChannel(embed) {
  try {
    const ch = await client.channels.fetch(UPDATE_LOG_CHANNEL);
    if (!ch?.isTextBased()) { logger.warn("UpdateLog", `Channel ${UPDATE_LOG_CHANNEL} wasn't found or isn't text-based - check UPDATE_LOG_CHANNEL and that the bot can see it.`); return; }
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
      .setDescription(`${DIVIDER}\nI'll post here whenever I restart on a new commit.\n${DIVIDER}`)
      .setFooter({ text: `${commit.slice(0, 7)} · v${BOT_VERSION}` })));
  }
  if (state.lastCommit === commit) { logger.info("UpdateLog", "Commit unchanged since last restart - nothing to post."); return; }

  const subjects = commitSubjectsBetween(state.lastCommit, commit);
  if (!subjects.length) { logger.warn("UpdateLog", `No commit log between ${state.lastCommit.slice(0, 7)} and ${commit.slice(0, 7)} - skipping (rebase/force-push?).`); return; }

  const SHOWN = 10;
  const lines = subjects.slice(-SHOWN).map(s => `- ${s}`);
  if (subjects.length > SHOWN) lines.unshift(`- …and ${subjects.length - SHOWN} earlier change${subjects.length - SHOWN === 1 ? "" : "s"}`);

  logger.info("UpdateLog", `Posting ${subjects.length} change(s) since ${state.lastCommit.slice(0, 7)}.`);
  return postToUpdateLogChannel(brand(new EmbedBuilder().setColor(NV.GOLD).setTitle("The bot just updated")
    .setDescription(`${DIVIDER}\n${lines.join("\n")}\n${DIVIDER}`)
    .setFooter({ text: `${commit.slice(0, 7)} · v${BOT_VERSION}` })));
}

// ---- punishment dm notice ----
async function dmPunishmentNotice(discordUser, { action, color, playerId, reason, fields = [] }) {
  if (!discordUser) return null;
  const embed = brand(new EmbedBuilder().setColor(color)
    .setTitle(`Moderation Notice — ${action}`)
    .setDescription(`${hero("A moderation action has been taken on your account.")}\n\n**${playerId}** — ${action}${reason ? `: ${reason}` : ""}`)
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
      : `Couldn't DM <@${discordUser.id}> — their DMs are closed or the bot is blocked.`,
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

/** Live online players for a server as { name, id } — id is the Pavlov UniqueId,
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
  return true;
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
      logger.warn("Cache", `${server} roster cleared — unreachable beyond cache TTL`);
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

/** Returns { ok, already } — already=true if the player was already listed. */
function addDonator(playerId) {
  const lines = readDonatorFile();
  if (lines === null) return { ok: false, already: false };
  if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) return { ok: true, already: true };
  lines.push(playerId);
  return { ok: writeDonatorFile(lines), already: false };
}

/** Returns { ok, missing } — missing=true if the player wasn't listed. */
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
      logger.info("Donator", `Restored donator perks for ${s.playerId} — suspension served`);
    } catch (e) { logger.warn("Donator", `Restore failed for ${s.playerId}: ${e.message}`); continue; }
    await update(FILES.DONATOR_SUSPEND, {}, (m) => { delete m[key]; return m; });
    try { await logBan(clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Donator Perks Restored")
      .setDescription(`\`${s.playerId}\`'s donator perks were auto-restored — suspension served.`))); } catch {}
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
   excluded — the tally reflects exactly who they killed. */
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
  for (const rankFile of Object.values(cfg.rankFiles)) {
    if (rankFile === SPAWN_FILE_MAP[faction]) continue;   // membership roster is handled by the caller, never here
    const lines = readFactionFile(rankFile);
    if (!lines) continue;
    const updated = lines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
    if (updated.length !== lines.length) writeFactionFile(rankFile, updated);
  }
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
// Set EVERY player's caps to 0 (owner-only money wipe). Mirrors to all installs.
function wipeAllMoney() {
  const base = getModsavePath();
  if (!base) return { ok: false, error: "MODSAVE_PATH not set" };
  let files;
  try { files = fs.readdirSync(base).filter(f => f.endsWith(".txt") && f.toLowerCase() !== "banlist.txt"); }   // banlist.txt is the ban-message file, not a ledger
  catch (e) { return { ok: false, error: e.code || e.message }; }
  let wiped = 0;
  for (const f of files) { if (writePlayerBalance(path.basename(f, ".txt"), 0)) wiped++; }
  return { ok: true, wiped, total: files.length };
}

// ---- casino/ledger: atomic caps debit/credit, jackpot pot, shared intake (extracted to ./casino/ledger) ----
const { GAMBLE_QUOTA_MAX, GAMBLE_QUOTA_WINDOW_MS, _ledgerQueues, addToPot, casinoIntake, checkGambleQuota, creditCaps, currentPot, debitCaps, drainPot, gambleQuotaLimitEmbed, mutateBalance } = require("./casino/ledger")({
  FILES, MessageFlags, checkRateLimit, errorEmbed, loadCasinoConfig, logger,
  rateLimitEmbed, readPlayerBalance, safeRead, safeWrite, update, warningEmbed,
  writePlayerBalance,
  loadDiscordLinks: (...a) => loadDiscordLinks(...a),
});

/* ---------------- casino: game logic (pure — no I/O) ----------------
   Extracted to ./casino/games.js (deterministic given Math.random; no bot
   state). casinoResultEmbed stays below — it's theme-coupled. */
const {
  GAME_ICON, JACKPOT_MIN_BALANCE, JACKPOT_WIN_CHANCE,
  SLOT_SYMBOLS, spinSlotReel, spinSlots,
  ROULETTE_RED, rouletteColor, ROULETTE_COLOR_EMOJI, ROULETTE_SPACES, spinRoulette,
  CARD_RANKS, CARD_SUITS, freshDeck, cardValue, handValue, formatHand, isBlackjack,
  RUSSIAN_ROULETTE_MULTS,
} = require("./casino/games");

// Shared result-card builder for the single-shot games (slots, coinflip, roulette,
// cockfight-vs-house) so every casino embed shares one title/field/footer layout.
function casinoResultEmbed({ icon, title, color, body, bet, resultLabel, resultValue, balance }) {
  return brand(new EmbedBuilder().setColor(color).setTitle(`${icon}  ${title}`)
    .setDescription(`${DIVIDER}\n${body}\n${DIVIDER}`)
    .addFields(
      { name: "Wager",     value: `**${bet.toLocaleString()} caps**`,     inline: true },
      { name: resultLabel, value: resultValue,                            inline: true },
      { name: "Balance",   value: `**${balance.toLocaleString()} caps**`, inline: true },
    ).setFooter({ text: randomQuote("casino") }).setTimestamp());
}

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
        logger.warn("Bans", `Imported ban for "${p.name}" has an unparseable unban date "${p.unban}" — recorded as permanent; /unban and re-ban with a YYYY-MM-DD date to fix`);
        p.reason = `${p.reason} [unparseable unban date: ${p.unban}]`;
      }
      bans.push(expires
        ? { playerId: p.name, reason: p.reason, moderator: "in-game", at: Date.now(), expires, durationLabel: "until " + p.unban }
        : { playerId: p.name, reason: p.reason, moderator: "in-game", at: Date.now(), permanent: true });
      try { removeAutobanExempt(p.name).catch(() => {}); } catch {}   // an in-game ban is deliberate — clears any exemption
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
        clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Sentence Served — Courier Released")
          .setDescription(`> *"Every soul deserves a second chance in the Mojave."*\n\n**${ban.playerId}** served **${ban.durationLabel ?? "Unknown"}** for ${ban.reason} — originally banned by ${ban.moderator}.`),
          "Exile expired — access restored automatically")
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

// ---- wage payout ----
async function processWagePayout() {
  const wages = loadWages(), now = Date.now();
  const weekly = wages.filter(w => WAGE_TIERS[w.tier]?.weekly);
  if (!weekly.length) return;
  const results = { paid: [], skipped: [], failed: [] };
  let changed = false;
  for (const entry of weekly) {
    const tier = WAGE_TIERS[entry.tier];
    if (!tier) continue;
    if (entry.lastPaidAt && now - entry.lastPaidAt < WAGE_INTERVAL_MS * 0.9) {
      results.skipped.push({ playerId: entry.playerId }); continue;
    }
    const current = readPlayerBalance(entry.playerId) ?? 0;
    const newBal  = current + tier.amount;
    if (writePlayerBalance(entry.playerId, newBal)) {
      entry.lastPaidAt = now; changed = true;
      results.paid.push({ playerId: entry.playerId, tier: tier.label, amount: tier.amount, newBal });
      writeModLog({ action: "wage-payout", playerId: entry.playerId, amount: tier.amount, tier: tier.label });
    } else {
      results.failed.push({ playerId: entry.playerId, tier: tier.label });
    }
  }
  if (changed) saveWages(wages);
  logger.info("Wages", `Payout: ${results.paid.length} paid, ${results.skipped.length} skipped, ${results.failed.length} failed`);
  if (!results.paid.length && !results.failed.length) return;
  const lines = [
    ...results.paid.map(r   => `\`${r.playerId}\`  ·  **${r.tier}**  →  **${r.newBal.toLocaleString()} caps** *(+${r.amount})*`),
    ...results.failed.map(r => `\`${r.playerId}\`  ·  **${r.tier}**  —  *ledger write failed*`),
  ];
  const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Weekly Wages Disbursed")
    .setDescription(`> *${randomQuote("wages")}*\n\n${DIVIDER}\n**${results.paid.length}** paid  ·  **${results.skipped.length}** skipped  ·  **${results.failed.length}** failed`)
    .setFooter({ text: "The House always pays its debts." }).setTimestamp();
  for (const f of chunkFields(lines, "Payout Ledger")) embed.addFields(f);
  brand(embed); await logAction(embed);
}

// ---- leaderboards: caps/playtime boards, player list, live dashboard (extracted to ./leaderboards) ----
const { buildDashboardEmbed, buildLeaderboardData, buildLeaderboardEmbed, buildPlayerListEmbed, buildPlaytimeLeaderboardData, buildPlaytimeLeaderboardEmbed, dashboardSnapshots, getAutopostMsgId, hudRow, loadAutopostState, postDashboard, postLeaderboard, postPlayerList, postPlaytimeLeaderboard, purgeChannel, rankLabel, refreshLeaderboardChannels, serverSnapshot, setAutopostMsgId } = require("./leaderboards")({
  ACTIVE_SERVERS, ALL_FACTIONS, APPEAL_LINK, ActionRowBuilder, ActivityType, BAN_DURATIONS,
  BAN_REASON_LABELS, BAN_RECONCILE_MIN_INTERVAL_MS, BLACKLIST_IDS, BOT_AUTHOR, BOT_COPYRIGHT, BOT_START_MS,
  BOT_VERSION, BRAND_NAME, BUILD_ID, ButtonBuilder, ButtonStyle, CACHE_TTL_MS,
  CARD_RANKS, CARD_SUITS, CASINO_CONFIG_DEFAULTS, CLIN, CURRENT_LOG_LEVEL, Client,
  ComponentType, DASHBOARD_CHANNEL, DASHBOARD_INTERVAL_MS, DAY_MS, DB_EXPORT_INTERVAL_MS, DIVIDER,
  DONATOR_FILE, EXTRA_FACTION_FILES, EmbedBuilder, FACTION_BAK_DIR, FACTION_BOT, FACTION_BULK_DROP_LIMIT,
  FACTION_DEFAULT_CAP, FACTION_RANKS, FACTION_ROLES_PATH, FACTION_SPAWN_MAP, FILES, GAMBLE_QUOTA_MAX,
  GAMBLE_QUOTA_WINDOW_MS, GAME_ICON, GIT_SAFE, GLYPH, GatewayIntentBits, IPHUB_API_KEY,
  IPINFO_TOKEN, IPQS_API_KEY, JACKPOT_MIN_BALANCE, JACKPOT_WIN_CHANCE, KILLFEED_CHANNEL, LEADERBOARD_INTERVAL_MS,
  LEADERBOARD_TOP_N, LINK_APPROVER_ROLE, LINK_REQUEST_CHANNEL, LOCK_FILE, LOG_FILE, LOG_LEVEL,
  MASTER_NAMES, MENUS, MENU_PANEL_CHANNEL, MENU_ROLE_DEFAULTS, MODSAVE_REL, MODSAVE_SYNC_INTERVAL_MS,
  MODSAVE_SYNC_SKIP, MessageFlags, ModalBuilder, NV, OWNER_IDS, PAVLOV_BASES,
  PAVLOV_BASE_1, PLAYERLIST_CHANNEL, PLAYERLIST_INTERVAL_MS, PLAYTIME_LB_CHANNEL, PUNISHMENTS, PUNISH_BY_VALUE,
  PUNISH_CHOICES, PermissionFlagsBits, QUOTES, RCON_BLACKLIST_ROLE_ID, RCON_HEALTH_INTERVAL_MS, REST,
  ROULETTE_COLOR_EMOJI, ROULETTE_RED, ROULETTE_SPACES, RULE, RUSSIAN_ROULETTE_MULTS, RoleSelectMenuBuilder,
  Routes, SLOT_SYMBOLS, SPAWN_FILE_MAP, STAFF_MENU_ID, SlashCommandBuilder, StringSelectMenuBuilder,
  TextInputBuilder, TextInputStyle, UFW_BLOCK, UNBARRED_IDS, UPDATE_LOG_CHANNEL, WAGE_INTERVAL_MS,
  WAGE_TIERS, WebhookClient, _IPV4_RE, __dirname, _backfillGeo, _blAllCache,
  _doVpnCheck, _hasRole, _ledgerQueues, _modLogIndexCache, _reconcileBusy, _regionName,
  _sameId, _sweepBusy, _vpnInFlight, acquireSingleInstanceLock, addDonator, addPlayerToRankFile,
  addToPot, addUserBlacklist, adminOnlyEmbed, allCachedPlayers, applyMuteOnJoin, atomicCopyPreservingMtime,
  atomicWriteFile, autoBanDecision, awaitOwnedComponent, backupFactionFile, banWithIp, bar,
  blacklistAdd, blacklistAll, blacklistAllCached, blacklistHas, blacklistPathFor, blacklistRemove,
  blacklistStatus, blacklistedEmbed, brand, brandIcon, buildFactionMembershipIndex, buildModsaveBanlist,
  cardValue, casinoIntake, casinoResultEmbed, cell, checkAutoRotate, checkGambleQuota,
  checkRateLimit, checkVpn, checkVpnAndAlert, chunkFields, clampEmbed, clearMute,
  client, clinical, commandPlayerCandidates, commitSubjectsBetween, confirmDialog, countFactionRank,
  creditCaps, currentGitCommit, currentPot, customEmoji, debitCaps, deniedEmbed,
  disableValidators, discoverPavlovBases, dmPunishmentNotice, dmStatusField, drainPot, easternClock,
  easternNoonUTC, embedToText, emptyIdEmbed, enforceBansSweep, ensureFactionFiles, ensureFile,
  errorEmbed, execFile, execFileSync, exportDbToJson, extractPlayerNames, factionClient,
  factionKillBreakdown, factionLeaderOnlyEmbed, factionLeaderStrictEmbed, fclient, feedHook, firewallBlockIps,
  firewallField, firewallResyncAll, firewallUnblockIps, fixAutoBanReasons, formatFullLocation, formatHand,
  formatKD, formatPlaytime, formatTimeLeft, formatUptime, freshDeck, fs,
  gagEverywhere, gambleQuotaLimitEmbed, geoLookup, getFactionCap, getFactionDefaultRank, getFactionMembers,
  getFactionRank, getFactionRankBadge, getFactionRankCap, getFactionRankCaps, getFactionRankConfig, getFactionRankOrder,
  getKnownPlayerChoices, getLastSeen, getModsavePath, getMute, getOnlinePlayers, getPlayerChoices,
  getPlayerFactions, getPlayerFilePath, getPlayerHistory, getPlayerRanks, getServerConfig, handValue,
  hardEnforce, hasAdminRole, hasFactionLeaderRole, hasModRole, hasServer2, hasServer3,
  healTreeOwnership, hero, importBlacklistToBans, importModsaveBanlist, intendedOwner, ipBans,
  isBlackjack, isBlacklisted, isDonator, isMasterName, isOwner, isPidAlive,
  isPlayerOnline, isRealBan, listFilesRec, loadAutoRotate, loadBans, loadCasinoConfig,
  loadDonatorSuspends, loadFactionAudit, loadFactionBackup, loadFactionConfig, loadFactionRanks, loadKnownPlayers,
  loadLastSeen, loadMenuGrants, loadMenuRoles, loadModLog, loadMutes, loadPlaytime,
  loadRoles, loadVpnChecks, loadWages, log, logAction, logBan,
  logger, looksLikeLedgerEntry, matchTreeOwner, md5, memberHasRoleId, menuRoleTiers,
  meter, mirrorPaths, modOnlyEmbed, modsaveBanlistPath, mutateBalance, onlineServersOf,
  ownerOnlyEmbed, paginate, parseClockTime, parseDuration, parseRcon, patchInteractionOutput,
  path, pip, playerCache, postFeed, postKillFeed, postToUpdateLogChannel,
  postUpdateLogIfChanged, preserveBalanceAcrossKick, processDonatorRestores, processExpiredBans, processWagePayout, punishDurationLabel,
  randomQuote, rankBadge, rankHasRoom, rankWeight, rateLimitEmbed, rateLimits,
  readBlacklist, readDonatorFile, readFactionFile, readPlayerBalance, reconcileBans, reconcileBlacklists,
  recordKnownPlayers, recordLastSeen, refreshPlayerCache, releaseSingleInstanceLock, removeBans, removeDonator,
  removeFactionRank, removePlayerFromAllRankFiles, removePlayerFromRankFile, removeUserBlacklist, rouletteColor, safeRead,
  safeWrite, sanitizeBanName, sanitizeId, sanitizeMessage, saveCasinoConfig, saveFactionBackup,
  savePlaytime, saveRoles, saveUnbarred, saveUserBlacklist, saveVpnCheck, saveWages,
  scheduleBanRecheck, seedKnownPlayers, sendRcon, sendRconBoth, sendRconRaw, serverEmoji,
  serverLabel, setAutoRotate, setFactionCap, setFactionRank, setFactionRankCap, setMenuRole,
  setMute, setPlayerCacheFromData, sourceBanFor, spawn, spinRoulette, spinSlotReel,
  spinSlots, splitPages, successEmbed, suspendDonator, syncAllModSave, syncModsaveBanlist,
  syncPlayerLedger, textify, textifyChunks, tickPlaytime, unbanEverywhere, ungagEverywhere,
  update, upsertPermBan, upsertTempBan, validateConfig, warningEmbed, wipeAllMoney,
  wipeFaction, wouldWipeBalance, writeDonatorFile, writeFactionAudit, writeFactionFile, writeGameFile,
  writeGameFileSingle, writeModLog, writePlayerBalance,
});

/* Find the Discord user to DM for a Pavlov username, by matching the guild member
   whose server NICKNAME (or display name) equals the name. Returns a User or null. */
/* Explicit Discord <-> Pavlov links (set by the owner via /link). Keyed by Discord id:
   { [discordId]: { name, at, by } }. Takes priority over the nickname-search fallback. */
const loadDiscordLinks = () => safeRead(FILES.DISCORD_LINKS, {});
function setDiscordLink(discordId, name, by) { return update(FILES.DISCORD_LINKS, {}, (m) => { m[discordId] = { name, at: Date.now(), by }; return m; }); }
function removeDiscordLink(discordId)        { return update(FILES.DISCORD_LINKS, {}, (m) => { delete m[discordId]; return m; }); }
// Pavlov username -> linked Discord id (or null).
function discordIdForPavlov(name) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key) return null;
  const links = loadDiscordLinks();
  for (const [id, v] of Object.entries(links)) if (String(v?.name ?? "").toLowerCase() === key) return id;
  return null;
}

async function dmUserForPavlov(name, guild) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key) return null;
  // 1) explicit owner-set link wins.
  const linkedId = discordIdForPavlov(name);
  if (linkedId) { try { return await client.users.fetch(linkedId); } catch { /* fall through */ } }
  // 2) fall back to matching a guild member's nickname / display name.
  if (!guild) return null;
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
   grants the RCON menu matching their HIGHEST Discord role (High Staff > Staff >
   Faction). High Staff also gets Mod + Access Manager. */
async function ensureMenuPanel() {
  if (!MENU_PANEL_CHANNEL) return;
  let ch; try { ch = await client.channels.fetch(MENU_PANEL_CHANNEL); } catch { return; }
  if (!ch?.isTextBased()) return;
  const saved = safeRead(FILES.MENU_PANEL, {});
  if (saved.id) { try { await ch.messages.fetch(saved.id); return; } catch {} }   // panel still there
  const embed = clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("RCON Menu Access")
    .setDescription(`${hero("Tools for those the NCR trusts.")}\nPress **Get Menu** and enter your **exact** Pavlov in-game name. The bot grants the menu that matches your highest staff role automatically — no admin needed.\n\nOne RCON name per Discord account. Enter **your own name again** any time to remove your menu and redo it.`));
  const row = new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("menu_start").setLabel("Get Menu").setStyle(ButtonStyle.Success));
  try { const m = await ch.send(textify({ embeds: [embed], components: [row] })); safeWrite(FILES.MENU_PANEL, { id: m.id }); }
  catch (e) { logger.warn("MenuPanel", `panel post failed: ${e.message}`); }
}

/* Log linking a Discord user to the RCON name they claimed a menu for. One active
   menu per Discord user — they can't claim a second/other name while their grant is
   on record; re-entering their own name removes it (toggle), same as whitelists. */
const loadMenuLinks = () => safeRead(FILES.MENU_LINKS, {});
function setMenuLink(discordId, data) { return update(FILES.MENU_LINKS, {}, (m) => { m[discordId] = data; return m; }); }
function clearMenuLink(discordId)     { return update(FILES.MENU_LINKS, {}, (m) => { delete m[discordId]; return m; }); }
// Active only while their name still holds a recorded grant (an admin /stripmenu frees them).
function menuLinkActive(link) { return !!(link && link.name && (loadMenuGrants()[String(link.name).toLowerCase()] || []).length); }

async function handleMenuPanelSubmit(interaction) {
  // RCON blacklist role — self-serve is entirely off for these members.
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
        .setDescription(`${interaction.user} removed their own menu (was \`${link.name}\`).`)));
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
      .setDescription("You don't hold a High Staff, Staff, or Faction RCON role, so there's no menu to grant. Ask an admin if this is wrong."))], flags: MessageFlags.Ephemeral });
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
    .setDescription(`${interaction.user} self-granted **${meta.name}** to \`${name}\`.`)));
  return interaction.editReply({ embeds: [embed] });
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
   exact grant(s) on record — nothing is granted that an admin didn't grant. */
const _recentRegrant = new Map();   // nameLower -> ts (don't re-grant more than once per 2 min)

/* Master names get a menu handed to them automatically on join — GiveMenu with NO
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
   Staff/High Staff menu holders. NOTE: this is /flush immunity ONLY — it does NOT
   exempt anyone from ban-evasion auto-bans. Only MASTER names bypass auto-ban (see
   onAutoBan); staff/donators sharing an IP with an evader are still enforced.
   Faction-menu holders are NOT trusted (they can be any member). */
function isProtectedPlayer(name) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key) return false;
  if (isMasterName(key)) return true;
  if (isDonator(key)) return true;
  const grants = loadMenuGrants()[key] || [];
  return grants.some(g => g.menuValue === "staff" || g.menuValue === "highstaff");
}

/* Auto-ban exemption: a player who was explicitly UNBANNED is never auto-banned again
   (even if a lingering flag — e.g. a shared IP with a still-banned evader — would match),
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
setInterval(checkAutoRotate,         60_000);   // scheduled map rotation (Eastern time)
setInterval(postLeaderboard,         LEADERBOARD_INTERVAL_MS);
setInterval(postPlaytimeLeaderboard, LEADERBOARD_INTERVAL_MS);
setInterval(postPlayerList,          PLAYERLIST_INTERVAL_MS);
if (DASHBOARD_CHANNEL) setInterval(postDashboard, DASHBOARD_INTERVAL_MS);
setInterval(rconHealthCheck,         RCON_HEALTH_INTERVAL_MS);
setInterval(async () => {
  await refreshPlayerCache("server1");
  if (hasServer2) await refreshPlayerCache("server2");
  if (hasServer3) await refreshPlayerCache("server3");
  tickPlaytime();
}, 60_000);
setInterval(processWagePayout, WAGE_INTERVAL_MS);

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
setTimeout(postPlaytimeLeaderboard, 25_000);
setTimeout(() => {
  const due = loadWages().filter(w => WAGE_TIERS[w.tier]?.weekly && (!w.lastPaidAt || Date.now() - w.lastPaidAt >= WAGE_INTERVAL_MS * 0.9));
  if (due.length) { logger.info("Wages", `${due.length} overdue payout(s), processing in 15s...`); setTimeout(processWagePayout, 15_000); }
}, 5_000);
}


// Daily faction-whitelist auto-backup, so there's always a recent snapshot to /configure -> Load.
function autoBackupFactions() {
  const r = saveFactionBackup();
  if (r.ok) logger.info("Backup", `Auto-saved faction whitelists — ${r.count} file(s)`);
  else logger.warn("Backup", `Auto faction backup skipped: ${r.error}`);
}

// ---- slash command definitions ----
function serverOption(o) {
  return o.setName("server").setDescription("Which server to target").setRequired(true)
    .addChoices({ name: "Server 1", value: "server1" }, { name: "Server 2", value: "server2" }, { name: "Server 3", value: "server3" }, { name: "All", value: "both" });
}

const factionChoices = ALL_FACTIONS.map(f => ({ name: f, value: f }));

const ALL_RANK_NAMES = [...new Set(
  Object.values(FACTION_RANKS).flatMap(cfg => cfg.order)
)].map(r => ({ name: r, value: r }));


const commands = [
  new SlashCommandBuilder().setName("help").setDescription("Show all commands and your current access level"),
  new SlashCommandBuilder().setName("ping").setDescription("Bot and server health check with uptime"),
  new SlashCommandBuilder().setName("dashboard").setDescription("Live server status dashboard (auto-refreshes for 5 min)"),
  new SlashCommandBuilder().setName("serverinfo").setDescription("Server info: map, mode, player count").addStringOption(serverOption),
  new SlashCommandBuilder().setName("find")
    .setDescription("Search for a player by partial name across both servers")
    .addStringOption(o => o.setName("name").setDescription("Partial or full player name").setRequired(true)),
  new SlashCommandBuilder().setName("kick")
    .setDescription("Eject a courier from the server")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("reason").setDescription("Reason for ejection"))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("mute")
    .setDescription("In-game mute (gag) a courier for a set time — re-applied every join until it expires")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("duration").setDescription("How long — e.g. 30s, 10m, 2h, 1d").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for the mute")),
  new SlashCommandBuilder().setName("unmute")
    .setDescription("Lift a courier's in-game mute now")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("flush")
    .setDescription("Randomly kick one online player from a server")
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("staffactivity")
    .setDescription("Admin — All moderation actions taken by a staff member")
    .addUserOption(o => o.setName("staff").setDescription("Staff member to audit").setRequired(true)),
  new SlashCommandBuilder().setName("tempban")
    .setDescription("Ban a courier — the punishment sets the duration (Hard R = permanent; Other = custom date)")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("reason").setDescription("Punishment — sets the ban length automatically").setRequired(true).addChoices(...PUNISH_CHOICES))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("date").setDescription("Only for 'Other': unban date YYYY-MM-DD (lifts 12pm Eastern that day)").setRequired(false))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("unban")
    .setDescription("Lift a courier's exile")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID to pardon").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("checkban")
    .setDescription("Check if a courier is currently exiled")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("banlist")
    .setDescription("List all active bans"),
  new SlashCommandBuilder().setName("permban")
    .setDescription("Admin — Permanently exile a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("reason").setDescription("Grounds").setRequired(true).addChoices(...PUNISH_CHOICES))
    .addStringOption(o => o.setName("notes").setDescription("Additional context"))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("cleartempbans").setDescription("Admin — Clear all temporary exiles (confirmation required)"),
  new SlashCommandBuilder().setName("donator")
    .setDescription("Admin — Manage the donator whitelist file")
    .addSubcommand(s => s.setName("add")
      .setDescription("Add a player to the donator file")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a player from the donator file")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all players in the donator file")),
  new SlashCommandBuilder().setName("setroles")
    .setDescription("Admin — Configure role permissions")
    .addRoleOption(o => o.setName("mod_role").setDescription("Moderator role"))
    .addRoleOption(o => o.setName("admin_role").setDescription("Admin role"))
    .addRoleOption(o => o.setName("faction_leader_role").setDescription("Faction Leader role")),
  new SlashCommandBuilder().setName("announce")
    .setDescription("Mod — Broadcast a message via RCON Notify")
    .addStringOption(o => o.setName("message").setDescription("Message to broadcast (max 200 chars)").setRequired(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("target").setDescription("Who to notify: a specific courier, or All").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("givemenu")
    .setDescription("Admin — Grant RCON menu access to a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("menu").setDescription("Menu to grant").setRequired(true)
      .addChoices(...MENUS.map(m => ({ name: m.name, value: m.value })))),
  new SlashCommandBuilder().setName("stripmenu")
    .setDescription("Admin — Revoke ALL RCON menu access from a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("stripmenuall")
    .setDescription("Owner — Clear ALL menu access from every player (both servers)"),
  new SlashCommandBuilder().setName("configure")
    .setDescription("Owner menu"),
  new SlashCommandBuilder().setName("link")
    .setDescription("Link your Discord account to your Pavlov username")
    .addSubcommand(s => s.setName("add")
      .setDescription("Request to link YOUR Discord to your Pavlov username (staff approves)")
      .addStringOption(o => o.setName("pavlov").setDescription("Your exact Pavlov in-game username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Mod — Remove a Discord account's link")
      .addUserOption(o => o.setName("discord_user").setDescription("The Discord account").setRequired(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("Mod — Show all Discord ↔ Pavlov links")),
  new SlashCommandBuilder().setName("clearallbans")
    .setDescription("Owner — Unban everyone (clears blacklist.txt on both servers)"),
  new SlashCommandBuilder().setName("setrconroles")
    .setDescription("Admin — Set the Discord roles that grant each RCON menu (self-service panel)")
    .setDefaultMemberPermissions(PermissionFlagsBits.Administrator)
    .addRoleOption(o => o.setName("high_staff_role").setDescription("Role that grants the High Staff menu"))
    .addRoleOption(o => o.setName("staff_role").setDescription("Role that grants the Staff menu"))
    .addRoleOption(o => o.setName("faction_role").setDescription("Role that grants the Faction menu")),
  /* ── FACTION ─────────────────────────────────────────── */
  new SlashCommandBuilder()
    .setName("faction")
    .setDescription("Manage faction whitelists, ranks, and rosters")
    .addSubcommand(s => s.setName("add")
      .setDescription("Add a player to a faction whitelist")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Starting rank (faction-specific, default is lowest rank)").setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a player from a faction whitelist")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID (pick the faction first)").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("rank")
      .setDescription("Faction Leader — Add or remove a rank for a member (a member can hold several)")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID (pick the faction first)").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("rank").setDescription("Rank to add (faction-specific)").setRequired(true).setAutocomplete(true))
      .addBooleanOption(o => o.setName("remove").setDescription("Remove this rank instead of adding it")))
    .addSubcommand(s => s.setName("transfer")
      .setDescription("Mod — Transfer a player from one faction to another")
      .addStringOption(o => o.setName("from_faction").setDescription("Current faction").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("to_faction").setDescription("Destination faction").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID (pick the current faction first)").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("rank").setDescription("Rank in new faction (default: lowest rank)").setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all members of a faction with their ranks")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("audit")
      .setDescription("View recent add/remove/rank changes for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("playtime")
      .setDescription("Whitelisted members' playtime, highest to lowest")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("setcap")
      .setDescription("Admin — Set the maximum member cap for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addIntegerOption(o => o.setName("cap").setDescription("Maximum number of members (1–500)").setRequired(true).setMinValue(1).setMaxValue(500)))
    .addSubcommand(s => s.setName("setrankcap")
      .setDescription("Admin — Set the per-rank member cap within a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Rank to cap (faction-specific)").setRequired(true).setAutocomplete(true))
      .addIntegerOption(o => o.setName("cap").setDescription("Max members at this rank (0 = unlimited)").setRequired(true).setMinValue(0).setMaxValue(500)))
    .addSubcommand(s => s.setName("wipe")
      .setDescription("Owner — Reset a faction's whitelist (or all of them), clearing every member and rank")
      .addStringOption(o => o.setName("faction").setDescription("Faction to wipe (omit to wipe ALL factions)").addChoices(...factionChoices))),

  new SlashCommandBuilder().setName("manual")
    .setDescription("Admin — Send a raw RCON command")
    .addStringOption(o => o.setName("command").setDescription("Raw RCON signal").setRequired(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("autorotate")
    .setDescription("Owner — Schedule a daily map rotation at a set Eastern time")
    .addSubcommand(s => s.setName("set")
      .setDescription("Rotate the map every day at this Eastern (EST/EDT) time")
      .addStringOption(o => o.setName("time").setDescription("Time — e.g. 03:00, 18:30, 3pm, 6:30pm (Eastern)").setRequired(true))
      .addStringOption(serverOption))
    .addSubcommand(s => s.setName("off").setDescription("Turn off the scheduled map rotation"))
    .addSubcommand(s => s.setName("status").setDescription("Show the current rotation schedule")),
  new SlashCommandBuilder().setName("addwage")
    .setDescription("Enrol a courier in payroll or issue a one-time mercenary payment")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("tier").setDescription("Payment tier").setRequired(true)
      .addChoices(
        { name: "Low Rank  — 400 caps/week",       value: "low_rank"  },
        { name: "Mid Rank  — 500 caps/week",       value: "mid_rank"  },
        { name: "High Rank — 650 caps/week",       value: "high_rank" },
        { name: "Mercenary — 200 caps (one-time)", value: "mercenary" }
      )),
  new SlashCommandBuilder().setName("removewage")
    .setDescription("Remove a courier from the weekly payroll")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("wagelist").setDescription("View all couriers on the weekly payroll"),
  new SlashCommandBuilder().setName("checkbalance")
    .setDescription("Check a courier's caps balance")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("givecaps")
    .setDescription("Give caps to a courier (faction leader / mod command)")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Caps to give").setRequired(true).setMinValue(1).setMaxValue(10000))
    .addStringOption(o => o.setName("reason").setDescription("Reason (shown in logs)")),
  new SlashCommandBuilder().setName("adjustcaps")
    .setDescription("Admin — Manually add or subtract caps from a courier's ledger")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Caps to add (positive) or subtract (negative)").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for adjustment (logged)")),
  new SlashCommandBuilder().setName("stats")
    .setDescription("Courier dossier: playtime, factions, balance, and mod history")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  // Owner-only deep inspection (gated in the handler; not listed in /help). Discord
  // requires a non-empty description — a zero-width one gets the whole PUT rejected.
  new SlashCommandBuilder().setName("inspect")
    .setDescription("Owner: full record for a courier — IPs, VPN checks, alts, flags")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)),
  // Owner-only manual OS-firewall (ufw) control — block/unblock an IP by hand,
  // independent of any ban. Gated in the handler; requires UFW_BLOCK=1.
  new SlashCommandBuilder().setName("firewall")
    .setDescription("Owner: block or unblock an IP at the OS firewall (ufw)")
    .addSubcommand(s => s.setName("block")
      .setDescription("Block an IP at the firewall — sudo ufw insert 1 deny from <ip>")
      .addStringOption(o => o.setName("ip").setDescription("IPv4 address to block").setRequired(true)))
    .addSubcommand(s => s.setName("unblock")
      .setDescription("Remove a firewall block for an IP — sudo ufw delete <rule>")
      .addStringOption(o => o.setName("ip").setDescription("IPv4 address to unblock").setRequired(true))),
  new SlashCommandBuilder().setName("kd")
    .setDescription("Kill/death stats — a courier's K/D, or the leaderboard")
    .addStringOption(o => o.setName("playerid").setDescription("Courier (leave blank for the K/D leaderboard)").setRequired(false).setAutocomplete(true)),
  /* ── CASINO ─────────────────────────────────────────── */
  new SlashCommandBuilder().setName("slots")
    .setDescription("Pull the slot machine — wager caps for a shot at the jackpot")
    .addIntegerOption(o => o.setName("bet").setDescription("Caps to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000)),
  new SlashCommandBuilder().setName("coinflip")
    .setDescription("Call it — heads or tails, double or nothing")
    .addIntegerOption(o => o.setName("bet").setDescription("Caps to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000))
    .addStringOption(o => o.setName("call").setDescription("Your call").setRequired(true)
      .addChoices({ name: "Heads", value: "heads" }, { name: "Tails", value: "tails" })),
  new SlashCommandBuilder().setName("blackjack")
    .setDescription("Play a hand of blackjack against the dealer")
    .addIntegerOption(o => o.setName("bet").setDescription("Caps to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000)),
  new SlashCommandBuilder().setName("roulette")
    .setDescription("Place a bet on the wheel")
    .addIntegerOption(o => o.setName("bet").setDescription("Caps to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000))
    .addStringOption(o => o.setName("space").setDescription("Outside bet (ignored if a number is given)")
      .addChoices(
        { name: "Red (2x)",            value: "red"    },
        { name: "Black (2x)",          value: "black"  },
        { name: "Even (2x)",           value: "even"   },
        { name: "Odd (2x)",            value: "odd"    },
        { name: "1-18 / Low (2x)",     value: "low"    },
        { name: "19-36 / High (2x)",   value: "high"   },
        { name: "1st 12  1-12 (3x)",   value: "1st12"  },
        { name: "2nd 12  13-24 (3x)",  value: "2nd12"  },
        { name: "3rd 12  25-36 (3x)",  value: "3rd12"  },
      ))
    .addIntegerOption(o => o.setName("number").setDescription("Straight-up bet on a single number 0-36 (36x, overrides space)").setMinValue(0).setMaxValue(36)),
  new SlashCommandBuilder().setName("cockfight")
    .setDescription("Wager caps on a duel — challenge another courier, or the house")
    .addIntegerOption(o => o.setName("bet").setDescription("Caps to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000))
    .addUserOption(o => o.setName("opponent").setDescription("Challenge this courier instead of the house")),
  new SlashCommandBuilder().setName("russianroulette")
    .setDescription("Push your luck — pull the trigger for a rising multiplier, or cash out")
    .addIntegerOption(o => o.setName("bet").setDescription("Caps to wager").setRequired(true).setMinValue(1).setMaxValue(1_000_000)),
  new SlashCommandBuilder().setName("jackpot")
    .setDescription(`Bet your ENTIRE balance for a shot at the casino jackpot (min ${JACKPOT_MIN_BALANCE.toLocaleString()} caps)`),
  new SlashCommandBuilder().setName("casino")
    .setDescription("Admin — Configure the casino")
    .setDefaultMemberPermissions(PermissionFlagsBits.Administrator)
    .addSubcommand(s => s.setName("status").setDescription("Show the current casino config"))
    .addSubcommand(s => s.setName("toggle")
      .setDescription("Enable or disable gambling server-wide")
      .addBooleanOption(o => o.setName("enabled").setDescription("On or off").setRequired(true)))
    .addSubcommand(s => s.setName("setlimits")
      .setDescription("Set the min/max bet")
      .addIntegerOption(o => o.setName("min").setDescription("Minimum bet").setRequired(true).setMinValue(1))
      .addIntegerOption(o => o.setName("max").setDescription("Maximum bet").setRequired(true).setMinValue(1))),
].map(c => c.toJSON());

// Partition: faction commands live on the faction bot when it's configured.
const FACTION_COMMAND_NAMES = new Set(["faction"]);
const mainCommands    = FACTION_BOT ? commands.filter(c => !FACTION_COMMAND_NAMES.has(c.name)) : commands;
const factionCommands = commands.filter(c => FACTION_COMMAND_NAMES.has(c.name));

// ---- ready ----
client.once("clientReady", async () => {   // "ready" is deprecated in discord.js 14.22+
  logger.info("Bot", `${client.user.tag} online — v${BOT_VERSION}`);
  try {
    client.user.setPresence({
      activities: [{ name: "over the Mojave from the Lucky 38  ·  /help", type: ActivityType.Watching }],
      status: "online",
    });
  } catch (err) {
    logger.warn("Bot", `Could not set presence: ${err.message}`);
  }
  const rest = new REST({ version: "10" }).setToken(process.env.DISCORD_TOKEN);
  try {
    const result = await rest.put(Routes.applicationCommands(process.env.CLIENT_ID), { body: mainCommands });
    logger.info("Bot", `${result.length} slash commands registered`);
  } catch (err) {
    logger.error("Bot", `Command registration failed: ${err.message}`);
  }
  seedKnownPlayers();   // backfill the offline-autocomplete registry from existing data
  postUpdateLogIfChanged().catch(err => logger.warn("UpdateLog", err.message));   // announce this deploy, if it's a new one
  // Re-apply ufw blocks for every currently-active ban's IP at position 1 (self-heals any
  // previously-appended rules). Delayed so it doesn't slow boot; off unless UFW_BLOCK.
  if (UFW_BLOCK) setTimeout(() => { firewallResyncAll().catch(err => logger.warn("Firewall", `resync failed: ${err.message}`)); }, 15_000);
  // Watch EVERY install's Pavlov.log (server 1, 2, …) - derived from the discovered
  // installs, unioned with any explicit PAVLOV_LOGS, so server 2 is never missed.
  const ipLogFiles = [...new Set([
    ...String(process.env.PAVLOV_LOGS ?? "").split(/[,:]/).map(s => s.trim()).filter(Boolean),
    ...PAVLOV_BASES.map(b => path.join(b, "Pavlov/Saved/Logs/Pavlov.log")),
  ])];
  logger.info("IPBans", `Watching ${ipLogFiles.length} server log(s): ${ipLogFiles.join(", ")}`);
  // Map each watched log's label (the install dir name ipBans tags lines with) to a
  // friendly "Server N" by INSTALL ORDER - robust regardless of folder naming, so
  // server 2's feed isn't mislabeled as server 1.
  const labelOfLog = (f) => { const m = String(f).match(/([^/\\]+)[/\\]Pavlov[/\\]/i); return m ? m[1] : path.basename(path.dirname(f)); };
  const serverNameByLabel = new Map();
  PAVLOV_BASES.forEach((b, i) => serverNameByLabel.set(path.basename(b), `Server ${i + 1}`));   // install order = Server 1, 2, …
  let _extra = PAVLOV_BASES.length;
  ipLogFiles.forEach((f) => { const l = labelOfLog(f); if (!serverNameByLabel.has(l)) serverNameByLabel.set(l, `Server ${++_extra}`); });
  logger.info("IPBans", `Server labels: ${[...serverNameByLabel].map(([l, n]) => `${l}=${n}`).join(", ")}`);
  ipBans.init({
    logFiles: ipLogFiles,
    masters: [...MASTER_NAMES],   // never IP-logged / fed / auto-banned
    // Fired on every LIVE join - re-grant recorded RCON menus (the server drops a
    // player's menu on disconnect, so a rejoin needs the grant re-applied).
    onConnect: async ({ name, ip, confident }) => {
      // Pull the newest caps ledger onto every install BEFORE this server reads it,
      // so a hop from another server carries their money over.
      try { syncPlayerLedger(name); } catch (e) { logger.warn("Sync", `join ledger sync failed for ${name}: ${e.message}`); }
      // Re-apply an active gag on join, or lift an expired one — for everyone.
      try { applyMuteOnJoin(name); } catch (e) { logger.warn("Mute", `mute-on-join failed for ${name}: ${e.message}`); }
      // Master names get a menu handed to them on every join (no bit code).
      if (isMasterName(name)) { try { grantMasterMenu(name); } catch (e) { logger.warn("Menus", `master menu failed: ${e.message}`); } return; }
      try { scheduleMenuRegrant(name); } catch (e) { logger.warn("Menus", `re-grant schedule failed: ${e.message}`); }
      // VPN check at JOIN so a VPN/proxy user is banned AND kicked while still online —
      // not just blocked on their next rejoin. Only act on a `confident` (unambiguous)
      // join IP so a mis-correlated IP can't kick the wrong player; checkVpnAndAlert
      // caches per IP and re-checks nothing. The disconnect (onConfirm) check remains a
      // backstop for ambiguous joins. Masters already returned above.
      if (ip && confident && IPHUB_API_KEY && !isAutobanExempt(name)) {
        checkVpnAndAlert(name, ip).catch(err => logger.warn("VPN", `join check failed for ${name}: ${err.message}`));
      }
    },
    // Fired once a player's IP is CONFIRMED (the same-line disconnect pairing) -
    // posts an accurate name, ID, IP entry to the connection-feed webhook.
    onConfirm: async ({ name, ip, server, record }) => {
      // The economy mod saves the balance at disconnect — propagate it to the other
      // installs now (and once more a few seconds later for a slow save write), so
      // the caps are already there if they hop to another server.
      try { syncPlayerLedger(name); } catch {}
      setTimeout(() => { try { syncPlayerLedger(name); } catch {} }, 5_000);
      let vpnResult = null;
      try { vpnResult = await checkVpnAndAlert(name, ip); }   // runs the check + auto-ban; returns the verdict for the feed
      catch (err) { logger.warn("VPN", `check failed for ${name}: ${err.message}`); }
      // Even with the VPN keys unset, still fetch geolocation for the feed.
      if (!vpnResult && ip) { try { vpnResult = await checkVpn(ip); } catch {} }
      if (!feedHook) return;   // IP connection feed only runs when CONNECT_WEBHOOK_URL is set
      const srvName = serverNameByLabel.get(String(server)) || "Server 1";
      // everything Pavlov.log knows about this player (resolved by id inside ipBans)
      const rec = record || { ips: [], cips: [], alts: [], firstSeen: null, lastSeen: null, recent: [], logins: 0, bypass: false, flagged: false };
      const fmt = (ms) => { if (!ms) return "unknown"; try { return new Date(ms).toISOString().replace("T", " ").slice(0, 19); } catch { return "unknown"; } };

      // recent connections - newest first; fold in this live join, dedupe near-duplicates
      const conns = [];
      if (ip) conns.push({ ts: Date.now(), ip });
      for (const c of (rec.recent || [])) conns.push(c);
      const seen = new Set(); const connLines = [];
      for (const c of conns) {
        const k = `${Math.floor((c.ts || 0) / 1000)}|${c.ip}`;
        if (seen.has(k)) continue; seen.add(k);
        connLines.push(`${fmt(c.ts)}   ${c.ip ?? "unknown"}`);
        if (connLines.length >= 8) break;
      }
      const lastActivity = Math.max(rec.lastSeen || 0, conns[0]?.ts || 0) || null;

      // VPN/proxy verdict for the IP they connected from (from the check above).
      const vpnField = !IPHUB_API_KEY               ? "Not configured (set IPHUB_API_KEY)"
        : !vpnResult                                ? "Not checked"
        : !vpnResult.flagged                        ? "Clean"
        : vpnResult.confirmed === false             ? "IPHub flagged · IPQS disputes (likely false positive)"
        : vpnResult.confirmed === true              ? "VPN/Proxy — IPHub + IPQS agree"
        :                                             "Flagged by IPHub";
      const vpnIsp    = vpnResult?.isp  ? ` · ${vpnResult.isp}` : "";
      const vpnDetail = vpnResult?.ipqs ? ` · vpn:${vpnResult.ipqs.vpn} proxy:${vpnResult.ipqs.proxy} tor:${vpnResult.ipqs.tor} fraud:${vpnResult.ipqs.fraudScore}` : "";

      const embed = clinical(new EmbedBuilder().setColor(CLIN.green)
        .setTitle(`Player Information: ${name}`)
        .setDescription(`${name} just connected on ${srvName}.`)
        .addFields(
          { name: "EOS ID",          value: rec.id ? `\`${rec.id}\`` : "unknown", inline: false },
          { name: "First Seen",      value: fmt(rec.firstSeen),                  inline: true },
          { name: "Last Seen",       value: fmt(rec.lastSeen),                   inline: true },
          { name: "Login Count",     value: String(rec.logins ?? 0),             inline: true },
          { name: "Possible Alts",   value: (rec.alts && rec.alts.length ? rec.alts.join(", ") : "None").slice(0, 1024), inline: false },
          { name: "Bypass Auto-Ban", value: rec.bypass ? "Yes" : "No",           inline: true },
          { name: "Server",          value: srvName,                             inline: true },
          { name: "Log Scan Results", value: rec.flagged ? "Flagged — matches the blacklist (auto-banned)"
            : "No matches", inline: false },
          { name: "VPN / Proxy",     value: (vpnField + vpnIsp + vpnDetail).slice(0, 1024), inline: false },
          { name: "Location",        value: (formatFullLocation(vpnResult?.geo) || (ip ? "unknown" : "no IP")).slice(0, 1024), inline: false },
          { name: "Last Activity",   value: fmt(lastActivity),                   inline: false },
          { name: "Recent Connections", value: "```\n" + (connLines.length ? connLines.join("\n") : "no records").slice(0, 1000) + "\n```", inline: false },
        ), "Connection log · Mojave Authority");
      postFeed(embed);
    },
    // Fired on every live PvP kill (ipBans already filters out suicides/environmental
    // deaths - killer is always distinct from and present alongside the victim).
    onKill: async ({ killer, killed }) => { postKillFeed(killer, killed); },
    // Fired when someone CONNECTS (live log) matching a blacklisted username/IP:
    // ban that username on both servers (Shack bans by name, not hex id).
    onAutoBan: async ({ name, uniqueId, ip, reason }) => {
      // Only MASTER names are exempt from auto-ban. Staff/donators are exempt from /flush
      // only, not from ban evasion enforcement.
      if (isMasterName(name)) { logger.info("IPGuard", `Skipped auto-ban for master name ${name}`); return; }
      // An explicitly-unbanned player is never auto-banned again (a lingering shared flag
      // must not re-catch them) until they're deliberately re-banned.
      if (isAutobanExempt(name)) { logger.info("IPGuard", `Skipped auto-ban for unban-exempt player ${name}`); return; }
      // A TEMP-banned player bouncing off their ban still shows up as a join attempt.
      const existing = loadBans().find(b => _sameId(b.playerId, name));
      const decision = autoBanDecision(existing, reason);
      if (decision === "block") {
        // Still actively (temp-)banned — force them off (their ban already stands).
        try { await hardEnforce(name, { banToo: false }); } catch {}
        logger.info("IPGuard", `${name} tried to join while banned — re-removed, no escalation`);
        return;
      }
      if (decision === "lift") {
        try { unbanEverywhere(existing.playerId); } catch {}
        try { await removeBans(existing.playerId); } catch {}
        try { await addAutobanExempt(existing.playerId, "sentence served"); } catch {}   // served ban never re-catches them
        logger.info("IPGuard", `${name} rejoined after temp-ban expiry — lifted now (no escalation)`);
        return;
      }
      // Show the REAL offense in /checkban, not "IP-Guard": inherit the reason + mod from
      // the original ban whose flag caught them (fall back to "Ban evasion").
      const src       = sourceBanFor(name, uniqueId);
      const banReason = src?.reason || "Ban evasion";
      const banMod    = "Ban evasion (auto)";   // never attribute an auto-ban to a person
      // Enforce with a native RCON Ban + Kick by username (banWithIp) + flag the exact
      // IP/EOS. Per-command server responses are logged so we can see what lands.
      const res = await banWithIp(name, "both", { permanent: true, ip });
      try { await upsertPermBan({ playerId: name, reason: banReason, moderator: banMod }); } catch {}   // show in /banlist with the real punishment
      writeModLog({ action: "auto-ipban", playerId: name, reason: `${banReason} (evasion via ${reason || "match"}${ip ? ` ${ip}` : ""})`, by: banMod });
      logger.warn("IPGuard", `Auto-banned ${name} — ${banReason} (evasion via ${reason || "match"}${ip ? ` ${ip}` : ""}), id [${uniqueId || "?"}]`);
      const banEmbed = clinical(new EmbedBuilder().setColor(CLIN.red)
        .setTitle("Auto-Ban — Ban Evasion")
        .setDescription(`${hero(randomQuote("autoban"))}\n\n**${name}** was auto-banned for ${banReason} — caught by ${reason || "match"}${ip ? ` from \`${ip}\`` : ""}${uniqueId ? ` (id \`${uniqueId}\`)` : ""}. Banned and kicked on ${res?.blacklist?.servers ?? 0}/${ACTIVE_SERVERS.length} server(s).`),
        "Auto-ban · native RCON ban · all servers");
      await logBan(banEmbed);   // dedicated ban-log channel (falls back to mod-log)
      postFeed(banEmbed);       // also surface it in the connection feed
    },
  });
  refreshPlayerCache("server1");
  if (hasServer2) refreshPlayerCache("server2");
  if (hasServer3) refreshPlayerCache("server3");
  try { healTreeOwnership(); } catch (e) { logger.warn("Init", `ownership heal failed: ${e.message}`); }
  try { const r = syncAllModSave(); if (r.installs > 1 && !r.off) logger.info("Sync", `ModSave sync on startup — ${r.synced} file(s) propagated across ${r.installs} installs`); } catch (e) { logger.warn("Sync", `ModSave sync failed: ${e.message}`); }
  try { ensureFactionFiles(); } catch (e) { logger.warn("Init", `faction file build failed: ${e.message}`); }
  try { reconcileBlacklists(); } catch (e) { logger.warn("Blacklist", `reconcile failed: ${e.message}`); }
  try { await importBlacklistToBans(); } catch (e) { logger.warn("Bans", `blacklist import failed: ${e.message}`); }
  try { await importModsaveBanlist(); } catch (e) { logger.warn("Bans", `modsave banlist import failed: ${e.message}`); }   // pull in-game-menu bans into the DB
  try { syncModsaveBanlist(); } catch {}   // then (re)build the custom ban-message file
  setTimeout(rconHealthCheck, 5_000);
  try { await fixAutoBanReasons(); } catch (e) { logger.warn("Bans", `auto-ban reason repair failed: ${e.message}`); }   // /checkban shows real punishment
  setTimeout(enforceBansSweep, 10_000);   // clear any banned players already online at startup
  setTimeout(reconcileBans,    15_000);   // rebuild the server ban list from the DB on startup
  ensureMenuPanel();
  // Wipe stale leaderboard/player-list messages from the previous run, then post fresh.
  try { await refreshLeaderboardChannels(); } catch (e) { logger.warn("Purge", `leaderboard refresh failed: ${e.message}`); }
});

// ---- graceful shutdown ----
async function shutdown(signal) {
  logger.info("Bot", `${signal} received — draining write queues…`);
  // _queues holds the tail promise of every per-file write chain; waiting on them
  // means pm2 restarts can't kill an in-flight atomic write mid-rename.
  try { await Promise.allSettled([..._queues.values()]); } catch {}
  try { ipBans.flushAll(); } catch {}   // registry / K-D / kill-log throttled writes
  try { if (DB_EXPORT_INTERVAL_MS > 0) exportDbToJson(); } catch {}   // fresh JSON backup on clean exit
  releaseSingleInstanceLock();
  logger.info("Bot", "Queues drained — exiting.");
  process.exit(0);
}
process.on("SIGINT",  () => { shutdown("SIGINT").catch(() => process.exit(1)); });
process.on("SIGTERM", () => { shutdown("SIGTERM").catch(() => process.exit(1)); });
process.on("uncaughtException",  err => logger.error("Uncaught",  err.message, { stack: err.stack }));
process.on("unhandledRejection", r   => logger.error("Unhandled", String(r)));

// ---- interactions ----
// ---- interactions (handler extracted to ./commands) ----
const { onInteraction } = require("./commands")({
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
});

client.on("interactionCreate", onInteraction);
if (factionClient) factionClient.on("interactionCreate", onInteraction);

/* Faction bot startup: register its command set + own the whitelist panels. */
if (factionClient) {
  factionClient.once("clientReady", async () => {
    logger.info("FactionBot", `${factionClient.user.tag} online (faction commands)`);
    try {
      factionClient.user.setPresence({ activities: [{ name: "faction rosters  ·  /faction", type: ActivityType.Watching }], status: "online" });
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
  buildPlaytimeLeaderboardData, savePlaytime,
  // warnings
  // bans (serialized)
  loadBans, upsertTempBan, upsertPermBan, removeBans, autoBanDecision, isRealBan, sourceBanFor,
  // donators
  DONATOR_FILE, readDonatorFile, writeDonatorFile, isDonator, addDonator, removeDonator,
  // owner / access
  isOwner, isBlacklisted, isMasterName, isProtectedPlayer,
  parseClockTime, easternClock, parseDuration,
  loadMutes, getMute, setMute, clearMute,
  loadDiscordLinks, setDiscordLink, removeDiscordLink, discordIdForPavlov,
  isAutobanExempt, addAutobanExempt, removeAutobanExempt,
  // ui / parsing helpers
  splitPages, extractPlayerNames, bar, dmStatusField,
  // faction rank caps
  getFactionRankCap, getFactionRankCaps, setFactionRankCap, getFactionCap, setFactionCap,
  // faction file safety
  readFactionFile, writeFactionFile, FACTION_BULK_DROP_LIMIT, FACTION_ROLES_PATH,
  SPAWN_FILE_MAP, ALL_FACTIONS, wipeFaction, loadFactionRanks, setFactionRank,
  // modsave sync
  syncAllModSave, syncPlayerLedger, looksLikeLedgerEntry, isPlayerOnline, playerCache,
  // rcon menu roles
  loadMenuRoles, setMenuRole,
  // casino
  loadCasinoConfig, saveCasinoConfig, mutateBalance, debitCaps, creditCaps,
  SLOT_SYMBOLS, spinSlots, ROULETTE_SPACES, rouletteColor, spinRoulette,
  freshDeck, cardValue, handValue, formatHand, isBlackjack, RUSSIAN_ROULETTE_MULTS,
  awaitOwnedComponent, checkGambleQuota, GAMBLE_QUOTA_MAX, GAMBLE_QUOTA_WINDOW_MS,
  currentPot, addToPot, drainPot, JACKPOT_MIN_BALANCE, JACKPOT_WIN_CHANCE,
  getAutopostMsgId, setAutopostMsgId,
  isPidAlive, acquireSingleInstanceLock, releaseSingleInstanceLock, LOCK_FILE,
  checkVpn, checkVpnAndAlert, loadVpnChecks, saveVpnCheck,
  // update log
  currentGitCommit, commitSubjectsBetween, postUpdateLogIfChanged,
  // ban reconciliation
  reconcileBans, BAN_RECONCILE_MIN_INTERVAL_MS,
};
