/* ================================================================
 * Mojave Authority Bot
 * Copyright (c) 2026 bs9zw69ff9-source. All rights reserved.
 *
 * PROPRIETARY — licensed, not sold. Unauthorized copying, modification,
 * distribution, or hosting (in whole or in part) is prohibited. See the
 * LICENSE file. Do not remove or alter this notice or the in-app
 * attribution; doing so violates the license.
 * ================================================================ */
require("dotenv").config();
const fs     = require("fs");
const net    = require("net");
const crypto = require("crypto");
const path   = require("path");
const ipBans = require("./ipBans");
const {
  Client,
  GatewayIntentBits,
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
  ModalBuilder,
  TextInputBuilder,
  TextInputStyle,
  WebhookClient,
} = require("discord.js");

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

/* ================================================================
   VERSION & STARTUP
   ================================================================ */
const BOT_VERSION  = "3.2.2";
const BOT_START_MS = Date.now();

/* Authorship / build attribution. Surfaced in /help and /ping; protected by
   the LICENSE. Removing or altering it violates the license terms. */
const BOT_AUTHOR    = "bs9zw69ff9-source";
const BOT_COPYRIGHT = `© 2026 ${BOT_AUTHOR} · All rights reserved`;
const BUILD_ID      = process.env.BUILD_ID || `v${BOT_VERSION}-${new Date(BOT_START_MS).toISOString().slice(0, 10)}`;

/* ================================================================
   HARDCODED OWNERS  (super-users — top of every permission)
   ================================================================
   These Discord user IDs are baked into the source. They bypass ALL
   permission checks (admin/mod/faction leader), skip rate limits, and
   can never be blacklisted. The only way to change this list is to edit
   the code — it is not exposed through any command or data file.
   ================================================================ */
const OWNER_IDS = new Set([
  "1014251293159731310",
  "678362059905171471",
]);
function isOwner(userId) { return OWNER_IDS.has(String(userId)); }

/* Roles granted when a staff application is accepted (see /acceptstaffapp). */
const STAFF_ROLE_IDS = ["1517243775175622808", "1498172888224628776"];
/* Channel where accepted-staff welcome announcements are posted. */
const STAFF_ANNOUNCE_CHANNEL = "1516187330145161278";

/* ================================================================
   STRUCTURED LOGGER
   ================================================================ */
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

/* ================================================================
   CONFIG VALIDATION
   ================================================================ */
function validateConfig() {
  const required = [
    "DISCORD_TOKEN", "CLIENT_ID",
    "RCON_HOST_1", "RCON_PORT_1", "RCON_PASSWORD_1",
  ];
  const optional = [
    "RCON_HOST_2", "RCON_PORT_2", "RCON_PASSWORD_2",
    "MODSAVE_PATH", "MOD_LOG_CHANNEL", "BAN_LOG_CHANNEL", "LEADERBOARD_CHANNEL", "LOG_LEVEL",
    "PLAYTIME_LB_CHANNEL", "PLAYERLIST_CHANNEL", "DONATOR_PATH", "BLACKLIST_IDS", "BUILD_ID",
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

/* ================================================================
   DATA FILES
   ================================================================ */
const FILES = {
  TEMPBAN:        "./tempbans.json",
  ROLES:          "./roles.json",
  WAGES:          "./wages.json",
  PLAYTIME:       "./playtime.json",
  NOTES:          "./hardban_notes.json",
  WARNS:          "./warnings.json",
  MODLOG:         "./modlog.json",
  FACTION_RANKS:  "./faction_ranks.json",
  FACTION_CONFIG: "./faction_config.json",
  FACTION_AUDIT:  "./faction_audit.json",
  MENU_GRANTS:    "./menu_grants.json",
  PLAYER_NOTES:   "./player_notes.json",
  LASTSEEN:       "./lastseen.json",
  KNOWN:          "./known_players.json",
  USER_BLACKLIST: "./user_blacklist.json",
  VERIFY_PANEL:   "./verify_panel.json",
  VERIFY_LINKS:   "./verify_links.json",
};

const DEFAULTS = {
  [FILES.TEMPBAN]:        "[]",
  [FILES.WAGES]:          "[]",
  [FILES.PLAYTIME]:       "{}",
  [FILES.NOTES]:          "{}",
  [FILES.WARNS]:          "{}",
  [FILES.MODLOG]:         "[]",
  [FILES.FACTION_RANKS]:  "{}",
  [FILES.FACTION_CONFIG]: "{}",
  [FILES.FACTION_AUDIT]:  "[]",
  [FILES.MENU_GRANTS]:    "{}",
  [FILES.PLAYER_NOTES]:   "{}",
  [FILES.LASTSEEN]:       "{}",
  [FILES.KNOWN]:          "{}",
  [FILES.USER_BLACKLIST]: "[]",
  [FILES.VERIFY_PANEL]:   "{}",
  [FILES.VERIFY_LINKS]:   "{}",
  [FILES.ROLES]:          JSON.stringify({ modRoleId: "", adminRoleId: "", factionLeaderRoleId: "" }, null, 2),
};

for (const [file, def] of Object.entries(DEFAULTS)) {
  if (!fs.existsSync(file)) {
    fs.writeFileSync(file, def);
    logger.info("Init", `Created ${file}`);
  }
}

/* ================================================================
   ATOMIC FILE I/O  +  IN-MEMORY CACHE  +  MUTATION SERIALIZATION
   ================================================================
   Problem this solves: every command used to do load → modify → save
   directly against disk. Two near-simultaneous operations (two mods, or
   a command racing one of the 60s intervals) could read the same state
   and have the second write silently clobber the first.

   Design:
   • _cache holds the parsed object for each file (read once, reused).
   • safeRead returns a CLONE so callers can't mutate cached state.
   • update(file, mutator) runs the mutator on a clone, writes the result,
     and refreshes the cache — all chained on a per-file promise queue so
     mutations against the same file are strictly serialized.
   ================================================================ */
const _cache  = new Map(); // file -> parsed value
const _queues = new Map(); // file -> Promise (tail of the per-file chain)

function _rawRead(file, fallback) {
  try { return JSON.parse(fs.readFileSync(file, "utf8")); }
  catch (err) {
    logger.warn("IO", `Read failed for ${file}: ${err.message}`);
    return fallback;
  }
}

function _rawWrite(file, data) {
  const tmp = `${file}.${process.pid}.${Date.now()}.tmp`;
  try {
    fs.writeFileSync(tmp, JSON.stringify(data, null, 2), "utf8");
    fs.renameSync(tmp, file);
    return true;
  } catch (err) {
    logger.error("IO", `Write failed for ${file}: ${err.message}`);
    try { fs.unlinkSync(tmp); } catch {}
    return false;
  }
}

const _clone = (v) => (typeof structuredClone === "function"
  ? structuredClone(v)
  : JSON.parse(JSON.stringify(v)));

/** Read through cache. Returns a CLONE so callers can't mutate cached state. */
function safeRead(file, fallback) {
  if (!_cache.has(file)) _cache.set(file, _rawRead(file, fallback));
  const val = _cache.get(file);
  return val === undefined ? fallback : _clone(val);
}

/** Direct write (cache-aware). Prefer `update` for read-modify-write. */
function safeWrite(file, data) {
  const ok = _rawWrite(file, data);
  if (ok) _cache.set(file, _clone(data));
  return ok;
}

/**
 * Serialized read-modify-write. The mutator receives a clone of current
 * state and may mutate it in place and/or return the new state.
 * Returns a Promise resolving to { ok, value }.
 */
function update(file, fallback, mutator) {
  const prev = _queues.get(file) ?? Promise.resolve();
  const next = prev.then(async () => {
    if (!_cache.has(file)) _cache.set(file, _rawRead(file, fallback));
    const working = _clone(_cache.get(file) ?? fallback);
    const result  = mutator(working);
    const toWrite = result === undefined ? working : result;
    const ok      = _rawWrite(file, toWrite);
    if (ok) _cache.set(file, _clone(toWrite));
    return { ok, value: toWrite };
  }).catch(err => {
    logger.error("IO", `update() failed for ${file}: ${err.message}`, { stack: err.stack });
    return { ok: false, value: null };
  });
  _queues.set(file, next.catch(() => {}));
  return next;
}

/* ---- Typed loaders / savers ---- */
const loadBans          = () => safeRead(FILES.TEMPBAN,        []);
/* Serialized temp-ban mutations — go through update() so concurrent commands
   and the 60s expiry sweep can't clobber each other's writes. */
const _sameId = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
function upsertTempBan(entry) {
  return update(FILES.TEMPBAN, [], (bans) => {
    const next = bans.filter(b => !_sameId(b.playerId, entry.playerId));
    next.push(entry);
    return next;
  });
}
/** Remove one or more player IDs from the temp-ban list (case-insensitive). */
function removeBans(...ids) {
  const drop = ids.filter(Boolean).map(s => String(s).toLowerCase());
  return update(FILES.TEMPBAN, [], (bans) => bans.filter(b => !drop.includes(String(b.playerId).toLowerCase())));
}
const loadRoles         = () => safeRead(FILES.ROLES,          { modRoleId: "", adminRoleId: "", factionLeaderRoleId: "" });
const saveRoles         = (d) => safeWrite(FILES.ROLES,         d);
const loadWages         = () => safeRead(FILES.WAGES,          []);
const saveWages         = (d) => safeWrite(FILES.WAGES,         d);
const loadPlaytime      = () => safeRead(FILES.PLAYTIME,       {});
const savePlaytime      = (d) => safeWrite(FILES.PLAYTIME,      d);
const loadNotes         = () => safeRead(FILES.NOTES,          {});
const saveNotes         = (d) => safeWrite(FILES.NOTES,         d);
const loadWarns         = () => safeRead(FILES.WARNS,          {});
/* warnings are mutated via the serialized update() (issueWarn / delwarn / clearwarnings) */
const loadModLog        = () => safeRead(FILES.MODLOG,         []);
const saveModLog        = (d) => safeWrite(FILES.MODLOG,        d);
const loadFactionRanks  = () => safeRead(FILES.FACTION_RANKS,  {});
const saveFactionRanks  = (d) => safeWrite(FILES.FACTION_RANKS, d);
const loadFactionConfig = () => safeRead(FILES.FACTION_CONFIG, {});
const saveFactionConfig = (d) => safeWrite(FILES.FACTION_CONFIG,d);
const loadFactionAudit  = () => safeRead(FILES.FACTION_AUDIT,  []);
// (writes go through the serialized writeFactionAudit() — no direct saver needed)
const loadMenuGrants    = () => safeRead(FILES.MENU_GRANTS,    {});
const saveMenuGrants    = (d) => safeWrite(FILES.MENU_GRANTS,   d);
const loadPlayerNotes   = () => safeRead(FILES.PLAYER_NOTES,   {});
const savePlayerNotes   = (d) => safeWrite(FILES.PLAYER_NOTES,  d);
const loadLastSeen      = () => safeRead(FILES.LASTSEEN,       {});
const saveLastSeen      = (d) => safeWrite(FILES.LASTSEEN,      d);
const loadKnownPlayers  = () => safeRead(FILES.KNOWN,          {});
const saveKnownPlayers  = (d) => safeWrite(FILES.KNOWN,         d);

/* ================================================================
   MOD LOG WRITER  (serialized)
   ================================================================ */
function writeModLog(entry) {
  return update(FILES.MODLOG, [], (log) => {
    log.push({ ...entry, at: Date.now() });
    if (log.length > 10_000) log.splice(0, log.length - 10_000);
    return log;
  });
}

function getPlayerHistory(playerId) {
  const id = playerId.toLowerCase();
  return loadModLog().filter(e => e.playerId?.toLowerCase() === id);
}

/* ================================================================
   PLAYER NOTES  (freeform staff notes on ANY courier — serialized)
   ================================================================ */
function getPlayerNotes(playerId) {
  return loadPlayerNotes()[playerId.toLowerCase()] ?? [];
}
async function addPlayerNote(playerId, text, by) {
  const key = playerId.toLowerCase();
  let count = 0;
  await update(FILES.PLAYER_NOTES, {}, (notes) => {
    if (!notes[key]) notes[key] = [];
    notes[key].push({ text, by, at: Date.now() });
    if (notes[key].length > 100) notes[key].splice(0, notes[key].length - 100);
    count = notes[key].length;
    return notes;
  });
  return count;
}
async function clearPlayerNotes(playerId) {
  const key = playerId.toLowerCase();
  let count = 0;
  await update(FILES.PLAYER_NOTES, {}, (notes) => {
    count = notes[key]?.length ?? 0;
    delete notes[key];
    return notes;
  });
  return count;
}

/* ================================================================
   LAST-SEEN TRACKING  (updated from the player-cache refresh loop)
   ================================================================ */
function recordLastSeen(names, now = Date.now()) {
  if (!names || !names.length) return;
  const seen = loadLastSeen();
  for (const name of names) {
    if (name) seen[String(name).toLowerCase()] = now;
  }
  saveLastSeen(seen);
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
  const known = loadKnownPlayers();
  let added = false;
  for (const name of names) {
    if (!name) continue;
    const key = String(name).toLowerCase();
    if (known[key]) { known[key].lastSeen = now; }
    else { known[key] = { name: String(name), firstSeen: now, lastSeen: now }; added = true; }
  }
  if (added) saveKnownPlayers(known);   // new player discovered → persist
}

/** Autocomplete choices from the known-player registry (substring match,
    most-recently-seen first). Excludes any names already in `exclude`. */
function getKnownPlayerChoices(query, exclude = new Set(), limit = 25) {
  const q = query.toLowerCase();
  return Object.values(loadKnownPlayers())
    .filter(p => !exclude.has(p.name.toLowerCase()) && (!q || p.name.toLowerCase().includes(q)))
    .sort((a, b) => (b.lastSeen ?? 0) - (a.lastSeen ?? 0))
    .slice(0, limit)
    .map(p => ({ name: `${p.name} (offline)`, value: p.name }));
}

/** One-time backfill of the registry from data the bot already has on disk,
    so offline autocomplete works immediately (not just for players seen since
    deployment). Idempotent — recordKnownPlayers only writes new names. */
function seedKnownPlayers() {
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
  recordKnownPlayers([...names]);
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

  if (cmd === "unban")        return loadBans().map(b => b.playerId);                                  // currently temp-banned
  if (cmd === "warnings" || cmd === "clearwarnings" || cmd === "delwarn")
                              return Object.keys(loadWarns()).map(disp);                               // has warnings
  if (cmd === "history")      return [...new Set(loadModLog().map(e => e.playerId).filter(Boolean))];  // has mod history
  if (cmd === "stripmenu")    return Object.keys(loadMenuGrants()).map(disp);                          // holds a menu grant
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

/* ================================================================
   WARNING REMOVAL  (delete a single warning by 1-based index)
   ================================================================ */
async function removeWarningAt(playerId, index1Based) {
  const key = playerId.toLowerCase();
  let removed = null, remaining = 0;
  await update(FILES.WARNS, {}, (warns) => {
    const list = warns[key];
    if (!list || index1Based < 1 || index1Based > list.length) return warns;
    removed = list.splice(index1Based - 1, 1)[0];
    if (!list.length) delete warns[key];
    else remaining = list.length;
    return warns;
  });
  return { removed, remaining };
}

/* ================================================================
   FACTION AUDIT WRITER  (serialized)
   ================================================================ */
function writeFactionAudit(entry) {
  return update(FILES.FACTION_AUDIT, [], (audit) => {
    audit.push({ ...entry, at: Date.now() });
    if (audit.length > 5_000) audit.splice(0, audit.length - 5_000);
    return audit;
  });
}

/* ================================================================
   CONSTANTS
   ================================================================ */
const MENUS = [
  { name: "Staff",      value: "staff",     menuId: "0011110000000000101000000000000 01001100000000" },
  { name: "High Staff", value: "highstaff", menuId: "111110011110001000001101000000000010 1011110000001" },
];

const BAN_REASONS = [
  { name: "RDM In The Strip",          value: "rdm_strip"   },
  { name: "Spawn Killing Faction",     value: "sk_faction"  },
  { name: "Spawn Killing Civ Spawn",   value: "sk_civ"      },
  { name: "Slurs",                     value: "slurs"       },
  { name: "Hard R",                    value: "hard_r"      },
  { name: "Ban Evasion / Alt Account", value: "ban_evasion" },
  { name: "Cheating / Exploiting",     value: "cheating"    },
  { name: "Griefing",                  value: "griefing"    },
  { name: "Harassment",                value: "harassment"  },
  { name: "Other",                     value: "other"       },
];
const BAN_REASON_LABELS = Object.fromEntries(BAN_REASONS.map(r => [r.value, r.name]));

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
const WARN_THRESHOLDS = [
  { count: 3, action: "tempban", duration: "1d",  label: "1-day ban (3 warnings)"    },
  { count: 5, action: "tempban", duration: "1w",  label: "1-week ban (5 warnings)"   },
  { count: 7, action: "permban", duration: null,  label: "Permanent ban (7 warnings)" },
];

const WAGE_TIERS = {
  low_rank:  { label: "Low Rank",  amount: 400,  weekly: true  },
  mid_rank:  { label: "Mid Rank",  amount: 500,  weekly: true  },
  high_rank: { label: "High Rank", amount: 650,  weekly: true  },
  mercenary: { label: "Mercenary", amount: 200,  weekly: false },
};

const WAGE_INTERVAL_MS        = 7 * 24 * 60 * 60 * 1000;
const LEADERBOARD_INTERVAL_MS = 6 * 60 * 60 * 1000;
const LEADERBOARD_TOP_N       = 30;
/* Channel the playtime leaderboard auto-posts to (override with PLAYTIME_LB_CHANNEL). */
const PLAYTIME_LB_CHANNEL     = process.env.PLAYTIME_LB_CHANNEL || "1517198961918611566";
/* Channel the live player list auto-updates in, every 30s (override with PLAYERLIST_CHANNEL). */
const PLAYERLIST_CHANNEL      = process.env.PLAYERLIST_CHANNEL || "1518016127077318897";
const PLAYERLIST_INTERVAL_MS  = 30 * 1000;
const RCON_HEALTH_INTERVAL_MS = 5 * 60 * 1000;
/* Verification: panel channel + the role swapped on success. */
const VERIFY_CHANNEL          = process.env.VERIFY_CHANNEL          || "1518205248907513966";
const VERIFY_UNVERIFIED_ROLE  = process.env.VERIFY_UNVERIFIED_ROLE  || "1518204521916928030";
const VERIFY_VERIFIED_ROLE    = process.env.VERIFY_VERIFIED_ROLE    || "1500607750361583687";

/* ================================================================
   FACTION-SPECIFIC RANK SYSTEM
   ================================================================ */
const FACTION_RANKS = {
  "NCR": {
    order:   ["Private", "Corporal", "Sergeant", "Medic", "Heavy", "MP", "Ranger", "Lieutenant", "Officer"],
    default: "Private",
    badges:  {
      "Private":    "🪖",
      "Corporal":   "⚔️",
      "Sergeant":   "🎖️",
      "Medic":      "💉",
      "Heavy":      "🛡️",
      "MP":         "🔒",
      "Ranger":     "🎯",
      "Lieutenant": "🌟",
      "Officer":    "👑",
    },
    rankFiles: {
      "Private":    "ncrprivate.txt",
      "Corporal":   "ncrcorporal.txt",
      "Sergeant":   "ncrsergeant.txt",
      "Medic":      "ncrmedix.txt",
      "Heavy":      "ncrheavy.txt",
      "MP":         "ncrmp.txt",
      "Ranger":     "ncrranger.txt",
      "Lieutenant": "ncrlieutenant.txt",
      "Officer":    "ncrofficer.txt",
    },
  },
  "Legion": {
    order:   ["Recruit", "Legionnaire", "Prime Legionary", "Veteran Legionnaire", "Vexalarius", "Centurion", "Assassin", "Praetorian", "Legate"],
    default: "Recruit",
    badges:  {
      "Recruit":             "🪖",
      "Legionnaire":         "⚔️",
      "Prime Legionary":     "🗡️",
      "Veteran Legionnaire": "🎖️",
      "Vexalarius":          "🚩",
      "Centurion":           "🏅",
      "Assassin":            "🥷",
      "Praetorian":          "🛡️",
      "Legate":              "👑",
    },
    rankFiles: {
      "Recruit":             "legionrecruit.txt",
      "Legionnaire":         "legionlegionnaire.txt",
      "Prime Legionary":     "legionprimelegionary.txt",
      "Veteran Legionnaire": "legionveteranlegionnaire.txt",
      "Vexalarius":          "legionvexalarius.txt",
      "Centurion":           "legioncenturion.txt",
      "Assassin":            "legionassasin.txt",
      "Praetorian":          "legionpraetorian.txt",
      "Legate":              "legionlegate.txt",
    },
  },
  "Enclave": {
    order:   ["Low Rank", "Mid Rank", "High Rank"],
    default: "Low Rank",
    badges:  {
      "Low Rank":  "🪖",
      "Mid Rank":  "⚔️",
      "High Rank": "👑",
    },
    rankFiles: {
      "Low Rank":  "enclavelowrank.txt",
      "Mid Rank":  "enclavemidrank.txt",
      "High Rank": "enclavehighrank.txt",
    },
  },
  "Khans": {
    order:   ["Low Rank", "Mid Rank", "High Rank"],
    default: "Low Rank",
    badges:  {
      "Low Rank":  "🪖",
      "Mid Rank":  "⚔️",
      "High Rank": "👑",
    },
    rankFiles: {
      "Low Rank":  "khanslowrank.txt",
      "Mid Rank":  "khanmidrank.txt",
      "High Rank": "khansspawn.txt",
    },
  },
  "Brotherhood of Steel": {
    order:   ["Initiate", "Knight", "Paladin", "Elder"],
    default: "Initiate",
    badges:  {
      "Initiate": "🪖",
      "Knight":   "⚔️",
      "Paladin":  "🎖️",
      "Elder":    "👑",
    },
    rankFiles: {
      "Initiate": "bosinitiate.txt",
      "Knight":   "bosknight.txt",
      "Paladin":  "bospaladin.txt",
      "Elder":    "boselder.txt",
    },
  },
};

function getFactionRankConfig(faction) { return FACTION_RANKS[faction] ?? null; }
function getFactionRankOrder(faction)  { return FACTION_RANKS[faction]?.order ?? []; }
function getFactionDefaultRank(faction){ return FACTION_RANKS[faction]?.default ?? "Recruit"; }
function getFactionRankBadge(faction, rank) { return FACTION_RANKS[faction]?.badges[rank] ?? "❓"; }

function rankBadge(faction, rank) {
  const badge = getFactionRankBadge(faction, rank);
  return `${badge}  **${rank ?? getFactionDefaultRank(faction)}**`;
}

function rankWeight(faction, rank) {
  const order = getFactionRankOrder(faction);
  const idx   = order.indexOf(rank);
  return idx === -1 ? -1 : idx;
}

function getFactionRank(faction, playerId) {
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

const FACTION_ROLES_PATH  = "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/FactionRoles";
const FACTION_DEFAULT_CAP = 50;

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
};

const FACTION_SPAWN_MAP = {
  ncrspawn:     "NCR",
  legionspawn:  "Legion",
  enclavespawn: "Enclave",
  khanspawn:    "Khans",
  khansspawn:   "Khans",
  bosspawn:     "Brotherhood of Steel",
};

const ALL_FACTIONS = Object.keys(SPAWN_FILE_MAP);

/* ================================================================
   FALLOUT: NEW VEGAS THEME
   ================================================================ */
const NV = {
  AMBER:       0xFFB000,
  GOLD:        0xD4A017,
  IRRAD_GREEN: 0x39FF14,
  NCR_TAN:     0xC8A96E,
  LEGION_RED:  0x8B0000,
  RUST_RED:    0xC0392B,
  DEAD_GREY:   0x4A4A4A,
  BLUE_VATS:   0x1B4F8A,
  DEEP_BLACK:  0x0D0D0D,
};

/* Ban / IP embed accents — Fallout: New Vegas palette. */
const CLIN = {
  red:   0x8B0000,   // LEGION_RED  — ban / block / active
  green: 0x39FF14,   // IRRAD_GREEN — cleared / lifted / no bans
  grey:  0xFFB000,   // AMBER       — neutral info (lists, checks, connection log)
};

const QUOTES = {
  ban:     [
    '"You\'re banned from the Lucky 38. Mr. House\'s orders."',
    '"You\'ve made an enemy of the Mojave. Enjoy the wasteland."',
    '"Even in the wasteland, there are rules. You broke them."',
    '"The Securitrons don\'t forgive. Neither do we."',
    '"The game was rigged from the start — and you just lost."',
    '"Should\'ve learned to use your head instead of swinging it. Now you\'re exiled."',
    '"Out into the Divide with you. Don\'t look back."',
    '"The Courier always rings twice. You won\'t ring again."',
    '"Patrolling the Mojave almost makes you wish for a ban this clean."',
    '"The King is dead, and so is your access. Thank you. Thank you very much."',
  ],
  unban:   [
    '"Every soul deserves a second chance in the Mojave. Don\'t waste yours."',
    '"The gates of the Strip open once more. Don\'t make us regret it."',
    '"Exile lifted. Welcome back to New Vegas — try not to shoot anyone."',
    '"Begin again. The Mojave forgives, this once."',
    '"Your slate\'s wiped cleaner than a Vault-Tec ad. Walk the line."',
    '"Mr. House has reconsidered. Don\'t squander his mercy."',
  ],
  warn:    [
    '"Consider this a warning, friend. We\'re watching."',
    '"The Strip has eyes everywhere. Don\'t test us again."',
    '"One more strike and the Securitrons handle it personally."',
    '"Toe the line, courier — the NCR keeps ledgers, and so do we."',
    '"That\'s one mark on your Pip-Boy. Collect enough and you\'re Legion bait."',
    '"We\'ve got your number, and it\'s climbing. Slow down."',
  ],
  caps:    [
    '"War never changes. But caps? Caps are forever."',
    '"The House always collects. Today, it pays."',
    '"A courier without caps is just a wanderer."',
    '"In the Mojave, caps are the only truth that matters."',
    '"Bottle caps: the only currency the Brotherhood can\'t confiscate."',
  ],
  system:  [
    '"All systems nominal. Securitron network active."',
    '"Maintenance cycle complete. The Strip never sleeps."',
    '"Mr. House is watching. Always watching."',
    '"RobCo terminals online. Vault door sealed."',
    '"Reticulating splines across the Mojave wasteland..."',
  ],
  wages:   [
    '"The House always pays its debts — eventually."',
    '"Caps distributed. The economy of the Mojave endures."',
    '"A fair day\'s work for a fair day\'s pay. Even in the apocalypse."',
    '"Payday on the Strip. Don\'t spend it all at the Atomic Wrangler."',
  ],
  announce: [
    '"Attention all couriers on the Strip..."',
    '"Message from the Mojave Authority..."',
    '"Broadcast from the Lucky 38..."',
    '"This is Mr. New Vegas, and boy, do I have news for you..."',
    '"Radio New Vegas, cutting through the static..."',
  ],
  faction: [
    '"Allegiances in the Mojave are written in blood and caps."',
    '"Every faction needs soldiers. Every soldier needs orders."',
    '"The wasteland belongs to those who organise."',
    '"Rank is earned. Loyalty is proven."',
    '"NCR, Legion, or House — pick your banner and bleed for it."',
  ],
  kick:    [
    '"Get out. Don\'t make us ask twice."',
    '"Shown the door, courier. Mind the radroaches on your way out."',
    '"You\'re not welcome at the Tops tonight. Beat it."',
    '"Ejected. Take a walk down the Long 15 and cool off."',
  ],
  verify:  [
    '"Papers, please. The Strip opens to those it knows."',
    '"State your name, courier. The Securitrons are checking the registry."',
    '"Mr. House keeps a list. Let\'s get you on the right one."',
    '"No name, no entry. Those are the rules of New Vegas."',
  ],
  connect: [
    '"A courier strides into the Mojave."',
    '"Boots on the Strip. The Securitrons log every arrival."',
    '"Another wanderer steps off the Long 15."',
    '"Vault door opens. Someone\'s come to play."',
  ],
  autoban: [
    '"A barred courier tried to slip back into the Mojave. Denied."',
    '"The Securitrons remember every face. Yours wasn\'t welcome."',
    '"Ban evasion detected. The House does not tolerate cheats."',
    '"Nice try. The Mojave has a long memory and a longer reach."',
  ],
};
const randomQuote = (cat) => {
  const pool = QUOTES[cat] ?? QUOTES.system;
  return pool[Math.floor(Math.random() * pool.length)];
};

/* ================================================================
   RATE LIMITER
   ================================================================ */
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

/* ================================================================
   INPUT SANITIZATION
   ================================================================ */
function sanitizeId(raw) {
  return String(raw ?? "")
    .replace(/\s*\((?:manual entry|offline)\)/gi, "")   // strip autocomplete display labels
    .replace(/\s*\[(?:s1|s2|s1\+s2)\]/gi, "")
    .trim()
    .replace(/[^a-zA-Z0-9_\-.]/g, "")
    .slice(0, 64);
}
function sanitizeMessage(raw) {
  return String(raw ?? "").replace(/[\r\n\t]/g, " ").replace(/[^\x20-\x7E]/g, "").slice(0, 200);
}

/* ================================================================
   ROLE CHECKS
   ================================================================ */
function hasAdminRole(member) {
  if (isOwner(member?.id)) return true;
  const { adminRoleId } = loadRoles();
  return !adminRoleId || member.roles.cache.has(adminRoleId);
}
function hasModRole(member) {
  if (isOwner(member?.id)) return true;
  const { modRoleId } = loadRoles();
  return !modRoleId || member.roles.cache.has(modRoleId);
}
function hasFactionLeaderRole(member) {
  if (isOwner(member?.id)) return true;
  const { factionLeaderRoleId } = loadRoles();
  return factionLeaderRoleId && member.roles.cache.has(factionLeaderRoleId);
}

/* ================================================================
   COMMAND BLACKLIST  (Discord users barred from ALL bot commands)
   ================================================================
   Seeded from the BLACKLIST_IDS env var (comma / space / newline separated
   Discord user IDs) AND a runtime store managed from /configure. Owners
   (OWNER_IDS) are always exempt.
   ================================================================ */
const BLACKLIST_IDS = new Set([
  ...String(process.env.BLACKLIST_IDS ?? "").split(/[\s,]+/).map(s => s.trim()).filter(Boolean),
  ...(safeRead(FILES.USER_BLACKLIST, []) || []).map(String),
]);
function isBlacklisted(userId)  { return BLACKLIST_IDS.has(String(userId)); }
function saveUserBlacklist()    { safeWrite(FILES.USER_BLACKLIST, [...BLACKLIST_IDS]); }
function addUserBlacklist(id)   { id = String(id).trim(); const added = !!id && !BLACKLIST_IDS.has(id); if (added) { BLACKLIST_IDS.add(id); saveUserBlacklist(); } return added; }
function removeUserBlacklist(id) { id = String(id).trim(); const removed = BLACKLIST_IDS.delete(id); if (removed) saveUserBlacklist(); return removed; }

/* ================================================================
   UTILITY HELPERS
   ================================================================ */
function md5(text) { return crypto.createHash("md5").update(text).digest("hex"); }

function formatTimeLeft(expiresMs) {
  const diff = expiresMs - Date.now();
  if (diff <= 0) return "expired";
  const s = Math.floor(diff / 1000);
  const m = Math.floor(s / 60) % 60;
  const h = Math.floor(s / 3600) % 24;
  const d = Math.floor(s / 86400);
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
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
  return server === "server2" ? "Server 2" : server === "both" ? "Both Servers" : "Server 1";
}
function serverEmoji(server) {
  return server === "both" ? "🌐" : server === "server2" ? "2️⃣" : "1️⃣";
}

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

/* ================================================================
   INTERACTIVE PAGINATOR
   ================================================================
   Renders a list across pages with ◀ ▶ buttons. `buildEmbed(pageLines,
   pageIndex, totalPages)` returns the EmbedBuilder for a page. Uses the
   same awaitMessageComponent flow as the confirm dialogs elsewhere.
   ================================================================ */
async function paginate(interaction, lines, buildEmbed, { perPage = 12, ephemeral = false, idleMs = 120_000 } = {}) {
  const pages = splitPages(lines, perPage);
  const total = pages.length;
  let page = 0;
  const row = (p) => new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("pg_prev").setEmoji("◀️").setStyle(ButtonStyle.Secondary).setDisabled(p === 0),
    new ButtonBuilder().setCustomId("pg_ind").setLabel(`Page ${p + 1} / ${total}`).setStyle(ButtonStyle.Secondary).setDisabled(true),
    new ButtonBuilder().setCustomId("pg_next").setEmoji("▶️").setStyle(ButtonStyle.Secondary).setDisabled(p >= total - 1),
  );
  const render = (p) => ({ embeds: [brand(buildEmbed(pages[p], p, total))], components: total > 1 ? [row(p)] : [] });

  if (interaction.deferred || interaction.replied) await interaction.editReply(render(page));
  else await interaction.reply({ ...render(page), ephemeral });
  if (total <= 1) return;

  const msg = await interaction.fetchReply();
  for (;;) {
    let btn;
    try {
      btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: idleMs, filter: i => i.user.id === interaction.user.id });
    } catch {
      try { await interaction.editReply({ components: [] }); } catch {}
      return;
    }
    page = btn.customId === "pg_prev" ? Math.max(0, page - 1) : Math.min(total - 1, page + 1);
    await btn.update(render(page));
  }
}

/* ================================================================
   EMBED BUILDERS
   ================================================================ */
const DIVIDER = "▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬";
const RULE    = "─────────────────────────────";
const BRAND_NAME = "MOJAVE AUTHORITY";

/* ================================================================
   VISUAL SYSTEM  (consistent branding across every embed)
   ================================================================ */
function brandIcon() { try { return client.user?.displayAvatarURL?.({ size: 128 }) ?? null; } catch { return null; } }

/** Stamp an embed with the bot's identity: author header (+ avatar),
    timestamp, and optional thumbnail / footer. One look, everywhere. */
function brand(embed, { thumb = false, footer } = {}) {
  const icon = brandIcon();
  embed.setAuthor(icon ? { name: BRAND_NAME, iconURL: icon } : { name: BRAND_NAME });
  if (thumb && icon) embed.setThumbnail(icon);
  if (footer) embed.setFooter(typeof footer === "string" ? { text: footer } : footer);
  embed.setTimestamp();
  return embed;
}

/** Unicode progress/meter bar, e.g. ███████░░░░░ */
function bar(value, max, width = 12) {
  const ratio = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
  const filled = Math.round(ratio * width);
  return "█".repeat(filled) + "░".repeat(Math.max(0, width - filled));
}
const pip = (ok) => (ok ? "🟢" : "🔴");

/* A blockquote-styled hero line used at the top of feature embeds. */
function hero(quoteText) { return `> *${quoteText}*\n${RULE}`; }

/* Ban / IP embeds: stamp them with the full Mojave Authority branding (author
   header, avatar, timestamp) + an optional footer — same look as everything else. */
function clinical(embed, footer) {
  return brand(embed, footer ? { footer } : {});
}

/* ================================================================
   EMBED BUILDERS
   ================================================================ */
function successEmbed(title, description, quoteCategory = "system") {
  return brand(new EmbedBuilder().setColor(NV.AMBER)
    .setTitle(`✅  ${title}`)
    .setDescription(`${description}`),
    { footer: { text: randomQuote(quoteCategory) } });
}
function errorEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.RUST_RED)
    .setTitle(`☢️  ${title}`)
    .setDescription(`${description}`),
    { footer: { text: "Securitron network active · incident logged" } });
}
function warningEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle(`⚠️  ${title}`)
    .setDescription(`${description}`));
}
function adminOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEEP_BLACK)
    .setTitle("🎰  Access Denied — Mr. House's Domain")
    .setDescription('> *"I didn\'t survive two centuries to be overruled by the uninvited."*\n\nThis command is restricted to **Administrators** only.'),
    { footer: { text: "Unauthorized access attempt logged" } });
}
function ownerOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEEP_BLACK)
    .setTitle("👁️  Owner Eyes Only")
    .setDescription('> *"Some files even Mr. House keeps to himself."*\n\nThis command is restricted to the **bot owner**.'),
    { footer: { text: "Unauthorized access attempt logged" } });
}
function modOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
    .setTitle("🛡️  Clearance Required")
    .setDescription('> *"You don\'t have the credentials for this, friend."*\n\nThis command requires the **Moderator** role.'),
    { footer: { text: "Access restricted · civilian status confirmed" } });
}
function blacklistedEmbed(entry) {
  const reason = entry?.reason ? `\n\n**Reason:** ${entry.reason}` : "";
  return brand(new EmbedBuilder().setColor(NV.LEGION_RED)
    .setTitle("⛔  Blacklisted — Access Revoked")
    .setDescription(`> *"You're persona non grata around here. The Securitrons won't lift a finger for you."*\n\nYou have been **blacklisted** from using this bot. All commands are unavailable to you.${reason}`),
    { footer: { text: "Contact an administrator if you believe this is a mistake" } });
}
function factionLeaderOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("⚔️  Faction Authority Required")
    .setDescription('> *"Only faction leaders pull strings around here, stranger."*\n\nRequires the **Faction Leader** role (or Moderator).'),
    { footer: { text: "Faction access not verified" } });
}
function factionLeaderStrictEmbed() {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("⚔️  Faction Leader Authority Required")
    .setDescription('> *"Rank assignments are the sole domain of faction leadership."*\n\nThis action requires the **Faction Leader** role specifically.'),
    { footer: { text: "Rank authority not verified" } });
}
function emptyIdEmbed() {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("📭  No Courier ID Provided")
    .setDescription("A valid **Courier ID** or username is required.\n\n💡 *Start typing in the player field — autocomplete surfaces anyone currently online.*"),
    { footer: { text: "Tip: manual IDs are accepted if the player is offline" } });
}
function rateLimitEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
    .setTitle("⏱️  Slow Down, Courier")
    .setDescription("You're issuing commands too quickly. Wait a moment and try again."),
    { footer: { text: "Rate limit active" } });
}

/* ================================================================
   RCON
   ================================================================ */
function getServerConfig(server) {
  if (server === "server2") return {
    host: process.env.RCON_HOST_2, port: Number(process.env.RCON_PORT_2), password: process.env.RCON_PASSWORD_2,
  };
  return {
    host: process.env.RCON_HOST_1, port: Number(process.env.RCON_PORT_1), password: process.env.RCON_PASSWORD_1,
  };
}

function sendRconRaw(command, server = "server1", timeoutMs = 3000) {
  const { host, port, password } = getServerConfig(server);
  return new Promise((resolve, reject) => {
    const socket = new net.Socket();
    let response = "", authenticated = false, settled = false;
    // Guard so the promise settles exactly once and the fallback timer is
    // always cleared — otherwise every call leaked a live timer for `timeoutMs`.
    let fallbackTimer = null;
    const cleanup = () => { try { socket.destroy(); } catch {} };
    const finish = (fn, value) => {
      if (settled) return;
      settled = true;
      if (fallbackTimer) clearTimeout(fallbackTimer);
      cleanup();
      fn(value);
    };

    socket.setTimeout(timeoutMs);
    socket.connect(port, host);

    socket.on("data", (data) => {
      const text = data.toString();
      if (text.includes("Password:")) { socket.write(md5(password)); return; }
      if (text.includes("Authenticated=1") && !authenticated) {
        authenticated = true;
        socket.write(command + "\n");
        return;
      }
      response += text;
    });
    socket.on("timeout", () => finish(resolve, response || ""));
    socket.on("error",   (err) => finish(reject, err));
    socket.on("close",   () => finish(resolve, response));
    fallbackTimer = setTimeout(() => finish(resolve, response), timeoutMs);
  });
}

async function sendRcon(command, server = "server1", timeoutMs = 3000, retries = 2) {
  let lastErr;
  for (let attempt = 0; attempt <= retries; attempt++) {
    try {
      return await sendRconRaw(command, server, timeoutMs);
    } catch (err) {
      lastErr = err;
      if (attempt < retries) {
        const wait = 500 * Math.pow(2, attempt);
        logger.warn("RCON", `Attempt ${attempt + 1} failed for [${server}] "${command}", retrying in ${wait}ms: ${err.message}`);
        await new Promise(r => setTimeout(r, wait));
      }
    }
  }
  logger.error("RCON", `All ${retries + 1} attempts failed for [${server}] "${command}": ${lastErr.message}`);
  throw lastErr;
}

async function sendRconBoth(command, server) {
  if (server === "both") {
    const [s1, s2] = await Promise.all([sendRcon(command, "server1"), sendRcon(command, "server2")]);
    return { s1, s2 };
  }
  return { s1: await sendRcon(command, server), s2: null };
}

/* RCON-ban an id on the chosen server(s) AND flag every IP we've ever seen
   that id connect from. Any account that later connects from a flagged IP is
   auto-banned by the live log watcher. Never throws — failures are logged.
   Returns ipBans' enforcement summary ({ ids, ips, alts, field }). */
async function banWithIp(playerId, server = "both") {
  try { await sendRconBoth(`Ban ${sanitizeId(playerId)}`, server); }
  catch (err) { logger.warn("Bans", `RCON ban failed for ${playerId}: ${err.message}`); }
  try { return ipBans.blacklistPlayer(playerId); }
  catch (err) { logger.warn("IPBan", `IP enforcement failed for ${playerId}: ${err.message}`); return { ids: [], ips: [], alts: [], field: null }; }
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

/* ================================================================
   DISCORD CLIENT & LOG CHANNEL
   ================================================================ */
const client = new Client({ intents: [GatewayIntentBits.Guilds] });

async function logAction(embed) {
  if (!process.env.MOD_LOG_CHANNEL) return;
  try {
    const ch = await client.channels.fetch(process.env.MOD_LOG_CHANNEL);
    if (ch?.isTextBased()) await ch.send({ embeds: [embed] });
  } catch (err) {
    logger.warn("Log", `Failed to post mod log: ${err.message}`);
  }
}
// Ban actions go to a dedicated ban-log channel (BAN_LOG_CHANNEL). If that isn't
// set, they fall back to the regular mod-log channel.
async function logBan(embed) {
  const channelId = process.env.BAN_LOG_CHANNEL;
  if (!channelId) return logAction(embed);
  try {
    const ch = await client.channels.fetch(channelId);
    if (ch?.isTextBased()) await ch.send({ embeds: [embed] });
  } catch (err) {
    logger.warn("Log", `Failed to post ban log: ${err.message}`);
  }
}

/* ================================================================
   PUNISHMENT DM NOTICE
   ================================================================
   When a moderator links a Discord account to a punishment, DM that user a
   branded breakdown of what happened. Returns true (sent), false (DM
   failed — DMs closed/blocked), or null (no account was linked).
   ================================================================ */
async function dmPunishmentNotice(discordUser, { action, color, playerId, reason, fields = [] }) {
  if (!discordUser) return null;
  const embed = brand(new EmbedBuilder().setColor(color)
    .setTitle(`📨  Moderation Notice — ${action}`)
    .setDescription(hero("A moderation action has been taken on your account."))
    .addFields(
      { name: "🎯  Courier",  value: `\`${playerId}\``, inline: true },
      { name: "⚖️  Action",   value: `**${action}**`,   inline: true },
      ...(reason ? [{ name: "📋  Reason", value: reason, inline: false }] : []),
      ...fields,
    ),
    { thumb: true, footer: { text: "You received this because a moderator linked your Discord account to this action." } });
  try {
    await discordUser.send({ embeds: [embed] });
    return true;
  } catch (err) {
    logger.warn("DM", `Could not DM ${discordUser.id}: ${err.message}`);
    return false;
  }
}

/** Builds the "📨 Player Notified" status field for the moderator's reply. */
function dmStatusField(sent, discordUser) {
  if (sent === null) return null;
  return {
    name: "📨  Player Notified",
    value: sent
      ? `✅  DM delivered to <@${discordUser.id}>`
      : `⚠️  Couldn't DM <@${discordUser.id}> — their DMs are closed or the bot is blocked.`,
    inline: false,
  };
}

/** Send a single branded embed as a DM. Returns true (sent) or false (failed). */
async function dmEmbed(discordUser, embed) {
  try { await discordUser.send({ embeds: [brand(embed)] }); return true; }
  catch (err) { logger.warn("DM", `Could not DM ${discordUser.id}: ${err.message}`); return false; }
}

/* ================================================================
   PLAYER CACHE
   ================================================================ */
const playerCache = {
  server1: [], server2: [],
  lastUpdated: { server1: 0, server2: 0 },
};
const CACHE_TTL_MS = 90_000;

/** Pull trimmed, non-empty player names out of a parsed RefreshList payload. */
function extractPlayerNames(data) {
  return (data?.PlayerList ?? [])
    .map(p => String(p.Username ?? p.username ?? p.PlayerName ?? p.name ?? p.Name ?? "").trim())
    .filter(Boolean);
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
  }
}

function getPlayerChoices(server, focused = "") {
  const now     = Date.now();
  const servers = (!server || server === "both") ? ["server1", "server2"] : [server];
  const seen    = new Set();
  const choices = [];
  // 1) currently-online players first
  for (const srv of servers) {
    if (now - playerCache.lastUpdated[srv] > CACHE_TTL_MS) refreshPlayerCache(srv);
    const label = srv === "server1" ? "[S1]" : "[S2]";
    for (const name of playerCache[srv]) {
      const key = name.toLowerCase();
      if (seen.has(key) || (focused && !key.includes(focused.toLowerCase()))) continue;
      seen.add(key);
      const onBoth = playerCache.server1.some(n => n.toLowerCase() === key)
                  && playerCache.server2.some(n => n.toLowerCase() === key);
      choices.push({ name: onBoth ? `${name} [S1+S2]` : `${name} ${label}`, value: name });
    }
  }
  // 2) fall back to previously-seen (offline) players so anyone who's ever
  //    joined can still be picked — e.g. typing "ncr_" surfaces "ncr_private"
  if (choices.length < 25) {
    for (const c of getKnownPlayerChoices(focused, seen, 25 - choices.length)) {
      seen.add(c.value.toLowerCase());
      choices.push(c);
    }
  }
  return choices.slice(0, 25);
}

/* ================================================================
   PLAYTIME TRACKING
   ================================================================ */
function tickPlaytime() {
  const online = new Set([...playerCache.server1, ...playerCache.server2]);
  if (!online.size) return;
  const pt = loadPlaytime();
  for (const id of online) if (id) pt[id] = (pt[id] ?? 0) + 1;
  savePlaytime(pt);
  recordLastSeen([...online]);
}

/* ================================================================
   FACTION FILE HELPERS
   ================================================================ */
function readFactionFile(spawnFile) {
  const fp = path.join(FACTION_ROLES_PATH, spawnFile);
  try {
    return fs.readFileSync(fp, "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  } catch { return null; }
}

function writeFactionFile(spawnFile, lines) {
  const fp = path.join(FACTION_ROLES_PATH, spawnFile);
  try {
    fs.writeFileSync(fp, lines.join("\n") + "\n", "utf8");
    return true;
  } catch (err) {
    logger.error("Faction", `Write failed for ${spawnFile}: ${err.message}`);
    return false;
  }
}

/* ================================================================
   DONATOR WHITELIST FILE  (one player ID per line in DONATOR_FILE)
   ================================================================ */
function readDonatorFile() {
  try {
    return fs.readFileSync(DONATOR_FILE, "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  } catch (err) {
    if (err.code === "ENOENT") return [];          // not created yet — treat as empty
    logger.error("Donator", `Read failed: ${err.message}`);
    return null;                                    // real I/O error
  }
}

function writeDonatorFile(lines) {
  try {
    fs.mkdirSync(path.dirname(DONATOR_FILE), { recursive: true });
    fs.writeFileSync(DONATOR_FILE, lines.join("\n") + "\n", "utf8");
    logger.info("Donator", `Wrote ${lines.length} entr${lines.length === 1 ? "y" : "ies"} to ${DONATOR_FILE}`);
    return true;
  } catch (err) {
    logger.error("Donator", `Write failed: ${err.message}`);
    return false;
  }
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

function addPlayerToRankFile(faction, playerId, rank) {
  const cfg = getFactionRankConfig(faction);
  if (!cfg) return true;
  const rankFile = cfg.rankFiles[rank];
  if (!rankFile) {
    logger.warn("Faction", `No rank file mapped for ${faction}/${rank}`);
    return true;
  }
  const lines = readFactionFile(rankFile) ?? [];
  if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) return true;
  lines.push(playerId);
  return writeFactionFile(rankFile, lines);
}

function removePlayerFromRankFile(faction, playerId, rank) {
  const cfg = getFactionRankConfig(faction);
  if (!cfg) return true;
  const rankFile = cfg.rankFiles[rank];
  if (!rankFile) return true;
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
  const factionRanks = loadFactionRanks()[faction] ?? {};
  const defaultRank  = getFactionDefaultRank(faction);
  return lines
    .map(id => {
      const rank = factionRanks[id.toLowerCase()] ?? defaultRank;
      return { playerId: id, rank, weight: rankWeight(faction, rank) };
    })
    .sort((a, b) => b.weight - a.weight || a.playerId.localeCompare(b.playerId));
}

/* ================================================================
   MODSAVE / BALANCE HELPERS
   ================================================================ */
function getModsavePath()            { return process.env.MODSAVE_PATH || null; }
function getPlayerFilePath(playerId) {
  const base = getModsavePath();
  return base ? path.join(base, `${sanitizeId(playerId)}.txt`) : null;
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
  try { fs.writeFileSync(fp, String(Math.max(0, Math.floor(amount))), "utf8"); return true; }
  catch (err) { logger.error("Balance", `Write failed for ${playerId}: ${err.message}`); return false; }
}

/* ================================================================
   WARN SYSTEM
   ================================================================ */
async function issueWarn(playerId, reason, moderator, server, interaction) {
  const key = playerId.toLowerCase();
  let count = 0;
  await update(FILES.WARNS, {}, (warns) => {
    if (!warns[key]) warns[key] = [];
    warns[key].push({ reason, by: moderator, at: Date.now() });
    count = warns[key].length;
    return warns;
  });

  writeModLog({ action: "warn", playerId, reason, by: moderator, count });

  const escalation = [...WARN_THRESHOLDS].reverse().find(t => count >= t.count);
  let escalated = null;

  if (escalation && escalation.action === "tempban") {
    const { ms, label } = BAN_DURATIONS[escalation.duration];
    const expires        = Date.now() + ms;
    await banWithIp(playerId, server);
    await upsertTempBan({ playerId, reason: `Auto-ban: ${escalation.label}`, expires, durationLabel: label, moderator: "Auto-Escalation", server });
    escalated = { type: "tempban", label };
    writeModLog({ action: "auto-tempban", playerId, reason: escalation.label, duration: label });
  } else if (escalation && escalation.action === "permban") {
    await banWithIp(playerId, server);
    escalated = { type: "permban" };
    writeModLog({ action: "auto-permban", playerId, reason: escalation.label });
  }

  return { count, escalated };
}

/* ================================================================
   TEMP BAN EXPIRY
   ================================================================ */
async function processExpiredBans() {
  const now = Date.now();
  const lifted = [];
  for (const ban of loadBans()) {
    if (ban.expires > now) continue;
    try {
      await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server1");
      await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server2");
      try { ipBans.unblacklistPlayer(ban.playerId); } catch {}   // clear that player's flagged IPs
      lifted.push(ban.playerId);
      logger.info("Bans", `Expired ban lifted: ${ban.playerId}`);
      writeModLog({ action: "auto-unban", playerId: ban.playerId, reason: "Sentence served" });
      await logBan(
        clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("⏰  Sentence Served — Courier Released")
          .setDescription('> *"Every soul deserves a second chance in the Mojave."*')
          .addFields(
            { name: "🎯  Courier",          value: `\`${ban.playerId}\``,          inline: true },
            { name: "⚖️  Original Offense", value: ban.reason,                     inline: true },
            { name: "⏱️  Duration Served",  value: ban.durationLabel ?? "Unknown", inline: true },
            { name: "🛡️  Originally Banned",value: `by ${ban.moderator}`,          inline: false },
          ), "Exile expired — access restored automatically")
      );
    } catch (err) {
      logger.error("Bans", `Unban failed for ${ban.playerId}: ${err.message}`);
      // leave it on the list; retried next sweep
    }
  }
  if (lifted.length) await removeBans(...lifted);   // serialized — preserves concurrent additions
}

/* ================================================================
   WAGE PAYOUT
   ================================================================ */
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
    ...results.paid.map(r   => `✅  \`${r.playerId}\`  ·  **${r.tier}**  →  **${r.newBal.toLocaleString()} caps** *(+${r.amount})*`),
    ...results.failed.map(r => `☢️  \`${r.playerId}\`  ·  **${r.tier}**  —  *ledger write failed*`),
  ];
  const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💰  Weekly Wages Disbursed")
    .setDescription(`> *${randomQuote("wages")}*\n\n${DIVIDER}\n**${results.paid.length}** paid  ·  **${results.skipped.length}** skipped  ·  **${results.failed.length}** failed`)
    .setFooter({ text: "The House always pays its debts." }).setTimestamp();
  for (const f of chunkFields(lines, "📋  Payout Ledger")) embed.addFields(f);
  brand(embed); await logAction(embed);
}

/* ================================================================
   LEADERBOARD
   ================================================================ */
function buildLeaderboardData() {
  const base = getModsavePath();
  if (!base) return null;
  const entries = [];
  try {
    for (const file of fs.readdirSync(base).filter(f => f.endsWith(".txt"))) {
      const id  = path.basename(file, ".txt");
      try {
        const bal = parseInt(fs.readFileSync(path.join(base, file), "utf8").trim(), 10);
        if (!isNaN(bal)) entries.push({ playerId: id, balance: bal });
      } catch {}
    }
  } catch (err) { logger.error("Leaderboard", err.message); return null; }
  return entries.sort((a, b) => b.balance - a.balance).slice(0, LEADERBOARD_TOP_N);
}

function rankLabel(i) {
  return i === 0 ? "🥇" : i === 1 ? "🥈" : i === 2 ? "🥉" : `\`#${String(i + 1).padStart(2)}\``;
}

function buildLeaderboardEmbed() {
  const entries = buildLeaderboardData();
  const embed = new EmbedBuilder().setColor(NV.GOLD)
    .setTitle(`💰  New Vegas Caps — Top ${LEADERBOARD_TOP_N}`);
  if (!entries) return brand(embed.setColor(NV.RUST_RED)
    .setDescription(`${hero("Vault records inaccessible.")}\n\`MODSAVE_PATH\` not configured or unreadable — check your \`.env\`.`),
    { footer: { text: `Updated every 6h · ${BUILD_ID}` } });
  if (!entries.length) return brand(embed.setColor(NV.IRRAD_GREEN)
    .setDescription(`${hero("No ledgers found.")}\nNo cap records on file yet.`),
    { footer: { text: `Updated every 6h · ${BUILD_ID}` } });
  const top = entries[0]?.balance || 1;
  const body = entries.map((e, i) => {
    const meter = i < 5 ? `  \`${bar(e.balance, top, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  ·  ${e.balance.toLocaleString()} caps${meter}`;
  }).join("\n");
  return brand(embed.setDescription(`${hero("War never changes. But caps? Caps fluctuate.")}\n${body}`),
    { thumb: true, footer: { text: `Updated every 6h · ${BUILD_ID}` } });
}

let lastLeaderboardMsgId = null;
async function postLeaderboard() {
  const channelId = process.env.LEADERBOARD_CHANNEL;
  if (!channelId) return;
  let channel;
  try { channel = await client.channels.fetch(channelId); } catch { return; }
  const embed = buildLeaderboardEmbed();
  if (lastLeaderboardMsgId) {
    try { const m = await channel.messages.fetch(lastLeaderboardMsgId); await m.edit({ embeds: [embed] }); return; }
    catch { lastLeaderboardMsgId = null; }
  }
  try { const m = await channel.send({ embeds: [embed] }); lastLeaderboardMsgId = m.id; } catch {}
}

/* ================================================================
   PLAYTIME LEADERBOARD
   ================================================================ */
function buildPlaytimeLeaderboardData() {
  return Object.entries(loadPlaytime())
    .map(([playerId, minutes]) => ({ playerId, minutes: Number(minutes) || 0 }))
    .filter(e => e.minutes > 0)
    .sort((a, b) => b.minutes - a.minutes)
    .slice(0, LEADERBOARD_TOP_N);
}

function buildPlaytimeLeaderboardEmbed() {
  const entries = buildPlaytimeLeaderboardData();
  const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN)
    .setTitle(`⏱️  Most Active Couriers — Top ${LEADERBOARD_TOP_N}`);
  if (!entries.length) return brand(embed
    .setDescription(`${hero("No playtime tracked yet.")}\nPlaytime accrues while couriers are online (sampled every 60s).`),
    { footer: { text: `Updated every 6h · ${BUILD_ID}` } });
  const top = entries[0]?.minutes || 1;
  const body = entries.map((e, i) => {
    const meter = i < 5 ? `  \`${bar(e.minutes, top, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  ·  ${formatPlaytime(e.minutes)}${meter}`;
  }).join("\n");
  return brand(embed.setDescription(`${hero("Time served in the Mojave.")}\n${body}`),
    { thumb: true, footer: { text: `Updated every 6h · ${BUILD_ID}` } });
}

let lastPlaytimeLbMsgId = null;
async function postPlaytimeLeaderboard() {
  if (!PLAYTIME_LB_CHANNEL) return;
  let channel;
  try { channel = await client.channels.fetch(PLAYTIME_LB_CHANNEL); } catch { return; }
  const embed = buildPlaytimeLeaderboardEmbed();
  if (lastPlaytimeLbMsgId) {
    try { const m = await channel.messages.fetch(lastPlaytimeLbMsgId); await m.edit({ embeds: [embed] }); return; }
    catch { lastPlaytimeLbMsgId = null; }
  }
  try { const m = await channel.send({ embeds: [embed] }); lastPlaytimeLbMsgId = m.id; } catch {}
}

/* Live player list — edits its own message in a channel every 30s. */
const hasServer2 = !!process.env.RCON_HOST_2;
function buildPlayerListEmbed() {
  const fmt = (arr) => {
    if (!arr.length) return "*Empty*";
    let out = arr.map(n => `• ${n}`).join("\n");
    if (out.length > 1024) out = out.slice(0, 1000).replace(/\n[^\n]*$/, "") + "\n…";
    return out;
  };
  const s1 = [...playerCache.server1].sort((a, b) => a.localeCompare(b));
  const s2 = [...playerCache.server2].sort((a, b) => a.localeCompare(b));
  const total = new Set([...s1, ...s2].map(n => n.toLowerCase())).size;
  const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("🧭  Live Player List")
    .setDescription(hero(`**${total}** courier${total !== 1 ? "s" : ""} roaming the Mojave right now.`))
    .addFields({ name: `🖥️  Server 1 (${s1.length})`, value: fmt(s1), inline: true });
  if (hasServer2) embed.addFields({ name: `🖥️  Server 2 (${s2.length})`, value: fmt(s2), inline: true });
  return brand(embed.setFooter({ text: "Updates every 30s" }).setTimestamp());
}
let lastPlayerListMsgId = null;
async function postPlayerList() {
  if (!PLAYERLIST_CHANNEL) return;
  let channel;
  try { channel = await client.channels.fetch(PLAYERLIST_CHANNEL); } catch { return; }
  try { await refreshPlayerCache("server1"); if (hasServer2) await refreshPlayerCache("server2"); } catch {}
  const embed = buildPlayerListEmbed();
  if (lastPlayerListMsgId) {
    try { const m = await channel.messages.fetch(lastPlayerListMsgId); await m.edit({ embeds: [embed] }); return; }
    catch { lastPlayerListMsgId = null; }
  }
  try { const m = await channel.send({ embeds: [embed] }); lastPlayerListMsgId = m.id; } catch {}
}

/* ================================================================
   VERIFICATION  (link a Discord user to their Pavlov username)
   ================================================================ */
const loadVerifyLinks = () => safeRead(FILES.VERIFY_LINKS, {});
// Resolve the verified Discord user for a Pavlov username (set at /verify), or
// null. Lets bans auto-DM the linked account without a discord_user option.
async function dmUserForPavlov(name) {
  const id = loadVerifyLinks()[String(name ?? "").trim().toLowerCase()];
  if (!id) return null;
  try { return await client.users.fetch(id); } catch { return null; }
}
// True if the name matches any Pavlov player the bot has on record: online now,
// known-players registry (seeded from playtime/factions/wages/donators/bans),
// the playtime leaderboard, or the IP log registry.
function isKnownPavlovPlayer(name) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key) return false;
  if (playerCache.server1.some(n => n.toLowerCase() === key)) return true;
  if (playerCache.server2.some(n => n.toLowerCase() === key)) return true;
  if (Object.keys(loadKnownPlayers()).some(k => k.toLowerCase() === key)) return true;
  if (Object.keys(loadPlaytime()).some(k => k.toLowerCase() === key)) return true;
  try { if (ipBans.resolveIds(name).length) return true; } catch {}
  return false;
}

// Post (or re-use) the persistent verification panel in VERIFY_CHANNEL.
async function ensureVerifyPanel() {
  if (!VERIFY_CHANNEL) return;
  let ch; try { ch = await client.channels.fetch(VERIFY_CHANNEL); } catch { return; }
  if (!ch?.isTextBased()) return;
  const saved = safeRead(FILES.VERIFY_PANEL, {});
  if (saved.id) { try { await ch.messages.fetch(saved.id); return; } catch {} }   // panel still there
  const embed = clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("🎫  Mojave Checkpoint — Verification")
    .setDescription(`${hero(randomQuote("verify"))}\n**Halt, courier.** Before the Strip opens to you, the Securitrons need a name on file.\n\nPress **Verify** below and enter your **exact** Pavlov username. Match the registry and the gates swing wide — vault door, NCR checkpoint, the whole Mojave.`));
  const row = new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("verify_start").setLabel("Verify").setStyle(ButtonStyle.Success));
  try { const m = await ch.send({ embeds: [embed], components: [row] }); safeWrite(FILES.VERIFY_PANEL, { id: m.id }); }
  catch (e) { logger.warn("Verify", `panel post failed: ${e.message}`); }
}

// Handle the verification modal submit: check the name, then nickname + role swap.
async function handleVerifySubmit(interaction) {
  const name = sanitizeMessage(interaction.fields.getTextInputValue("verify_name")).trim();
  if (!isKnownPavlovPlayer(name)) {
    return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("📛  Verification Denied")
      .setDescription(`${hero("That name's not in our records, wanderer.")}\nCouldn't find a Pavlov courier named \`${name}\`. Enter your **exact** in-game username — you must have set foot in the Mojave first.`))], ephemeral: true });
  }
  // one identity per courier — reject if this Pavlov name is already claimed by someone else
  const links = loadVerifyLinks();
  const claimedBy = links[name.toLowerCase()];
  if (claimedBy && claimedBy !== interaction.user.id) {
    return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("⛔  Identity Already Claimed")
      .setDescription(`${hero("Two couriers, one name? Not on Mr. House's Strip.")}\nThe Pavlov courier \`${name}\` is already verified to <@${claimedBy}>. If this is really you, contact an admin.`))], ephemeral: true });
  }
  const member = interaction.member;
  const notes = [];
  try { await member.setNickname(name.slice(0, 32)); notes.push("nickname set"); }
  catch { notes.push("⚠️ couldn't set nickname (bot role must be above yours)"); }
  try { await member.roles.remove(VERIFY_UNVERIFIED_ROLE); } catch {}
  try { await member.roles.add(VERIFY_VERIFIED_ROLE); notes.push("verified role granted"); }
  catch { notes.push("⚠️ couldn't change roles (check bot Manage Roles + role order)"); }
  // persist the Pavlov-name -> Discord-id link so bans can DM this user later.
  // Drop any previous name this user held so each Discord account maps to one name.
  for (const k of Object.keys(links)) if (links[k] === member.id) delete links[k];
  links[name.toLowerCase()] = member.id;
  safeWrite(FILES.VERIFY_LINKS, links);
  logger.info("Verify", `${member.user.tag} verified as Pavlov "${name}"`);
  return interaction.reply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("✅  Welcome to the Strip — Verified")
    .setDescription(`${hero("Identity confirmed. The Mojave welcomes you.")}\nLinked to Pavlov courier \`${name}\`. ${notes.join(" · ")}`))], ephemeral: true });
}

/* ================================================================
   MENU GRANT PERSISTENCE
   ================================================================ */
function addMenuGrant(playerId, server, menuValue, menuId, grantedBy) {
  const grants = loadMenuGrants();
  const key    = playerId.toLowerCase();
  if (!grants[key]) grants[key] = [];
  grants[key] = grants[key].filter(g => !(g.menuValue === menuValue && g.server === server));
  grants[key].push({ server, menuValue, menuId, grantedBy, at: Date.now() });
  saveMenuGrants(grants);
}

function removeMenuGrant(playerId, server, menuValue) {
  const grants = loadMenuGrants();
  const key    = playerId.toLowerCase();
  if (!grants[key]) return;
  grants[key] = grants[key].filter(g => !(g.menuValue === menuValue && g.server === server));
  if (!grants[key].length) delete grants[key];
  saveMenuGrants(grants);
}

async function reapplyMenuGrants(playerId, server) {
  const grants = loadMenuGrants();
  const key    = playerId.toLowerCase();
  const list   = (grants[key] ?? []).filter(g => g.server === server || g.server === "both");
  for (const g of list) {
    try {
      await sendRcon(`GiveMenu ${playerId} ${g.menuId}`, server, 3000, 1);
      logger.debug("Menu", `Re-applied ${g.menuValue} to ${playerId} on ${server}`);
    } catch (err) {
      logger.warn("Menu", `Failed to re-apply ${g.menuValue} to ${playerId}: ${err.message}`);
    }
  }
}

const prevCache = { server1: new Set(), server2: new Set() };

async function refreshPlayerCacheWithMenuReapply(server) {
  const prev = new Set(prevCache[server]);
  await refreshPlayerCache(server);
  for (const name of playerCache[server]) {
    if (!prev.has(name.toLowerCase())) {
      prevCache[server].add(name.toLowerCase());
      reapplyMenuGrants(name, server).catch(() => {});
    }
  }
  prevCache[server] = new Set(playerCache[server].map(n => n.toLowerCase()));
}

async function rconHealthCheck() {
  for (const srv of ["server1", "server2"]) {
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

/* ================================================================
   INTERVALS
   ================================================================ */
setInterval(processExpiredBans,      60_000);
setInterval(postLeaderboard,         LEADERBOARD_INTERVAL_MS);
setInterval(postPlaytimeLeaderboard, LEADERBOARD_INTERVAL_MS);
setInterval(postPlayerList,          PLAYERLIST_INTERVAL_MS);
setInterval(rconHealthCheck,         RCON_HEALTH_INTERVAL_MS);
setInterval(async () => {
  await refreshPlayerCacheWithMenuReapply("server1");
  await refreshPlayerCacheWithMenuReapply("server2");
  tickPlaytime();
}, 60_000);
setInterval(processWagePayout, WAGE_INTERVAL_MS);

setTimeout(postLeaderboard, 20_000);
setTimeout(postPlaytimeLeaderboard, 25_000);
setTimeout(() => {
  const due = loadWages().filter(w => WAGE_TIERS[w.tier]?.weekly && (!w.lastPaidAt || Date.now() - w.lastPaidAt >= WAGE_INTERVAL_MS * 0.9));
  if (due.length) { logger.info("Wages", `${due.length} overdue payout(s), processing in 15s...`); setTimeout(processWagePayout, 15_000); }
}, 5_000);

/* ================================================================
   SLASH COMMAND DEFINITIONS
   ================================================================ */
function serverOption(o) {
  return o.setName("server").setDescription("Which server to target").setRequired(true)
    .addChoices({ name: "Server 1", value: "server1" }, { name: "Server 2", value: "server2" }, { name: "Both", value: "both" });
}

const factionChoices = ALL_FACTIONS.map(f => ({ name: f, value: f }));

const ALL_RANK_NAMES = [...new Set(
  Object.values(FACTION_RANKS).flatMap(cfg => cfg.order)
)].map(r => ({ name: r, value: r }));

const commands = [
  new SlashCommandBuilder().setName("help").setDescription("Show all commands and your current access level"),
  new SlashCommandBuilder().setName("ping").setDescription("Bot and server health check with uptime"),
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
  new SlashCommandBuilder().setName("warn")
    .setDescription("Issue a warning to a courier — auto-escalates to bans at thresholds")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for warning").setRequired(true).addChoices(...BAN_REASONS))
    .addStringOption(serverOption)
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("warnings")
    .setDescription("Check a courier's warning history")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("clearwarnings")
    .setDescription("🔒 Admin — Clear all warnings for a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("delwarn")
    .setDescription("🛡️ Mod — Remove a single warning by its number (see /warnings)")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("number").setDescription("Warning number to remove (from /warnings)").setRequired(true).setMinValue(1)),
  new SlashCommandBuilder().setName("seen")
    .setDescription("Show when a courier was last seen online")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("note")
    .setDescription("Staff notes on a courier")
    .addSubcommand(s => s.setName("add")
      .setDescription("🛡️ Mod — Add a staff note to a courier")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("note").setDescription("Note text").setRequired(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("🛡️ Mod — View staff notes on a courier")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("clear")
      .setDescription("🔒 Admin — Delete all staff notes on a courier")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))),
  new SlashCommandBuilder().setName("history")
    .setDescription("View full moderation history for a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("staffactivity")
    .setDescription("🔒 Admin — All moderation actions taken by a staff member")
    .addUserOption(o => o.setName("staff").setDescription("Staff member to audit").setRequired(true)),
  new SlashCommandBuilder().setName("tempban")
    .setDescription("Exile a courier for a set period")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("duration").setDescription("How long to exile").setRequired(true)
      .addChoices(
        { name: "1 Hour",   value: "1h"  }, { name: "6 Hours",  value: "6h"  }, { name: "1 Day",    value: "1d"  },
        { name: "3 Days",   value: "3d"  }, { name: "5 Days",   value: "5d"  }, { name: "1 Week",   value: "1w"  },
        { name: "2 Weeks",  value: "2w"  }, { name: "1 Month",  value: "1mo" }, { name: "3 Months", value: "3mo" },
        { name: "6 Months", value: "6mo" }, { name: "1 Year",   value: "1y"  }
      ))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("reason").setDescription("Grounds for exile").setRequired(true).addChoices(...BAN_REASONS))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("unban")
    .setDescription("Lift a courier's exile")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID to pardon").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("checkban")
    .setDescription("Check if a courier is currently exiled")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("banlist").setDescription("View all active exiles").addStringOption(serverOption),
  new SlashCommandBuilder().setName("permban")
    .setDescription("🔒 Admin — Permanently exile a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("reason").setDescription("Grounds").setRequired(true).addChoices(...BAN_REASONS))
    .addStringOption(o => o.setName("notes").setDescription("Additional context"))
    .addUserOption(o => o.setName("discord_user").setDescription("Discord account to DM the punishment details to")),
  new SlashCommandBuilder().setName("cleartempbans").setDescription("🔒 Admin — Clear all temporary exiles (confirmation required)"),
  new SlashCommandBuilder().setName("donator")
    .setDescription("🔒 Admin — Manage the donator whitelist file")
    .addSubcommand(s => s.setName("add")
      .setDescription("Add a player to the donator file")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a player from the donator file")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all players in the donator file")),
  new SlashCommandBuilder().setName("setroles")
    .setDescription("🔒 Admin — Configure role permissions")
    .addRoleOption(o => o.setName("mod_role").setDescription("Moderator role"))
    .addRoleOption(o => o.setName("admin_role").setDescription("Admin role"))
    .addRoleOption(o => o.setName("faction_leader_role").setDescription("Faction Leader role")),
  new SlashCommandBuilder().setName("acceptstaffapp")
    .setDescription("🔒 Admin — Accept a staff application: DM the applicant and grant staff roles")
    .addUserOption(o => o.setName("user").setDescription("The accepted applicant").setRequired(true)),
  new SlashCommandBuilder().setName("denystaffapp")
    .setDescription("🔒 Admin — Deny a staff application: DM the applicant (no other action)")
    .addUserOption(o => o.setName("user").setDescription("The denied applicant").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Optional reason shown in the DM")),
  new SlashCommandBuilder().setName("announce")
    .setDescription("📢 Mod — Broadcast a message via RCON Notify")
    .addStringOption(o => o.setName("message").setDescription("Message to broadcast (max 200 chars)").setRequired(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("target").setDescription("Who to notify: a specific courier, or All").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("givemenu")
    .setDescription("🔒 Admin — Grant RCON menu access to a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("menu").setDescription("Menu to grant").setRequired(true)
      .addChoices(...MENUS.map(m => ({ name: m.name, value: m.value })))),
  new SlashCommandBuilder().setName("stripmenu")
    .setDescription("🔒 Admin — Revoke RCON menu access from a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("menu").setDescription("Menu to revoke").setRequired(true)
      .addChoices(...MENUS.map(m => ({ name: m.name, value: m.value })))),
  new SlashCommandBuilder().setName("stripmenuall")
    .setDescription("👁️ Owner — Revoke a menu from EVERY player who holds it (both servers)")
    .addStringOption(o => o.setName("menu").setDescription("Menu to revoke from everyone").setRequired(true)
      .addChoices(...MENUS.map(m => ({ name: m.name, value: m.value })))),
  new SlashCommandBuilder().setName("configure")
    .setDescription("Owner menu"),
  new SlashCommandBuilder().setName("clearallbans")
    .setDescription("👁️ Owner — Unban everyone (runs Unban per player on both servers)"),

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
      .setDescription("⚔️ Faction Leader — Set or change a member's rank within a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID (pick the faction first)").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("rank").setDescription("New rank to assign (faction-specific)").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("transfer")
      .setDescription("🛡️ Mod — Transfer a player from one faction to another")
      .addStringOption(o => o.setName("from_faction").setDescription("Current faction").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("to_faction").setDescription("Destination faction").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID (pick the current faction first)").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("rank").setDescription("Rank in new faction (default: lowest rank)").setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all members of a faction with their ranks")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("overview")
      .setDescription("Show all factions with member counts and officers at a glance"))
    .addSubcommand(s => s.setName("audit")
      .setDescription("View recent add/remove/rank changes for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("setcap")
      .setDescription("🔒 Admin — Set the maximum member cap for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addIntegerOption(o => o.setName("cap").setDescription("Maximum number of members (1–500)").setRequired(true).setMinValue(1).setMaxValue(500)))
    .addSubcommand(s => s.setName("setrankcap")
      .setDescription("🔒 Admin — Set the per-rank member cap within a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Rank to cap (faction-specific)").setRequired(true).setAutocomplete(true))
      .addIntegerOption(o => o.setName("cap").setDescription("Max members at this rank (0 = unlimited)").setRequired(true).setMinValue(0).setMaxValue(500))),

  new SlashCommandBuilder().setName("manual")
    .setDescription("🔒 Admin — Send a raw RCON command")
    .addStringOption(o => o.setName("command").setDescription("Raw RCON signal").setRequired(true))
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("rotatemap")
    .setDescription("🔒 Admin — Rotate the map (confirmation required)")
    .addStringOption(serverOption),
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
  new SlashCommandBuilder().setName("transfercaps")
    .setDescription("🔒 Admin — Move caps between two courier ledgers")
    .addStringOption(o => o.setName("from_id").setDescription("Courier to deduct from").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("to_id").setDescription("Courier to credit").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Number of caps to transfer").setRequired(true).setMinValue(1)),
  new SlashCommandBuilder().setName("adjustcaps")
    .setDescription("🔒 Admin — Manually add or subtract caps from a courier's ledger")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Caps to add (positive) or subtract (negative)").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for adjustment (logged)")),
  new SlashCommandBuilder().setName("stats")
    .setDescription("Courier dossier: playtime, factions, balance, and mod history")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("inspect")
    .setDescription("👁️ Owner — Full dossier on a courier (everything, incl. IPs & alts)")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)),
].map(c => c.toJSON());

/* ================================================================
   READY
   ================================================================ */
client.once("ready", async () => {
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
    const result = await rest.put(Routes.applicationCommands(process.env.CLIENT_ID), { body: commands });
    logger.info("Bot", `${result.length} slash commands registered`);
  } catch (err) {
    logger.error("Bot", `Command registration failed: ${err.message}`);
  }
  seedKnownPlayers();   // backfill the offline-autocomplete registry from existing data
  ipBans.init({
    // Fired once a player's IP is CONFIRMED (the same-line disconnect pairing) —
    // posts an accurate name · ID · IP entry to the connection-feed webhook.
    onConfirm: async ({ name, ip, server, record }) => {
      if (!feedHook) return;
      const srvName = /1$/.test(String(server)) ? "Server 2" : (server ? "Server 1" : "unknown");
      // everything Pavlov.log knows about this player (resolved by id inside ipBans)
      const rec = record || { ips: [], cips: [], alts: [], firstSeen: null, lastSeen: null };
      const ts = (ms) => ms ? `<t:${Math.floor(ms / 1000)}:f>` : "unknown";
      const tsR = (ms) => ms ? `<t:${Math.floor(ms / 1000)}:R>` : "unknown";
      const embed = clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("🟢  Courier Logged — IP Confirmed")
        .setDescription(`${hero(randomQuote("connect"))}`)
        .addFields(
          { name: "🎯  Name",       value: `\`${name}\``,                                            inline: true },
          { name: "🌐  Current IP", value: `\`${ip ?? "unknown"}\``,                                 inline: true },
          { name: "🖥️  Server",     value: srvName,                                                  inline: true },
          { name: `📍  Confirmed IPs (${rec.cips.length})`, value: (rec.cips.length ? rec.cips.map(x => `\`${x}\``).join("  ·  ") : "*none*").slice(0, 1024), inline: false },
          { name: "📅  First Seen",  value: ts(rec.firstSeen),                                        inline: true },
          { name: "👁️  Last Seen",  value: tsR(rec.lastSeen),                                        inline: true },
          { name: `🔗  Known Alts (${rec.alts.length})`, value: (rec.alts.length ? rec.alts.map(a => `\`${a}\``).join("  ·  ") : "*none*").slice(0, 1024), inline: false },
        ), "Connection log · Mojave Authority");
      feedHook.send({ embeds: [embed] }).catch(err => logger.warn("Feed", `webhook post failed: ${err.message}`));
    },
    // Fired when someone CONNECTS (live log) matching a blacklisted username/IP:
    // ban that username on both servers (Shack bans by name, not hex id).
    onAutoBan: async ({ name, ip, reason }) => {
      await banWithIp(name, "both");
      writeModLog({ action: "auto-ipban", playerId: name, reason: `Auto-ban — ${reason || "blacklist match"}${ip ? ` (${ip})` : ""}`, by: "IP-Guard" });
      logger.warn("IPGuard", `Auto-banned ${name} — ${reason || "blacklist match"}${ip ? ` (${ip})` : ""}`);
      const banEmbed = clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("🛑  Blacklisted Courier Blocked")
        .setDescription(`${hero(randomQuote("autoban"))}`)
        .addFields(
          { name: "🎯  Courier", value: `\`${name}\``,            inline: true },
          { name: "🌐  IP",      value: `\`${ip ?? "unknown"}\``, inline: true },
          { name: "🚫  Reason",  value: reason || "blacklist match", inline: true },
        ), "Auto-ban · both servers");
      await logBan(banEmbed);   // dedicated ban-log channel (falls back to mod-log)
      // also surface it in the connection feed (the channel you watch for joins)
      if (feedHook) feedHook.send({ embeds: [banEmbed] }).catch(err => logger.warn("Feed", `auto-ban post failed: ${err.message}`));
    },
  });
  refreshPlayerCache("server1");
  refreshPlayerCache("server2");
  setTimeout(rconHealthCheck, 5_000);
  ensureVerifyPanel();
});

/* ================================================================
   GRACEFUL SHUTDOWN
   ================================================================ */
function shutdown(signal) {
  logger.info("Bot", `${signal} received — shutting down`);
  process.exit(0);
}
process.on("SIGINT",  () => shutdown("SIGINT"));
process.on("SIGTERM", () => shutdown("SIGTERM"));
process.on("uncaughtException",  err => logger.error("Uncaught",  err.message, { stack: err.stack }));
process.on("unhandledRejection", r   => logger.error("Unhandled", String(r)));

/* ================================================================
   INTERACTIONS
   ================================================================ */
client.on("interactionCreate", async (interaction) => {

  /* ── Blacklist gate — barred users get nothing, on every interaction.
        Owners are immune and can never be blacklisted. ── */
  if (isBlacklisted(interaction.user.id) && !isOwner(interaction.user.id)) {
    if (interaction.isAutocomplete()) return interaction.respond([]).catch(() => {});
    if (interaction.isChatInputCommand()) {
      return interaction.reply({ embeds: [blacklistedEmbed()], ephemeral: true }).catch(() => {});
    }
    return;
  }

  /* ── Verification button + modal ─────────────────────────── */
  if (interaction.isButton() && interaction.customId === "verify_start") {
    const modal = new ModalBuilder().setCustomId("verify_modal").setTitle("Verify your Pavlov account")
      .addComponents(new ActionRowBuilder().addComponents(
        new TextInputBuilder().setCustomId("verify_name").setLabel("Your exact Pavlov username")
          .setStyle(TextInputStyle.Short).setRequired(true).setMaxLength(64)));
    return interaction.showModal(modal).catch(() => {});
  }
  if (interaction.isModalSubmit() && interaction.customId === "verify_modal") {
    return handleVerifySubmit(interaction).catch(e => logger.warn("Verify", e.message));
  }

  /* ── Autocomplete ─────────────────────────────────────── */
  if (interaction.isAutocomplete()) {
    const focused  = interaction.options.getFocused(true);
    const cmdName  = interaction.commandName;

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
        out.unshift({ name: `${focused.value} (manual entry)`, value: focused.value });
      }
      return interaction.respond(out.slice(0, 25)).catch(() => {});
    }

    // Default: currently-online players, then previously-seen (offline) ones.
    const server  = interaction.options.getString("server") ?? null;
    const choices = getPlayerChoices(server, query);
    if (query && !choices.find(c => c.value.toLowerCase() === query)) {
      choices.unshift({ name: `${focused.value} (manual entry)`, value: focused.value });
    }
    // /announce target field also offers "All"
    if (cmdName === "announce" && focused.name === "target" && (!query || "all".includes(query))) {
      choices.unshift({ name: "📢  All players", value: "all" });
    }
    return interaction.respond(choices.slice(0, 25)).catch(() => {});
  }

  if (!interaction.isChatInputCommand()) return;

  /* ── Permission routing ───────────────────────────────── */
  const PUBLIC         = ["help", "ping", "serverinfo", "find", "banlist", "checkban", "wagelist", "checkbalance", "stats", "warnings", "seen"];
  const MOD_COMMANDS   = ["kick", "warn", "tempban", "unban", "announce", "givecaps", "history", "delwarn", "note"];
  const FL_COMMANDS    = ["addwage", "removewage", "faction"];
  const ADMIN_COMMANDS = ["permban", "cleartempbans", "clearwarnings", "setroles", "givemenu", "stripmenu", "manual", "rotatemap", "transfercaps", "adjustcaps", "donator", "acceptstaffapp", "denystaffapp", "staffactivity"];

  const name = interaction.commandName;

  if (!PUBLIC.includes(name)) {
    if (ADMIN_COMMANDS.includes(name) && !hasAdminRole(interaction.member)) {
      return interaction.reply({ embeds: [adminOnlyEmbed()], ephemeral: true });
    }
    if (FL_COMMANDS.includes(name) && !hasModRole(interaction.member) && !hasFactionLeaderRole(interaction.member)) {
      return interaction.reply({ embeds: [factionLeaderOnlyEmbed()], ephemeral: true });
    }
    if (MOD_COMMANDS.includes(name) && !hasModRole(interaction.member)) {
      return interaction.reply({ embeds: [modOnlyEmbed()], ephemeral: true });
    }
  }

  if (!ADMIN_COMMANDS.includes(name) && !PUBLIC.includes(name) && !isOwner(interaction.user.id)) {
    if (!checkRateLimit(interaction.user.id, name, 4000)) {
      return interaction.reply({ embeds: [rateLimitEmbed()], ephemeral: true });
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
        if (isAdmin)      { badge = "🔒  **ADMIN**";          color = NV.AMBER;     }
        else if (isMod)   { badge = "🛡️  **MODERATOR**";      color = NV.NCR_TAN;   }
        else if (isFLead) { badge = "⚔️  **FACTION LEADER**"; color = NV.GOLD;      }
        else              { badge = "🌐  **PUBLIC ACCESS**";  color = NV.BLUE_VATS; }

        const mStr = modRoleId           ? `<@&${modRoleId}>`           : "`not set`";
        const aStr = adminRoleId         ? `<@&${adminRoleId}>`         : "`not set`";
        const fStr = factionLeaderRoleId ? `<@&${factionLeaderRoleId}>` : "`not set`";

        const rankSummaryLines = ALL_FACTIONS.map(f => {
          const cfg = getFactionRankConfig(f);
          const rankStr = cfg ? cfg.order.map(r => `${cfg.badges[r]} ${r}`).join(" → ") : "*no ranks*";
          return `**${f}:** ${rankStr}`;
        }).join("\n");

        const embed = new EmbedBuilder().setColor(color)
          .setTitle("🎰  Mojave Authority — Command Roster")
          .setDescription(
            `> *"War. War never changes. But the rules of the Strip — those we enforce."*\n\n${DIVIDER}\n` +
            `**Your Access:** ${badge}\n🛡️ Mod: ${mStr}  ·  🔒 Admin: ${aStr}  ·  ⚔️ Faction: ${fStr}\n${DIVIDER}\n\n` +
            `💡 *Autocomplete works in all Courier ID and Rank fields — faction ranks are filtered per faction.*`
          )
          .addFields(
            { name: "🌐  Public",
              value: "`/help` `/ping` `/serverinfo` `/find` `/checkban` `/banlist` `/stats` `/checkbalance` `/wagelist` `/warnings` `/seen`\n`/faction list` `/faction overview` `/faction audit`" },
            { name: "🛡️  Moderator",
              value: [
                "`/kick <id> <server> [reason]` — Eject",
                "`/warn <id> <reason> <server>` — Issue warning *(auto-bans at 3/5/7)*",
                "`/delwarn <id> <number>` — Remove a single warning",
                "`/tempban <id> <duration> <server> <reason>` — Temporary exile",
                "`/unban <id> <server>` — Lift exile",
                "`/announce <msg> <server> <target>` — RCON Notify a player or All",
                "`/history <id>` — View mod action history",
                "`/note add|list <id>` — Staff notes on a courier",
                "`/givecaps <id> <amount> [reason]` — Give caps to a courier",
                "`/faction transfer <id> <from> <to> [rank]` — Move player between factions",
              ].join("\n") },
            { name: "⚔️  Faction Leader",
              value: [
                "`/faction add <id> <faction> [rank]` — Whitelist player (optional starting rank)",
                "`/faction remove <id> <faction>` — Remove from whitelist",
                "`/faction rank <id> <faction> <rank>` — Set member rank *(FL only)*",
                "`/faction list <faction>` — Roster with ranks (◀ ▶ pages)",
                "`/faction overview` — All factions at a glance",
                "`/faction audit <faction>` — Add/remove/rank change log (◀ ▶ pages)",
                "`/addwage <id> <tier>` — Enrol in payroll or issue mercenary pay",
                "`/removewage <id>` — Remove from payroll",
              ].join("\n") },
            { name: "🔒  Admin",
              value: [
                "`/permban <id> <server> <reason>` — Permanent ban",
                "`/clearwarnings <id>` — Wipe all warnings for a courier",
                "`/note clear <id>` — Delete all staff notes for a courier",
                "`/cleartempbans` `/setroles`",
                "`/staffactivity <staff>` — All mod actions by a staff member",
                "`/givemenu` `/stripmenu` `/transfercaps` `/adjustcaps`",
                "`/rotatemap` `/manual`",
                "`/donator add|remove|list <id>` — Manage the donator whitelist file",
                "`/inspect <id>` — 👁️ *Owner only* — full dossier (everything, incl. IPs & alts)",
                "`/stripmenuall <menu>` — 👁️ *Owner only* — revoke a menu from EVERY holder",
                "`/configure` — 👁️ *Owner only* — hidden control panel (IP tracker management)",
                "`/clearallbans` — 👁️ *Owner only* — unban everyone (runs Unban per player)",
                "`/acceptstaffapp <user>` — DM acceptance + grant staff roles",
                "`/denystaffapp <user> [reason]` — DM a denial (no other action)",
                "`/faction setcap <faction> <cap>` — Set faction size limit",
                "`/faction setrankcap <faction> <rank> <cap>` — Cap members per rank (0 = unlimited)",
              ].join("\n") },
            { name: "⚔️  Faction Ranks (per faction)",
              value: rankSummaryLines },
            { name: "⚙️  Automation",
              value: [
                "🔄  Temp bans auto-lifted every **60s**",
                "📊  Leaderboard auto-posted every **6h**",
                "💰  Wages disbursed every **7 days**",
                "🏥  RCON health check every **5 min**",
                `⚠️  Warn thresholds: **3** → 1d ban  ·  **5** → 1w ban  ·  **7** → permban`,
                "🎖️  Rank changes update both the rank registry and the rank-specific spawn files automatically",
                "📨  `/kick` `/warn` `/tempban` `/permban` accept an optional **discord_user** — the bot DMs them their punishment details",
                "⛔  Command blacklist is set via **`BLACKLIST_IDS`** in `.env` (restart to apply)",
              ].join("\n") },
          )
          .setFooter({ text: `${BUILD_ID}  ·  ${BOT_COPYRIGHT}` });
        brand(embed, { thumb: true });
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         PING
         ───────────────────────────────────────────────────── */
      case "ping": {
        await interaction.deferReply({ ephemeral: true });
        const start = Date.now();
        const [r1, r2] = await Promise.allSettled([
          sendRcon("RefreshList", "server1", 2000, 0),
          sendRcon("RefreshList", "server2", 2000, 0),
        ]);
        const rtt  = Date.now() - start;
        const s1ok = r1.status === "fulfilled" && parseRcon(r1.value)?.Successful;
        const s2ok = r2.status === "fulfilled" && parseRcon(r2.value)?.Successful;
        const okCount = (s1ok ? 1 : 0) + (s2ok ? 1 : 0);
        const color = okCount === 2 ? NV.IRRAD_GREEN : okCount === 1 ? NV.AMBER : NV.RUST_RED;
        const headline = okCount === 2 ? "All systems nominal — Securitron network active."
          : okCount === 1 ? "Partial connectivity — one server unreachable."
          : "Both servers unreachable — check RCON config.";
        const wsPing = Math.max(0, client.ws.ping);
        const health = `${pip(true)}${pip(s1ok)}${pip(s2ok)}`;
        const embed = new EmbedBuilder().setColor(color)
          .setTitle("📡  System Status")
          .setDescription(`${hero(headline)}\n${health}  ·  **${okCount + 1}/3** nodes online`)
          .addFields(
            { name: "🤖  Bot",        value: `${pip(true)}  Online\n\`gateway ${wsPing}ms\``,                    inline: true },
            { name: "1️⃣  Server 1",   value: s1ok ? `${pip(true)}  Reachable` : `${pip(false)}  Unreachable`,    inline: true },
            { name: "2️⃣  Server 2",   value: s2ok ? `${pip(true)}  Reachable` : `${pip(false)}  Unreachable`,    inline: true },
            { name: "⚡  RTT",        value: `\`${bar(Math.min(rtt, 1000), 1000, 10)}\`\n\`${rtt}ms\``,           inline: true },
            { name: "⏱️  Uptime",     value: `\`${formatUptime(Date.now() - BOT_START_MS)}\``,                    inline: true },
            { name: "👥  Cached",     value: `S1 \`${playerCache.server1.length}\` · S2 \`${playerCache.server2.length}\``, inline: true },
            { name: "💾  Mod Log",    value: `\`${loadModLog().length}\` entries`,                                inline: true },
            { name: "⚠️  Open Bans",  value: `\`${loadBans().length}\` active`,                                  inline: true },
            { name: "🔖  Build",      value: `\`${BUILD_ID}\``,                                                   inline: true },
          );
        brand(embed, { thumb: true, footer: { text: `${BOT_COPYRIGHT}  ·  authored by ${BOT_AUTHOR}` } });
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
        const servers = server === "both" ? ["server1", "server2"] : [server];
        const infos   = await Promise.all(servers.map(fetchInfo));
        const embeds  = infos.map((info, i) => {
          const srv = servers[i];
          const e = new EmbedBuilder()
            .setColor(info.ok ? NV.IRRAD_GREEN : NV.RUST_RED)
            .setTitle(`${serverEmoji(srv)}  ${info.serverName}`)
            .setDescription(`${pip(info.ok)}  ${info.ok ? "Online" : "Offline"}  ·  \`${bar(info.players, Number(info.maxPlayers) || info.players || 1, 10)}\``)
            .addFields(
              { name: "🗺️  Map",     value: info.mapLabel,                          inline: true },
              { name: "🎮  Mode",    value: info.gameMode,                          inline: true },
              { name: "👥  Players", value: `${info.players} / ${info.maxPlayers}`, inline: true },
            );
          return brand(e, { footer: { text: `${serverLabel(srv)} · live data` } });
        });
        return interaction.editReply({ embeds });
      }

      /* ─────────────────────────────────────────────────────
         FIND
         ───────────────────────────────────────────────────── */
      case "find": {
        const query = interaction.options.getString("name").toLowerCase();
        await interaction.deferReply({ ephemeral: true });
        await Promise.all([refreshPlayerCache("server1"), refreshPlayerCache("server2")]);
        const matches = [];
        const seen    = new Set();
        for (const srv of ["server1", "server2"]) {
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
            brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("🔍  No Matches Found")
              .setDescription(`${hero(`No couriers matching "${query}" are online.`)}\n*Try a shorter search term.*`))
          ]});
        }
        const lines = matches.map((m) => {
          const srvStr = m.servers.map(s => s === "server1" ? "S1" : "S2").join("+");
          const warn   = loadWarns()[m.name.toLowerCase()]?.length ?? 0;
          return `\`[${srvStr}]\`  **${m.name}**${warn ? `  ·  ⚠️ ${warn} warn${warn !== 1 ? "s" : ""}` : ""}`;
        });
        return interaction.editReply({ embeds: [
          brand(new EmbedBuilder().setColor(NV.AMBER).setTitle(`🔍  Search Results — "${query}"`)
            .setDescription(`${hero(`**${matches.length}** match${matches.length !== 1 ? "es" : ""} found.`)}\n${lines.join("\n")}`),
            { footer: { text: `⚠️ = warnings on record` } })
        ]});
      }

      /* ─────────────────────────────────────────────────────
         KICK  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "kick": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const server   = interaction.options.getString("server");
        const reason   = interaction.options.getString("reason") ?? "No reason provided";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        await sendRconBoth(`Kick ${playerId}`, server);
        writeModLog({ action: "kick", playerId, reason, by: interaction.user.tag, server });
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("👢  Courier Ejected from the Strip")
          .setDescription(`> *${randomQuote("kick")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier", value: `\`${playerId}\``,                                  inline: true },
            { name: "🖥️  Server",  value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "🛡️  By",      value: `${interaction.user}`,                              inline: true },
            { name: "📋  Reason",  value: reason,                                             inline: false },
          ).setFooter({ text: "Kick logged — no ban issued" }).setTimestamp();
        const kTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId);
        const kDm = await dmPunishmentNotice(kTarget, {
          action: "Kick", color: NV.NCR_TAN, playerId, reason,
          fields: [{ name: "🖥️  Server", value: serverLabel(server), inline: true }],
        });
        const kDmField = dmStatusField(kDm, kTarget);
        if (kDmField) embed.addFields(kDmField);
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         WARN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "warn": {
        const playerId  = sanitizeId(interaction.options.getString("playerid"));
        const reasonKey = interaction.options.getString("reason");
        const server    = interaction.options.getString("server") ?? "server1";
        const reason    = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                         // ← ADDED
        const { count, escalated } = await issueWarn(playerId, reason, interaction.user.tag, server, interaction);
        const threshold = WARN_THRESHOLDS.find(t => t.count === count);
        const embed = new EmbedBuilder()
          .setColor(escalated ? NV.RUST_RED : NV.NCR_TAN)
          .setTitle(escalated ? "⚠️  Warning Issued — Auto-Ban Triggered" : "⚠️  Warning Issued")
          .setDescription(`> *${randomQuote("warn")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",   value: `\`${playerId}\``,     inline: true },
            { name: "⚠️  Warning #", value: `**${count}**`,        inline: true },
            { name: "🛡️  By",        value: `${interaction.user}`, inline: true },
            { name: "⚖️  Offense",   value: reason,                inline: false },
          );
        if (escalated?.type === "tempban") {
          embed.addFields({ name: "🔴  Auto-Escalation", value: `Threshold reached — courier automatically **temp-banned** for **${escalated.label}**.`, inline: false });
        } else if (escalated?.type === "permban") {
          embed.addFields({ name: "🔴  Auto-Escalation", value: "Threshold reached — courier automatically **permanently banned**.", inline: false });
        } else if (threshold) {
          embed.addFields({ name: "🟡  Threshold Reached", value: `Next escalation: **${threshold.label}**`, inline: false });
        } else {
          const next = WARN_THRESHOLDS.find(t => t.count > count);
          if (next) embed.addFields({ name: "📊  Progress", value: `${count}/${next.count} warnings — next: **${next.label}**`, inline: false });
        }
        embed.setFooter({ text: `Total warnings: ${count}` }).setTimestamp();
        const wExtra = [{ name: "⚠️  Warning #", value: `**${count}**`, inline: true }];
        if (escalated?.type === "tempban") wExtra.push({ name: "🔴  Escalation", value: `Auto temp-ban: **${escalated.label}**`, inline: false });
        else if (escalated?.type === "permban") wExtra.push({ name: "🔴  Escalation", value: "Auto **permanent ban**", inline: false });
        const wTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId);
        const wDm = await dmPunishmentNotice(wTarget, {
          action: "Warning", color: escalated ? NV.RUST_RED : NV.NCR_TAN, playerId, reason, fields: wExtra,
        });
        const wDmField = dmStatusField(wDm, wTarget);
        if (wDmField) embed.addFields(wDmField);
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         WARNINGS
         ───────────────────────────────────────────────────── */
      case "warnings": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const all   = loadWarns()[playerId.toLowerCase()] ?? [];
        const count = all.length;
        if (!count) {
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("✅  No Warnings on Record")
              .setDescription(`\`${playerId}\` has a clean record — no warnings issued.`).setTimestamp()
          ], ephemeral: true });
        }
        const next  = WARN_THRESHOLDS.find(t => t.count > count);
        const lines = all.map((w, i) => {
          const ts = Math.floor(w.at / 1000);
          return `\`${String(i + 1).padStart(2, "0")}\`  **${w.reason}**  ·  by *${w.by}*  ·  <t:${ts}:R>`;
        });
        const header = `**${count}** warning${count !== 1 ? "s" : ""} on record\n` +
          (next ? `Next escalation at **${next.count}** warnings: *${next.label}*` : "**⛔  Maximum threshold exceeded — perm ban eligible**");
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(count >= 5 ? NV.RUST_RED : NV.NCR_TAN)
            .setTitle(`⚠️  Warning Record — ${playerId}`)
            .setDescription(`${header}\n\n${DIVIDER}\n${pageLines.join("\n")}`),
          { perPage: 12, ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         CLEARWARNINGS
         ───────────────────────────────────────────────────── */
      case "clearwarnings": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const key   = playerId.toLowerCase();
        let count = 0;
        await update(FILES.WARNS, {}, (warns) => {
          count = warns[key]?.length ?? 0;
          if (count) delete warns[key];
          return warns;
        });
        if (!count) {
          return interaction.reply({ embeds: [warningEmbed("No Warnings", `\`${playerId}\` has no warnings to clear.`)], ephemeral: true });
        }
        writeModLog({ action: "clearwarnings", playerId, count, by: interaction.user.tag });
        const embed = successEmbed("Warnings Cleared", `**${count}** warning${count !== 1 ? "s" : ""} cleared for \`${playerId}\`.\n\n**Cleared by:** ${interaction.user}`);
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         DELWARN  (remove one warning by number)
         ───────────────────────────────────────────────────── */
      case "delwarn": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const number   = interaction.options.getInteger("number");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const { removed, remaining } = await removeWarningAt(playerId, number);
        if (!removed) {
          return interaction.reply({ embeds: [warningEmbed("No Such Warning",
            `Warning **#${number}** does not exist for \`${playerId}\`.\n\nUse \`/warnings ${playerId}\` to see valid numbers.`)], ephemeral: true });
        }
        writeModLog({ action: "delwarn", playerId, reason: removed.reason, by: interaction.user.tag });
        const ts = Math.floor(removed.at / 1000);
        const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("🧹  Warning Removed")
          .setDescription(`${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",        value: `\`${playerId}\``,                       inline: true },
            { name: "🗑️  Removed #",       value: `**${number}**`,                          inline: true },
            { name: "📊  Remaining",       value: `**${remaining}** warning${remaining !== 1 ? "s" : ""}`, inline: true },
            { name: "⚖️  Was",             value: `*${removed.reason}*  ·  by *${removed.by}*  ·  <t:${ts}:R>`, inline: false },
            { name: "🛡️  Removed By",      value: `${interaction.user}`,                    inline: false },
          ).setFooter({ text: "Single warning removed — others renumbered" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         SEEN  (last time a courier was online)
         ───────────────────────────────────────────────────── */
      case "seen": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const key    = playerId.toLowerCase();
        const onS1   = playerCache.server1.some(n => n.toLowerCase() === key);
        const onS2   = playerCache.server2.some(n => n.toLowerCase() === key);
        const online = onS1 || onS2;
        const last   = getLastSeen(playerId);
        let color, desc;
        if (online) {
          const where = [onS1 && "Server 1", onS2 && "Server 2"].filter(Boolean).join("  +  ");
          color = NV.IRRAD_GREEN;
          desc  = `🟢  **Online right now** on ${where}.`;
        } else if (last) {
          color = NV.AMBER;
          desc  = `🔴  Last seen <t:${Math.floor(last / 1000)}:R>  ·  <t:${Math.floor(last / 1000)}:F>`;
        } else {
          color = NV.DEAD_GREY;
          desc  = "❔  No sighting on record. This courier hasn't been seen online since the bot started tracking.";
        }
        const embed = new EmbedBuilder().setColor(color).setTitle(`📡  Last Seen — ${playerId}`)
          .setDescription(`${DIVIDER}\n${desc}`)
          .setFooter({ text: "Presence sampled every 60s" }).setTimestamp();
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         NOTE  (freeform staff notes on any courier)
         ───────────────────────────────────────────────────── */
      case "note": {
        const sub      = interaction.options.getSubcommand();
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });

        if (sub === "add") {
          const text  = interaction.options.getString("note").trim().slice(0, 500);
          if (!text) return interaction.reply({ embeds: [errorEmbed("Empty Note", "The note cannot be empty.")], ephemeral: true });
          const count = await addPlayerNote(playerId, text, interaction.user.tag);
          writeModLog({ action: "note-add", playerId, reason: text, by: interaction.user.tag });
          const embed = successEmbed("Note Added", `Staff note added to \`${playerId}\` *(now ${count} note${count !== 1 ? "s" : ""})*.\n\n**Note:** ${text}\n**By:** ${interaction.user}`);
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        if (sub === "clear") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], ephemeral: true });
          }
          const count = await clearPlayerNotes(playerId);
          if (!count) return interaction.reply({ embeds: [warningEmbed("No Notes", `\`${playerId}\` has no staff notes to clear.`)], ephemeral: true });
          writeModLog({ action: "note-clear", playerId, count, by: interaction.user.tag });
          const embed = successEmbed("Notes Cleared", `**${count}** staff note${count !== 1 ? "s" : ""} deleted for \`${playerId}\`.\n\n**By:** ${interaction.user}`);
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        // sub === "list"
        const notes = getPlayerNotes(playerId);
        if (!notes.length) {
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`📝  No Staff Notes — ${playerId}`)
              .setDescription("This courier has no staff notes on record.\n\nUse `/note add` to record one.").setTimestamp()
          ], ephemeral: true });
        }
        const lines = notes.map((n, i) => {
          const ts = Math.floor(n.at / 1000);
          return `\`${String(i + 1).padStart(2, "0")}\`  ${n.text}  ·  *${n.by}*  ·  <t:${ts}:R>`;
        });
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.NCR_TAN).setTitle(`📝  Staff Notes — ${playerId}`)
            .setDescription(`**${notes.length}** note${notes.length !== 1 ? "s" : ""} on record\n\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Staff notes · internal only" }),
          { perPage: 10, ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         HISTORY
         ───────────────────────────────────────────────────── */
      case "history": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const history = getPlayerHistory(playerId);
        if (!history.length) {
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("📋  No Mod History Found")
              .setDescription(`\`${playerId}\` has no moderation history on record.`).setTimestamp()
          ], ephemeral: true });
        }
        const ICONS = { kick: "👢", warn: "⚠️", tempban: "⏳", unban: "🔓", permban: "💀", "auto-unban": "⏰", "auto-tempban": "🤖", "auto-permban": "🤖", "auto-ipban": "🤖", clearwarnings: "🧹", delwarn: "🧹", "note-add": "📝", "note-clear": "🗑️", "donator-add": "💎", "donator-remove": "💎", "wage-payout": "💰", givecaps: "💸", adjustcaps: "⚙️", "faction-add": "⚔️", "faction-remove": "🚪", "faction-rank": "🎖️", "faction-transfer": "↔️" };
        const lines = history.slice().reverse().map(e => {
          const ts     = Math.floor(e.at / 1000);
          const icon   = ICONS[e.action] ?? "📌";
          const detail = e.reason ? ` — *${e.reason}*` : e.amount ? ` — *${e.amount > 0 ? "+" : ""}${e.amount} caps*` : e.faction ? ` — *${e.faction}*` : "";
          return `${icon}  \`${e.action}\`${detail}  ·  by **${e.by ?? "System"}**  ·  <t:${ts}:R>`;
        });
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.AMBER)
            .setTitle(`📋  Moderation History — ${playerId}`)
            .setDescription(`**${history.length}** total action${history.length !== 1 ? "s" : ""} on record *(newest first)*\n\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Mod log — full history retained" }).setTimestamp(),
          { perPage: 12, ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         STAFFACTIVITY — all mod actions taken BY a staff member
         ───────────────────────────────────────────────────── */
      case "staffactivity": {
        const staff = interaction.options.getUser("staff");
        const tag   = (staff.tag || "").toLowerCase();
        const uname = (staff.username || "").toLowerCase();
        const matches = loadModLog().filter(e => {
          const by = String(e.by ?? "").toLowerCase();
          return by && (by === tag || by === uname || by.includes(uname));
        });
        if (!matches.length) {
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Staff Activity — None")
              .setDescription(`No moderation actions on record for ${staff}.`).setTimestamp()
          ], ephemeral: true });
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
        const playerId    = sanitizeId(interaction.options.getString("playerid"));
        const durationKey = interaction.options.getString("duration");
        const server      = interaction.options.getString("server");
        const reasonKey   = interaction.options.getString("reason");
        const reason      = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        const { ms, label } = BAN_DURATIONS[durationKey];
        const expires        = Date.now() + ms;
        const replaced       = loadBans().find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        const ipEnf = await banWithIp(playerId, server);
        await upsertTempBan({ playerId, reason, expires, durationLabel: label, moderator: interaction.user.tag, server });
        writeModLog({ action: "tempban", playerId, reason, duration: label, by: interaction.user.tag, server });
        const ts = Math.floor(expires / 1000);
        const embed = clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("⏳  Courier Exiled from the Mojave")
          .setDescription(`> *${randomQuote("ban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",  value: `\`${playerId}\``,                                inline: true },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`, inline: true },
            { name: "⏱️  Duration", value: `**${label}**`,                                   inline: true },
            { name: "⚖️  Offense",  value: reason,                                           inline: false },
            { name: "🔓  Expires",  value: `<t:${ts}:F>  ·  <t:${ts}:R>`,                     inline: true },
            { name: "🛡️  By",       value: `${interaction.user}`,                            inline: true },
          ), replaced ? `Replaced earlier exile: ${replaced.reason}` : "Auto-lifted when timer expires");
        const tbTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId);
        const tbDm = await dmPunishmentNotice(tbTarget, {
          action: "Temporary Ban", color: NV.RUST_RED, playerId, reason,
          fields: [
            { name: "⏱️  Duration", value: `**${label}**`,            inline: true },
            { name: "🖥️  Server",   value: serverLabel(server),       inline: true },
            { name: "🔓  Expires",  value: `<t:${ts}:F>  ·  <t:${ts}:R>`, inline: false },
          ],
        });
        const tbDmField = dmStatusField(tbDm, tbTarget);
        if (tbDmField) embed.addFields(tbDmField);
        if (ipEnf?.field) embed.addFields(ipEnf.field);
        brand(embed); await logBan(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         UNBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "unban": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const server   = interaction.options.getString("server");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        await sendRconBoth(`Unban ${playerId}`, server);
        const removed = loadBans().some(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        await removeBans(playerId);
        let cleared = null;
        try { cleared = ipBans.unblacklistPlayer(playerId); } catch {}   // also lift IP/username/ID blacklist
        writeModLog({ action: "unban", playerId, by: interaction.user.tag, server });
        const c = cleared?.cleared;
        const ipLifted = c && (c.ips + c.names) > 0
          ? `Cleared ${c.ips} IP(s) and ${c.names} username flag(s).`
          : "Nothing was flagged for this player.";
        const embed = clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("🔓  Exile Lifted — Welcome Back to the Strip")
          .setDescription(`> *${randomQuote("unban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",     value: `\`${playerId}\``,     inline: true },
            { name: "🖥️  Server",      value: serverLabel(server),   inline: true },
            { name: "🛡️  Pardoned By", value: `${interaction.user}`, inline: true },
            { name: "📋  Record",       value: removed ? "Temp ban record cleared." : "No temp ban record — RCON Unban sent.", inline: false },
            { name: "🌐  IP Enforcement", value: ipLifted, inline: false },
          ));
        await logBan(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         CHECKBAN
         ───────────────────────────────────────────────────── */
      case "checkban": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const server   = interaction.options.getString("server");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const tb = loadBans().find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        if (tb) {
          const ts = Math.floor(tb.expires / 1000);
          return interaction.reply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("⏳  Temporary Exile Active")
              .setDescription(`${DIVIDER}`)
              .addFields(
                { name: "🎯  Courier",   value: `\`${playerId}\``,                  inline: true },
                { name: "🖥️  Server",    value: serverLabel(server),                inline: true },
                { name: "⏱️  Duration",  value: tb.durationLabel ?? "?",            inline: true },
                { name: "⚖️  Offense",   value: tb.reason,                          inline: false },
                { name: "🛡️  By",        value: tb.moderator,                       inline: true },
                { name: "⏰  Remaining", value: `**${formatTimeLeft(tb.expires)}**`, inline: true },
                { name: "🔓  Expires",   value: `<t:${ts}:F>  ·  <t:${ts}:R>`,       inline: false },
              ), "Auto-lifted when timer expires")
          ]});
        }
        await interaction.deferReply();
        const checkOne = async (srv) => {
          try { const r = await sendRcon(`CheckBan ${playerId}`, srv); return r.toLowerCase().includes("banned") || r.toLowerCase().includes("true"); }
          catch { return false; }
        };
        const [b1, b2] = server === "both"
          ? await Promise.all([checkOne("server1"), checkOne("server2")])
          : [server === "server1" ? await checkOne("server1") : false, server === "server2" ? await checkOne("server2") : false];
        if (!b1 && !b2) {
          return interaction.editReply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("✅  No Exile Found")
              .setDescription(`${hero("This courier walks free.")}\n\`${playerId}\` has no active exile on any server.`))
          ]});
        }
        return interaction.editReply({ embeds: [
          clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("💀  Permanent Exile Active")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",   value: `\`${playerId}\``,                                                 inline: true },
              { name: "🖥️  Banned On", value: [b1 && "**Server 1**", b2 && "**Server 2**"].filter(Boolean).join("  +  "), inline: true },
            ), "Permanent exile — use /unban to lift")
        ]});
      }

      /* ─────────────────────────────────────────────────────
         BANLIST
         ───────────────────────────────────────────────────── */
      case "banlist": {
        const server = interaction.options.getString("server");
        await interaction.deferReply();
        const tempBans   = loadBans();
        const tempBanIds = new Set(tempBans.map(b => b.playerId.toLowerCase()));
        const fetchPermBans = async (srv) => {
          try {
            const r = await sendRcon("BanList", srv);
            const d = parseRcon(r);
            return (d?.BanList ?? []).filter(e => {
              const id = (typeof e === "string" ? e : e.name ?? e.username ?? e.uniqueId ?? e.id ?? "").toLowerCase();
              return id && !tempBanIds.has(id);
            });
          } catch { return []; }
        };
        let pb1 = [], pb2 = [];
        if (server === "both")         [pb1, pb2] = await Promise.all([fetchPermBans("server1"), fetchPermBans("server2")]);
        else if (server === "server2") pb2 = await fetchPermBans("server2");
        else                           pb1 = await fetchPermBans("server1");
        const extractId = e => typeof e === "string" ? e : (e.name ?? e.username ?? e.uniqueId ?? e.id ?? "");
        if (!tempBans.length && !pb1.length && !pb2.length) {
          return interaction.editReply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("✅  Exile Registry Clear")
              .setDescription('> *"The Mojave is peaceful — for now."*\n\nNo active exiles on any server.'))
          ]});
        }
        // Flatten every exile into a single tagged line so long lists paginate cleanly.
        const lines = [
          ...tempBans.map(b => `⏳  \`${b.playerId}\`  —  expires <t:${Math.floor(b.expires / 1000)}:R>  ·  *${b.reason}*`),
          ...pb1.map(b => `💀  \`${extractId(b)}\`  ·  *Permanent · S1*`),
          ...pb2.map(b => `💀  \`${extractId(b)}\`  ·  *Permanent · S2*`),
        ];
        const total = lines.length;
        const header = `> *"The Strip keeps its records."*\n\n${DIVIDER}\n**${total}** active exile${total !== 1 ? "s" : ""}  ·  ⏳ ${tempBans.length} temp  ·  💀 ${pb1.length + pb2.length} permanent`;
        return paginate(interaction, lines, (pageLines) =>
          clinical(new EmbedBuilder().setColor(CLIN.red).setTitle(`📜  Exile Registry — ${serverLabel(server)}`)
            .setDescription(`${header}\n${DIVIDER}\n${pageLines.join("\n")}`), `${total} exile${total !== 1 ? "s" : ""} active`),
          { perPage: 15 });
      }

      /* ─────────────────────────────────────────────────────
         PERMBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "permban": {
        const playerId  = sanitizeId(interaction.options.getString("playerid"));
        const server    = interaction.options.getString("server");
        const reasonKey = interaction.options.getString("reason");
        const notes     = interaction.options.getString("notes") ?? null;
        const reason    = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        const ipEnf = await banWithIp(playerId, server);
        await removeBans(playerId);   // a permanent ban supersedes any temp ban
        writeModLog({ action: "permban", playerId, reason, by: interaction.user.tag, server });
        const embed = clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("💀  Permanent Exile Issued")
          .setDescription(`> *${randomQuote("ban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",  value: `\`${playerId}\``,                                inline: true },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`, inline: true },
            { name: "⏱️  Sentence", value: "**Permanent**",                                  inline: true },
            { name: "⚖️  Offense",  value: reason,                                           inline: false },
            { name: "🔒  Admin",    value: `${interaction.user}`,                            inline: false },
          ));
        if (notes) embed.addFields({ name: "📝  Notes", value: notes });
        const pbTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId);
        const pbDm = await dmPunishmentNotice(pbTarget, {
          action: "Permanent Ban", color: NV.LEGION_RED, playerId, reason,
          fields: [
            { name: "⏱️  Sentence", value: "**Permanent**",      inline: true },
            { name: "🖥️  Server",   value: serverLabel(server),  inline: true },
          ],
        });
        const pbDmField = dmStatusField(pbDm, pbTarget);
        if (pbDmField) embed.addFields(pbDmField);
        if (ipEnf?.field) embed.addFields(ipEnf.field);
        brand(embed); await logBan(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         CLEARTEMPBANS
         ───────────────────────────────────────────────────── */
      case "cleartempbans": {
        const bans = loadBans();
        if (!bans.length) return interaction.reply({ embeds: [successEmbed("Registry Clear", "No active temporary exiles to remove.")], ephemeral: true });
        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("ctb_confirm").setLabel(`Clear all ${bans.length} temp ban${bans.length !== 1 ? "s" : ""}`).setStyle(ButtonStyle.Danger).setEmoji("🧹"),
          new ButtonBuilder().setCustomId("ctb_cancel").setLabel("Cancel").setStyle(ButtonStyle.Secondary)
        );
        const msg = await interaction.reply({
          embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("🧹  Confirm Mass Clearance")
            .setDescription(`> *"Are you sure? Every exile gets pardoned."*\n\n${DIVIDER}\n` +
              `This will lift **${bans.length}** exile${bans.length !== 1 ? "s" : ""} and unban all on both servers.\n\n` +
              bans.map(b => `·  \`${b.playerId}\`  —  *${b.reason}*`).join("\n").slice(0, 3500)), "Expires in 30 seconds")],
          components: [row], ephemeral: true, fetchReply: true,
        });
        try {
          const btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: 30_000, filter: i => i.user.id === interaction.user.id });
          if (btn.customId === "ctb_cancel") {
            return btn.update({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("🪖  Stand Down").setDescription("Clearance cancelled — all exiles remain active."))], components: [] });
          }
          await btn.deferUpdate();
          const ok = [], fail = [];
          for (const ban of bans) {
            try { await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server1"); await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server2"); ok.push(ban.playerId); }
            catch { fail.push(ban.playerId); }
          }
          await removeBans(...ok);   // drop only those actually lifted; keep failures & any concurrent additions
          writeModLog({ action: "cleartempbans", count: ok.length, by: interaction.user.tag });
          const lines = [...ok.map(id => `✅  \`${id}\``), ...fail.map(id => `☢️  \`${id}\`  — failed, kept on record`)];
          const embed = clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("🧹  Temp Bans Cleared")
            .setDescription(`> *"Clean slate."*\n\n${DIVIDER}\n**${ok.length}** released${fail.length ? `  ·  **${fail.length}** failed` : ""}\n\n${lines.join("\n")}`.slice(0, 4000))
            .addFields({ name: "🔒  By", value: `${interaction.user}`, inline: false }));
          await logBan(embed);
          return btn.editReply({ embeds: [embed], components: [] });
        } catch {
          return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("⌛  Timed Out").setDescription("Confirmation expired. No changes made."))], components: [] });
        }
      }

      /* ─────────────────────────────────────────────────────
         CLEARALLBANS — owner only: Unban every banned player
         ───────────────────────────────────────────────────── */
      case "clearallbans": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], ephemeral: true });
        await interaction.deferReply({ ephemeral: true });
        // gather every banned name: bot temp bans + the server BanList(s)
        const fetchBans = async (srv) => {
          try {
            const d = parseRcon(await sendRcon("BanList", srv, 3000, 1));
            return (d?.BanList ?? []).map(e => typeof e === "string" ? e : (e.name ?? e.username ?? e.uniqueId ?? e.id ?? "")).filter(Boolean);
          } catch { return []; }
        };
        const [b1, b2] = await Promise.all([fetchBans("server1"), process.env.RCON_HOST_2 ? fetchBans("server2") : Promise.resolve([])]);
        const names = [...new Set([...loadBans().map(b => b.playerId), ...b1, ...b2].map(s => String(s).trim()).filter(Boolean))];
        if (!names.length) {
          return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("✅  No Exiles on Record").setDescription(`${hero("The wasteland is at peace.")}\nNothing to clear — no bans on record.`))] });
        }

        // confirmation gate (irreversible)
        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("cab_confirm").setLabel(`Unban all ${names.length}`).setStyle(ButtonStyle.Danger).setEmoji("🧹"),
          new ButtonBuilder().setCustomId("cab_cancel").setLabel("Cancel").setStyle(ButtonStyle.Secondary),
        );
        const preview = names.slice(0, 30).map(n => `·  \`${n}\``).join("\n") + (names.length > 30 ? `\n…and ${names.length - 30} more` : "");
        const msg = await interaction.editReply({
          embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("🧹  Confirm — Pardon the Whole Mojave")
            .setDescription(`> *"A clean slate for the whole Mojave."*\n\n${DIVIDER}\n` +
              `Runs \`Unban\` for **${names.length}** courier(s) on both servers and lifts their IP/username flags. This cannot be undone.\n\n${preview}`.slice(0, 4000)), "Expires in 30 seconds")],
          components: [row],
        });
        let btn;
        try { btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: 30_000, filter: i => i.user.id === interaction.user.id }); }
        catch { return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("⌛  Timed Out").setDescription("Confirmation expired. No bans were lifted."))], components: [] }); }
        if (btn.customId === "cab_cancel") {
          return btn.update({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("🪖  Stand Down").setDescription("Cancelled — all bans remain in place."))], components: [] });
        }
        await btn.deferUpdate();

        let ok = 0, failed = 0;
        for (const n of names) {
          try { await sendRconBoth(`Unban ${sanitizeId(n)}`, "both"); ok++; }
          catch (e) { failed++; logger.warn("ClearAllBans", `Unban ${n} failed: ${e.message}`); }
          try { ipBans.unblacklistPlayer(n); } catch {}   // also lift IP/username flags
        }
        await removeBans(...names);   // clear the bot's temp-ban records
        writeModLog({ action: "clearallbans", count: ok, by: interaction.user.tag });
        const embed = clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("🧹  All Exiles Pardoned")
          .setDescription(`> *"A clean slate for the whole Mojave."*\n\n${DIVIDER}\nRan \`Unban\` for **${names.length}** courier(s) on both servers and lifted their IP/username flags.`)
          .addFields(
            { name: "✅  Unbanned", value: `**${ok}**${failed ? `  ·  ⚠️ ${failed} failed` : ""}`, inline: true },
            { name: "🔒  By",       value: `${interaction.user}`, inline: true },
          ));
        await logBan(embed);
        return btn.editReply({ embeds: [embed], components: [] });
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
            new EmbedBuilder().setColor(NV.AMBER).setTitle("🔑  Role Configuration")
              .setDescription(`> *Current role settings. Pass role options to update.*\n\nIf no roles are configured, all commands are unrestricted.\n\n${DIVIDER}`)
              .addFields(
                { name: "🛡️  Moderator",     value: c.modRoleId           ? `<@&${c.modRoleId}>`           : "`not set`", inline: true },
                { name: "🔒  Admin",          value: c.adminRoleId         ? `<@&${c.adminRoleId}>`         : "`not set`", inline: true },
                { name: "⚔️  Faction Leader", value: c.factionLeaderRoleId ? `<@&${c.factionLeaderRoleId}>` : "`not set`", inline: true },
              ).setFooter({ text: "Pass role options to /setroles to update" }).setTimestamp()
          ], ephemeral: true });
        }
        const c = loadRoles();
        if (modRole)   c.modRoleId           = modRole.id;
        if (adminRole) c.adminRoleId         = adminRole.id;
        if (flRole)    c.factionLeaderRoleId = flRole.id;
        saveRoles(c);
        const changes = [modRole && `🛡️  Mod → <@&${modRole.id}>`, adminRole && `🔒  Admin → <@&${adminRole.id}>`, flRole && `⚔️  Faction → <@&${flRole.id}>`].filter(Boolean);
        const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("🔑  Role Permissions Updated")
          .setDescription(changes.join("\n"))
          .addFields({ name: "🔒  By", value: `${interaction.user}`, inline: false }).setFooter({ text: "Takes effect immediately" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         ACCEPT STAFF APP  (DM the applicant + grant staff roles)
         ───────────────────────────────────────────────────── */
      case "acceptstaffapp": {
        const user = interaction.options.getUser("user");
        await interaction.deferReply({ ephemeral: true });

        const dm = new EmbedBuilder().setColor(NV.IRRAD_GREEN)
          .setTitle("Nuclear RP — Staff Application Accepted")
          .setDescription(
            `You've been **accepted to the Nuclear RP staff team.**\n\n` +
            `Your staff roles have been applied — you should see your new access in the server shortly. ` +
            `Please read through the staff guidelines and reach out to senior staff if you have any questions.\n\n` +
            `Welcome aboard.`
          )
          .setFooter({ text: "Nuclear RP Staff Team" });
        const sent = await dmEmbed(user, dm);

        // grant the staff roles
        let rolesGranted = false, roleErr = null;
        try {
          const member = await interaction.guild.members.fetch(user.id);
          await member.roles.add(STAFF_ROLE_IDS, `Staff application accepted by ${interaction.user.tag}`);
          rolesGranted = true;
        } catch (err) {
          roleErr = err.message;
          logger.warn("StaffApp", `Role grant failed for ${user.id}: ${err.message}`);
        }

        // public welcome announcement
        let announced = false, announceErr = null;
        try {
          const ch = await client.channels.fetch(STAFF_ANNOUNCE_CHANNEL);
          if (ch?.isTextBased()) {
            const welcome = brand(new EmbedBuilder().setColor(NV.AMBER)
              .setTitle("New Staff Member")
              .setDescription(`<@${user.id}> has joined the **Nuclear RP staff team.** Welcome to the team.`)
              .setFooter({ text: "Nuclear RP Staff Team" }));
            await ch.send({ content: `<@${user.id}>`, embeds: [welcome] });
            announced = true;
          } else {
            announceErr = "channel is not text-based";
          }
        } catch (err) {
          announceErr = err.message;
          logger.warn("StaffApp", `Announce failed: ${err.message}`);
        }

        writeModLog({ action: "staffapp-accept", targetUserId: user.id, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(rolesGranted ? NV.IRRAD_GREEN : NV.NCR_TAN)
          .setTitle("✅  Staff Application Accepted")
          .setDescription(RULE)
          .addFields(
            { name: "🎯  Applicant", value: `<@${user.id}>  \`${user.id}\``, inline: false },
            { name: "📨  DM",        value: sent ? "✅  Acceptance DM delivered" : "⚠️  Couldn't DM (DMs closed / bot blocked)", inline: false },
            { name: "🎖️  Roles",     value: rolesGranted ? `✅  Granted <@&${STAFF_ROLE_IDS[0]}> & <@&${STAFF_ROLE_IDS[1]}>` : `⚠️  Could not grant roles — ${roleErr || "check the bot's role position & Manage Roles permission"}`, inline: false },
            { name: "📢  Announced",  value: announced ? `✅  Posted in <#${STAFF_ANNOUNCE_CHANNEL}>` : `⚠️  Couldn't post announcement — ${announceErr || "check the channel ID & bot permissions"}`, inline: false },
            { name: "🔒  By",        value: `${interaction.user}`, inline: false },
          );
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         DENY STAFF APP  (DM the applicant — nothing else)
         ───────────────────────────────────────────────────── */
      case "denystaffapp": {
        const user   = interaction.options.getUser("user");
        const reason = interaction.options.getString("reason");
        await interaction.deferReply({ ephemeral: true });

        const dm = new EmbedBuilder().setColor(NV.NCR_TAN)
          .setTitle("Nuclear RP — Staff Application Update")
          .setDescription(
            `Thanks for applying to the Nuclear RP staff team.\n\n` +
            `After review, we've decided **not to accept your application at this time.** ` +
            `You're welcome to apply again when applications reopen.\n\n` +
            `Thanks for your interest.`
          )
          .setFooter({ text: "Nuclear RP Staff Team" });
        if (reason) dm.addFields({ name: "📋  Note from the team", value: reason });
        const sent = await dmEmbed(user, dm);

        // Deny does nothing else — no roles, no logging beyond this reply.
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN)
          .setTitle("📪  Staff Application Denied")
          .setDescription(RULE)
          .addFields(
            { name: "🎯  Applicant", value: `<@${user.id}>  \`${user.id}\``, inline: false },
            { name: "📨  DM",        value: sent ? "✅  Denial DM delivered" : "⚠️  Couldn't DM (DMs closed / bot blocked)", inline: false },
            { name: "🔒  By",        value: `${interaction.user}`, inline: false },
          );
        brand(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         DONATOR  (admin — manage the donator whitelist file)
         ───────────────────────────────────────────────────── */
      case "donator": {
        const sub = interaction.options.getSubcommand();

        if (sub === "list") {
          const lines = readDonatorFile();
          if (lines === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Could not read the donator file.\n\`${DONATOR_FILE}\``)], ephemeral: true });
          }
          if (!lines.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("💎  Donator List — Empty")
                .setDescription("No players are in the donator file yet.\n\nUse `/donator add` to enrol someone.").setTimestamp()
            ], ephemeral: true });
          }
          const out = lines.map((id, i) => `\`${String(i + 1).padStart(2, "0")}\`  **${id}**`);
          return paginate(interaction, out, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`💎  Donators — ${lines.length}`)
              .setDescription(`> *"The House remembers its most generous patrons."*\n\n${DIVIDER}\n${pageLines.join("\n")}`)
              .setFooter({ text: DONATOR_FILE }),
            { perPage: 20, ephemeral: true });
        }

        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });

        if (sub === "add") {
          const { ok, already } = addDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], ephemeral: true });
          if (already) return interaction.reply({ embeds: [warningEmbed("Already a Donator", `\`${playerId}\` is already in the donator file.`)], ephemeral: true });
          writeModLog({ action: "donator-add", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💎  Donator Added")
            .setDescription(`> *"A generous soul joins the ranks of the Strip's patrons."*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier", value: `\`${playerId}\``,        inline: true },
              { name: "🔒  Added By", value: `${interaction.user}`,   inline: true },
              { name: "📄  File",     value: `\`${DONATOR_FILE}\``,   inline: false },
            ).setFooter({ text: "Written to the donator file." }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        if (sub === "remove") {
          const { ok, missing } = removeDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], ephemeral: true });
          if (missing) return interaction.reply({ embeds: [warningEmbed("Not a Donator", `\`${playerId}\` is not in the donator file.`)], ephemeral: true });
          writeModLog({ action: "donator-remove", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("💎  Donator Removed")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",   value: `\`${playerId}\``,      inline: true },
              { name: "🔒  Removed By", value: `${interaction.user}`, inline: true },
            ).setFooter({ text: "Removed from the donator file." }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
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
        if (!message.trim()) return interaction.reply({ embeds: [errorEmbed("Empty Message", "Cannot broadcast an empty message.")], ephemeral: true });
        const isAll  = !rawTarget || rawTarget.trim().toLowerCase() === "all";
        const target = isAll ? "All" : sanitizeId(rawTarget);
        if (!target) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();
        const { s1, s2 } = await sendRconBoth(`Notify ${target} ${message}`, server);
        // Pavlov RCON has no broadcast verb on stock builds; Notify is build/mod-dependent.
        // Heuristically detect whether the server acknowledged the command.
        const ackOne = (raw) => {
          if (raw == null) return null; // server not targeted
          const lower = raw.toLowerCase();
          if (!raw.trim()) return false; // silent — likely unrecognised
          // Treat explicit failure markers as a miss, anything else as accepted
          return !(lower.includes("unknown") || lower.includes("not recognized") ||
                   lower.includes("not recognised") || lower.includes("invalid") ||
                   lower.includes("\"successful\":false"));
        };
        const a1 = ackOne(s1);
        const a2 = ackOne(s2);
        const acks = [a1, a2].filter(v => v !== null);
        const allOk  = acks.length > 0 && acks.every(Boolean);
        const anyOk  = acks.some(Boolean);
        writeModLog({ action: "announce", message, target, by: interaction.user.tag, server, delivered: allOk });
        const deliveryNote = allOk
          ? "✅  Sent via RCON `Notify` — visible in-game if your build supports it."
          : anyOk
            ? "⚠️  One server may not support `Notify`. Message logged here regardless."
            : "⚠️  Server gave no acknowledgement — your Pavlov build may not support `Notify`. Message logged here only.";
        const embed = new EmbedBuilder().setColor(allOk ? NV.BLUE_VATS : NV.NCR_TAN).setTitle("📢  Broadcast Sent")
          .setDescription(`> *${randomQuote("announce")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "📣  Message",  value: `> ${message}`,                                     inline: false },
            { name: "🎯  Target",   value: isAll ? "**All players**" : `\`${target}\``,         inline: true },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "🛡️  By",       value: `${interaction.user}`,                              inline: true },
            { name: "📡  Delivery", value: deliveryNote,                                       inline: false },
          ).setFooter({ text: "RCON Notify broadcast" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         GIVEMENU / STRIPMENU  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "givemenu":
      case "stripmenu": {
        const playerId  = sanitizeId(interaction.options.getString("playerid"));
        const server    = interaction.options.getString("server");
        const menuValue = interaction.options.getString("menu");
        const menuMeta  = MENUS.find(m => m.value === menuValue);
        const menuId    = menuMeta?.menuId ?? menuValue;
        const isGive    = name === "givemenu";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                         // ← ADDED
        if (isGive && menuValue === "highstaff") {
          // High Staff needs three distinct RCON commands — run each separately.
          await sendRconBoth(`AddMod ${playerId}`, server);
          await sendRconBoth(`AddAccessManager ${playerId}`, server);
          await sendRconBoth(`GiveMenu ${playerId} ${menuId}`, server);
        } else {
          await sendRconBoth(`${isGive ? "GiveMenu" : "RemoveMenu"} ${playerId} ${menuId}`, server);
        }
        if (isGive) {
          addMenuGrant(playerId, server, menuValue, menuId, interaction.user.tag);
        } else {
          if (server === "both") {
            removeMenuGrant(playerId, "server1", menuValue);
            removeMenuGrant(playerId, "server2", menuValue);
          }
          removeMenuGrant(playerId, server, menuValue);
        }
        const embed = new EmbedBuilder().setColor(isGive ? NV.AMBER : NV.NCR_TAN)
          .setTitle(isGive ? "🎛️  Menu Access Granted" : "🗑️  Menu Access Revoked")
          .setDescription(`${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier", value: `\`${playerId}\``,                                  inline: true },
            { name: "🖥️  Server",  value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "📋  Menu",    value: menuMeta?.name ?? menuValue,                        inline: true },
            { name: isGive ? "🔒  Granted By" : "🔒  Revoked By", value: `${interaction.user}`, inline: false },
            { name: isGive ? "♻️  Persistence" : "🗑️  Persistence", value: isGive ? "✅  Will be re-applied automatically on rejoin." : "✅  Removed from persistent store — will not reapply.", inline: false },
          ).setTimestamp();
        // High Staff: the bot ran all three commands automatically (each separately).
        if (isGive && menuValue === "highstaff") {
          embed.addFields({ name: "⚙️  Auto-applied (each run separately)", value: `\`\`\`\nAddMod ${playerId}\nAddAccessManager ${playerId}\nGiveMenu ${playerId} <menu bitmask>\n\`\`\`` , inline: false });
        }
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });     // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         STRIPMENUALL — owner only: revoke one menu from everyone
         ───────────────────────────────────────────────────── */
      case "stripmenuall": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], ephemeral: true });
        const menuValue = interaction.options.getString("menu");
        const menuMeta  = MENUS.find(m => m.value === menuValue);
        const menuId    = menuMeta?.menuId ?? menuValue;
        await interaction.deferReply();

        const grants  = loadMenuGrants();
        const holders = Object.keys(grants).filter(pid => (grants[pid] || []).some(g => g.menuValue === menuValue));
        let ok = 0, failed = 0;
        for (const pid of holders) {
          try { await sendRconBoth(`RemoveMenu ${pid} ${menuId}`, "both"); ok++; }
          catch (err) { failed++; logger.warn("StripAll", `RemoveMenu failed for ${pid}: ${err.message}`); }
          removeMenuGrant(pid, "server1", menuValue);
          removeMenuGrant(pid, "server2", menuValue);
        }

        const embed = brand(new EmbedBuilder().setColor(NV.LEGION_RED)
          .setTitle("🧹  Mass Menu Revocation")
          .setDescription(hero(`Pulled **${menuMeta?.name ?? menuValue}** access from every courier who held it.`))
          .addFields(
            { name: "📋  Menu",     value: menuMeta?.name ?? menuValue,                         inline: true },
            { name: "👥  Holders",  value: `**${holders.length}**`,                              inline: true },
            { name: "🖥️  Server",   value: "Both servers",                                       inline: true },
            { name: "✅  Revoked",  value: `**${ok}** RCON ${ok === 1 ? "call" : "calls"} sent${failed ? ` · ⚠️ ${failed} failed` : ""}`, inline: false },
          ).setTimestamp());
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         CONFIGURE — owner only: hidden control panel (dropdown).
         Non-owners never see the menu — just the missing-perms reply.
         ───────────────────────────────────────────────────── */
      case "configure": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], ephemeral: true });

        const menu = new StringSelectMenuBuilder().setCustomId("cfg_menu").setPlaceholder("Select a hidden command…")
          .addOptions(
            { label: "Blacklist IP / username", value: "blacklist_ip", description: "Auto-ban anyone matching an IP or username", emoji: "🚫" },
            { label: "View blacklist",          value: "view_blacklist", description: "Show all blacklisted IPs and usernames", emoji: "📜" },
            { label: "View alt accounts",       value: "view_alts",      description: "A courier's known alt accounts (shared IP)", emoji: "🔗" },
            { label: "Bar a Discord user",     value: "user_bl_add",    description: "Block a Discord user from ALL bot commands", emoji: "⛔" },
            { label: "Un-bar a Discord user",  value: "user_bl_remove", description: "Restore a Discord user's command access", emoji: "✅" },
            { label: "List barred Discord users", value: "user_bl_list", description: "Show Discord users barred from commands", emoji: "📵" },
            { label: "View verification links", value: "verify_list",  description: "Show Pavlov name -> Discord links", emoji: "🎫" },
            { label: "Clear a verification link", value: "verify_clear", description: "Free up a Pavlov name / re-verify a user", emoji: "🧾" },
            { label: "Ignore a username",      value: "ignore_add",    description: "Stop tracking a player's IPs",        emoji: "🙈" },
            { label: "Un-ignore a username",   value: "ignore_remove", description: "Resume tracking a player",            emoji: "👁️" },
            { label: "List ignored usernames", value: "ignore_list",   description: "Show the ignore list",                emoji: "📋" },
            { label: "Clear all flagged IPs",  value: "clear_flags",   description: "Stop every IP auto-ban (keep history)", emoji: "🧹" },
            { label: "Clear a specific IP",    value: "clear_ip",      description: "Un-flag + remove one IP",             emoji: "🌐" },
            { label: "Wipe ALL IP data",       value: "clear_all",     description: "Full registry + flag reset",          emoji: "💥" },
          );
        const panel = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("⚙️  Configure — Hidden Commands"));
        await interaction.reply({ embeds: [panel], components: [new ActionRowBuilder().addComponents(menu)], ephemeral: true });
        const msg = await interaction.fetchReply();

        let sel;
        try { sel = await msg.awaitMessageComponent({ componentType: ComponentType.StringSelect, time: 60_000, filter: i => i.user.id === interaction.user.id }); }
        catch { return interaction.editReply({ components: [] }).catch(() => {}); }
        const choice = sel.values[0];

        // actions that need text input -> open a modal
        if (["ignore_add", "ignore_remove", "clear_ip", "blacklist_ip", "view_alts", "user_bl_add", "user_bl_remove", "verify_clear"].includes(choice)) {
          const titleByChoice = { ignore_add: "Ignore a username", ignore_remove: "Un-ignore a username", clear_ip: "Clear a specific IP", blacklist_ip: "Blacklist IP / username", view_alts: "View alt accounts", user_bl_add: "Bar a Discord user", user_bl_remove: "Un-bar a Discord user", verify_clear: "Clear a verification link" };
          const labelByChoice = { ignore_add: "Username", ignore_remove: "Username", clear_ip: "IP address", blacklist_ip: "IP or username", view_alts: "Courier username", user_bl_add: "Discord user ID", user_bl_remove: "Discord user ID", verify_clear: "Pavlov username" };
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
              .setTitle(`🔗  Alt Accounts — ${val}`)
              .addFields({ name: `Linked accounts (${alts.length})`, value: list, inline: false })
              .setFooter({ text: "Alt links come from confirmed shared IPs" }).setTimestamp());
            return sub.reply({ embeds: [eAlt], ephemeral: true });
          }
          if (choice === "blacklist_ip") {
            await sub.deferReply({ ephemeral: true });
            const r = ipBans.flagTarget(val);            // IPv4 detected by shape; else a username
            for (const id of r.ids) { const nm = ipBans.registry[id]?.name; if (nm) { try { await banWithIp(nm, "both"); } catch {} } }   // ban matching accounts now (by name)
            color = NV.LEGION_RED;
            desc = `🚫 ${r.kind} \`${r.value}\` blacklisted — any account matching it is auto-banned.` +
              (r.ids.length ? `\nBanned **${r.ids.length}** account(s) already on record.` : `\nNo accounts on record yet — future connections will be caught.`);
            const e1 = brand(new EmbedBuilder().setColor(color).setTitle("⚙️  Blacklisted").setDescription(hero(desc)).setTimestamp());
            await logAction(e1);
            return sub.editReply({ embeds: [e1] });
          }
          if (choice === "user_bl_add")        { const uid = val.replace(/\D/g, ""); const added = uid && addUserBlacklist(uid); color = NV.LEGION_RED; desc = added ? `⛔ <@${uid}> (\`${uid}\`) is barred from ALL bot commands.` : `\`${uid || val}\` was already barred or isn't a valid ID.`; }
          else if (choice === "user_bl_remove") { const uid = val.replace(/\D/g, ""); const removed = uid && removeUserBlacklist(uid); desc = removed ? `✅ <@${uid}> (\`${uid}\`) can use commands again.` : `\`${uid || val}\` wasn't on the barred list.`; }
          else if (choice === "ignore_add")    { const r = ipBans.addUntracked(val); desc = `🙈 **${val}** will no longer be tracked. Purged **${r.purged}** record(s). (No IP logging, feed, or auto-ban for this name.)`; }
          else if (choice === "ignore_remove") { const ok2 = ipBans.removeUntracked(val); desc = ok2 ? `👁️ **${val}** is tracked again from their next connection.` : `**${val}** wasn't on the ignore list.`; }
          else if (choice === "verify_clear")  {
            const links = loadVerifyLinks(); const key = val.toLowerCase(); const had = links[key];
            if (had) { delete links[key]; safeWrite(FILES.VERIFY_LINKS, links); }
            desc = had ? `🧾 Verification link for \`${val}\` (<@${had}>) cleared — the name is free and they can re-verify.` : `No verification link found for \`${val}\`.`;
          }
          else                               { const r = ipBans.clearIp(val); desc = `🧹 \`${val}\` — ${r.flagRemoved ? "un-flagged" : "was not flagged"}, removed from **${r.players}** record(s).`; }
          const e = brand(new EmbedBuilder().setColor(color).setTitle("⚙️  Done").setDescription(hero(desc)).setTimestamp());
          await logAction(e);
          return sub.reply({ embeds: [e], ephemeral: true });
        }

        // view the blacklist (IPs / usernames)
        if (choice === "view_blacklist") {
          const b = ipBans.getBlacklist();
          const fmt = (a) => a.length ? a.map(x => `\`${x}\``).join("  ·  ").slice(0, 1024) : "*none*";
          const e = brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("📜  Blacklist")
            .addFields(
              { name: `🌐  IPs (${b.ips.length})`,        value: fmt(b.ips),   inline: false },
              { name: `🎯  Usernames (${b.names.length})`, value: fmt(b.names), inline: false },
            ).setTimestamp());
          return sel.update({ embeds: [e], components: [] });
        }

        // direct actions (no input)
        let desc, color = NV.AMBER, audit = true;
        if (choice === "ignore_list")      { const n = ipBans.getUntracked(); desc = n.length ? n.map(x => `• \`${x}\``).join("\n").slice(0, 4000) : "No usernames are ignored — everyone is tracked."; audit = false; }
        else if (choice === "user_bl_list") { const ids = [...BLACKLIST_IDS]; desc = ids.length ? ids.map(x => `• <@${x}> \`${x}\``).join("\n").slice(0, 4000) : "No Discord users are barred from commands."; audit = false; }
        else if (choice === "verify_list")  { const lk = loadVerifyLinks(); const es = Object.entries(lk); desc = es.length ? es.map(([n, id]) => `• \`${n}\` → <@${id}>`).join("\n").slice(0, 4000) : "No verified couriers yet."; audit = false; }
        else if (choice === "clear_flags") { const n = ipBans.clearFlags(); color = NV.LEGION_RED; desc = `🧹 Removed **${n}** flagged IP${n !== 1 ? "s" : ""}. No IP auto-bans until new bans flag IPs again. (History kept.)`; }
        else if (choice === "clear_all")   { const r = ipBans.clearAll(); color = NV.LEGION_RED; desc = `💥 Wiped **${r.ids}** player record(s) and **${r.flagged}** flagged IP${r.flagged !== 1 ? "s" : ""}. Rebuilds from the logs as players connect.`; }
        const e = brand(new EmbedBuilder().setColor(color).setTitle("⚙️  Configure").setDescription(hero(desc)).setTimestamp());
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
            return interaction.reply({ embeds: [adminOnlyEmbed()], ephemeral: true });
          }
          const faction = interaction.options.getString("faction");
          const cap     = interaction.options.getInteger("cap");
          await setFactionCap(faction, cap);
          writeModLog({ action: "faction-setcap", faction, cap, by: interaction.user.tag });
          const spawn   = SPAWN_FILE_MAP[faction];
          const current = spawn ? (readFactionFile(spawn)?.length ?? 0) : 0;
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("⚙️  Faction Size Cap Updated")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "⚔️  Faction",      value: faction,                                                       inline: true },
              { name: "📏  New Cap",       value: `**${cap}** members`,                                          inline: true },
              { name: "👥  Current Size",  value: `${current} / ${cap}${current > cap ? "  ⚠️  over cap!" : ""}`, inline: true },
              { name: "🔒  Set By",        value: `${interaction.user}`,                                          inline: false },
            ).setFooter({ text: "Cap enforced on /faction add" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        /* ── setrankcap (admin only) ── */
        if (sub === "setrankcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], ephemeral: true });
          }
          const faction = interaction.options.getString("faction");
          const rank    = interaction.options.getString("rank");
          const cap     = interaction.options.getInteger("cap");
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], ephemeral: true });
          }
          await setFactionRankCap(faction, rank, cap);
          writeModLog({ action: "faction-setrankcap", faction, rank, cap, by: interaction.user.tag });
          const current = countFactionRank(faction, rank);
          const capStr  = cap > 0 ? `**${cap}**` : "**Unlimited**";
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("⚙️  Rank Cap Updated")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "⚔️  Faction",     value: faction,                                                              inline: true },
              { name: "🎖️  Rank",        value: rankBadge(faction, rank),                                             inline: true },
              { name: "📏  New Cap",      value: capStr,                                                               inline: true },
              { name: "👥  Currently",    value: `${current}${cap > 0 ? ` / ${cap}${current > cap ? "  ⚠️  over cap!" : ""}` : ""}`, inline: true },
              { name: "🔒  Set By",       value: `${interaction.user}`,                                                inline: false },
            ).setFooter({ text: cap > 0 ? "Cap enforced on add / rank / transfer" : "Rank is now uncapped" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        /* ── overview (public) ── */
        if (sub === "overview") {
          const fields = [];
          for (const faction of ALL_FACTIONS) {
            const members = getFactionMembers(faction);
            if (members === null) {
              fields.push({ name: `⚔️  ${faction}`, value: "⚠️  Spawn file unreadable", inline: true });
              continue;
            }
            const cap    = getFactionCap(faction);
            const total  = members.length;
            const cfg    = getFactionRankConfig(faction);
            const topRanks = cfg ? cfg.order.slice(-2) : [];
            const topStr   = members
              .filter(m => topRanks.includes(m.rank))
              .slice(0, 3)
              .map(m => `${getFactionRankBadge(faction, m.rank)}  ${m.playerId}`)
              .join("\n") || "*No senior members*";
            const barFull = Math.round((total / cap) * 10);
            const bar     = "█".repeat(Math.min(barFull, 10)) + "░".repeat(Math.max(10 - barFull, 0));
            const rankCaps = getFactionRankCaps(faction);
            const cappedStr = Object.keys(rankCaps).length
              ? "\n" + (cfg ? cfg.order : Object.keys(rankCaps))
                  .filter(r => rankCaps[r] > 0)
                  .map(r => {
                    const c = countFactionRank(faction, r);
                    return `${getFactionRankBadge(faction, r)} ${c}/${rankCaps[r]}${c > rankCaps[r] ? "⚠️" : ""}`;
                  }).join("  ·  ")
              : "";
            fields.push({
              name:  `⚔️  ${faction}`,
              value: `\`${bar}\`  **${total}/${cap}**\n${topStr}${cappedStr}`,
              inline: true,
            });
          }
          const rankSummary = ALL_FACTIONS.map(f => {
            const cfg = getFactionRankConfig(f);
            return cfg ? `**${f}:** ${cfg.order.map(r => `${cfg.badges[r]}${r}`).join(" → ")}` : null;
          }).filter(Boolean).join("\n");
          const embed = new EmbedBuilder().setColor(NV.GOLD)
            .setTitle("⚔️  Faction Overview — Mojave Authority")
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(...fields)
            .addFields({ name: "⚔️  Rank Ladders", value: rankSummary, inline: false })
            .setTimestamp();
          return interaction.reply({ embeds: [embed] });
        }

        /* ── list (public, paginated) ── */
        if (sub === "list") {
          const faction  = interaction.options.getString("faction");
          const members  = getFactionMembers(faction);
          if (members === null) {
            return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**. Check the server path.`)], ephemeral: true });
          }
          if (!members.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`⚔️  ${faction} — Empty Roster`)
                .setDescription("No players are currently whitelisted for this faction.\n\nUse `/faction add` to enlist someone.")
                .setTimestamp()
            ], ephemeral: true });
          }
          const cap = getFactionCap(faction);
          const summary = getFactionRankOrder(faction).slice().reverse().map(r => {
            const n = members.filter(m => m.rank === r).length;
            const rcap = getFactionRankCap(faction, r);
            if (!n && !rcap) return null;
            const count = rcap ? `${n}/${rcap}${n > rcap ? "⚠️" : ""}` : `${n}`;
            return `${getFactionRankBadge(faction, r)} ${r}: **${count}**`;
          }).filter(Boolean).join("  ·  ");
          const lines = members.map((m, i) =>
            `\`${String(i + 1).padStart(2, "0")}\`  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  ·  *${m.rank}*`);
          const header = `**${members.length}/${cap}** members${members.length > cap ? " ⚠️ over cap" : ""}  ·  ${summary}`;
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.GOLD)
              .setTitle(`⚔️  ${faction} — Roster`)
              .setDescription(`${header}\n\n${DIVIDER}\n${pageLines.join("\n")}`)
              .setFooter({ text: SPAWN_FILE_MAP[faction] }),
            { perPage: 20 });
        }

        /* ── audit (public, paginated) ── */
        if (sub === "audit") {
          const faction   = interaction.options.getString("faction");
          const allAudit  = loadFactionAudit().filter(e => e.faction === faction).reverse();
          if (!allAudit.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`📋  ${faction} — Audit Log`)
                .setDescription("No faction changes recorded yet for this faction.")
                .setTimestamp()
            ], ephemeral: true });
          }
          const ACTION_ICONS = { "add": "➕", "remove": "➖", "rank": "🎖️", "transfer-in": "📥", "transfer-out": "📤" };
          const lines = allAudit.map(e => {
            const ts     = Math.floor(e.at / 1000);
            const icon   = ACTION_ICONS[e.action] ?? "📌";
            const detail = e.rank ? ` → **${e.rank}**` : e.oldRank ? ` *(was ${e.oldRank})*` : "";
            return `${icon}  \`${e.action}\`  **${e.playerId}**${detail}  ·  by *${e.by}*  ·  <t:${ts}:R>`;
          });
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.AMBER)
              .setTitle(`📋  ${faction} — Audit Log`)
              .setDescription(`**${allAudit.length}** total changes *(newest first)*\n\n${DIVIDER}\n${pageLines.join("\n")}`),
            { perPage: 15, ephemeral: true });
        }

        /* ── rank (Faction Leader ONLY) ── */
        if (sub === "rank") {
          if (!hasFactionLeaderRole(interaction.member)) {
            return interaction.reply({ embeds: [factionLeaderStrictEmbed()], ephemeral: true });
          }
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const rank     = interaction.options.getString("rank");
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], ephemeral: true });
          }
          const spawn = SPAWN_FILE_MAP[faction];
          const lines = readFactionFile(spawn);
          if (!lines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${faction}**.`)], ephemeral: true });
          if (!lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Not a Member", `\`${playerId}\` is not whitelisted in **${faction}**.\n\nUse \`/faction add\` first.`)], ephemeral: true });
          }
          const oldRank = getFactionRank(faction, playerId);
          if (oldRank !== rank) {
            const room = rankHasRoom(faction, rank);
            if (!room.ok) {
              return interaction.reply({ embeds: [errorEmbed("Rank Full",
                `**${rank}** in **${faction}** is at its cap (**${room.count}/${room.cap}**).\n\nPromote someone out first, or raise the cap with \`/faction setrankcap\`.`)], ephemeral: true });
            }
            removePlayerFromRankFile(faction, playerId, oldRank);
            addPlayerToRankFile(faction, playerId, rank);
          }
          await setFactionRank(faction, playerId, rank);
          writeFactionAudit({ action: "rank", faction, playerId, rank, oldRank, by: interaction.user.tag });
          writeModLog({ action: "faction-rank", playerId, faction, rank, oldRank, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("🎖️  Faction Rank Updated")
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",     value: `\`${playerId}\``,                inline: true },
              { name: "⚔️  Faction",     value: faction,                          inline: true },
              { name: "⬆️  New Rank",    value: rankBadge(faction, rank),         inline: true },
              { name: "⬇️  Old Rank",    value: rankBadge(faction, oldRank),      inline: true },
              { name: "⚔️  Assigned By", value: `${interaction.user}`,            inline: true },
              { name: "📁  Rank Files",  value: `Removed from \`${getFactionRankConfig(faction)?.rankFiles[oldRank] ?? "n/a"}\`\nAdded to \`${getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a"}\``, inline: false },
            ).setFooter({ text: "Rank change logged · rank files updated on disk" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── transfer (Mod+) ── */
        if (sub === "transfer") {
          if (!hasModRole(interaction.member)) {
            return interaction.reply({ embeds: [modOnlyEmbed()], ephemeral: true });
          }
          const playerId    = sanitizeId(interaction.options.getString("playerid"));
          const fromFaction = interaction.options.getString("from_faction");
          const toFaction   = interaction.options.getString("to_faction");
          const rawRank     = interaction.options.getString("rank");
          const newRank     = rawRank ?? getFactionDefaultRank(toFaction);
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
          if (fromFaction === toFaction) {
            return interaction.reply({ embeds: [errorEmbed("Same Faction", "Source and destination factions must be different.")], ephemeral: true });
          }
          const toValidRanks = getFactionRankOrder(toFaction);
          if (!toValidRanks.includes(newRank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${newRank}** is not a valid rank for **${toFaction}**.\n\nValid ranks: ${toValidRanks.map(r => `**${r}**`).join(", ")}`)], ephemeral: true });
          }
          const fromSpawn = SPAWN_FILE_MAP[fromFaction];
          const fromLines = readFactionFile(fromSpawn);
          if (!fromLines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read spawn file for **${fromFaction}**.`)], ephemeral: true });
          if (!fromLines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Not a Member", `\`${playerId}\` is not whitelisted in **${fromFaction}**.`)], ephemeral: true });
          }
          const toSpawn = SPAWN_FILE_MAP[toFaction];
          const toLines = readFactionFile(toSpawn) ?? [];
          const toCap   = getFactionCap(toFaction);
          if (toLines.length >= toCap) {
            return interaction.reply({ embeds: [errorEmbed("Faction Full", `**${toFaction}** is at capacity (**${toLines.length}/${toCap}** members).\n\nIncrease the cap with \`/faction setcap\` or remove a member first.`)], ephemeral: true });
          }
          if (toLines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            return interaction.reply({ embeds: [warningEmbed("Already a Member", `\`${playerId}\` is already whitelisted in **${toFaction}**.`)], ephemeral: true });
          }
          const toRoom = rankHasRoom(toFaction, newRank);
          if (!toRoom.ok) {
            return interaction.reply({ embeds: [errorEmbed("Rank Full",
              `**${newRank}** in **${toFaction}** is at its cap (**${toRoom.count}/${toRoom.cap}**).\n\nChoose a different rank, or raise the cap with \`/faction setrankcap\`.`)], ephemeral: true });
          }
          const oldRank = getFactionRank(fromFaction, playerId);
          const updatedFrom = fromLines.filter(l => l.toLowerCase() !== playerId.toLowerCase());
          if (!writeFactionFile(fromSpawn, updatedFrom)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${fromSpawn}\`. Check file permissions.`)], ephemeral: true });
          }
          removePlayerFromAllRankFiles(fromFaction, playerId);
          await removeFactionRank(fromFaction, playerId);
          toLines.push(playerId);
          if (!writeFactionFile(toSpawn, toLines)) {
            fromLines.push(playerId); writeFactionFile(fromSpawn, fromLines);
            addPlayerToRankFile(fromFaction, playerId, oldRank);
            await setFactionRank(fromFaction, playerId, oldRank);
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${toSpawn}\`. Transfer rolled back.`)], ephemeral: true });
          }
          addPlayerToRankFile(toFaction, playerId, newRank);
          await setFactionRank(toFaction, playerId, newRank);
          writeFactionAudit({ action: "transfer-out", faction: fromFaction, playerId, oldRank, by: interaction.user.tag });
          writeFactionAudit({ action: "transfer-in",  faction: toFaction,   playerId, rank: newRank, by: interaction.user.tag });
          writeModLog({ action: "faction-transfer", playerId, fromFaction, toFaction, oldRank, newRank, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("↔️  Faction Transfer Complete")
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",       value: `\`${playerId}\``,                                          inline: true },
              { name: "📤  From",           value: `**${fromFaction}**  *(${rankBadge(fromFaction, oldRank)})*`, inline: true },
              { name: "📥  To",             value: `**${toFaction}**  *(${rankBadge(toFaction, newRank)})*`,   inline: true },
              { name: "👥  New Roster Size",value: `${toLines.length} / ${toCap}`,                            inline: true },
              { name: "🛡️  Transferred By", value: `${interaction.user}`,                                     inline: true },
              { name: "📁  Rank Files",     value: `Cleared from **${fromFaction}** rank files\nAdded to \`${getFactionRankConfig(toFaction)?.rankFiles[newRank] ?? "n/a"}\``, inline: false },
            ).setFooter({ text: "Both faction files updated · rank files updated on disk · audit logged" }).setTimestamp();
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
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Faction", `Faction \`${faction}\` has no configured spawn file.`)], ephemeral: true });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
          const validRanks = getFactionRankOrder(faction);
          if (!validRanks.includes(rank)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Rank",
              `**${rank}** is not a valid rank for **${faction}**.\n\nValid ranks: ${validRanks.map(r => `**${r}**`).join(", ")}`)], ephemeral: true });
          }
          const lines = readFactionFile(spawn) ?? [];
          if (lines.some(l => l.toLowerCase() === playerId.toLowerCase())) {
            const existingRank = getFactionRank(faction, playerId);
            return interaction.reply({ embeds: [warningEmbed("Already Whitelisted", `\`${playerId}\` is already in **${faction}** as ${rankBadge(faction, existingRank)}.\n\nUse \`/faction rank\` to change their rank.`)], ephemeral: true });
          }
          const cap = getFactionCap(faction);
          if (lines.length >= cap) {
            return interaction.reply({ embeds: [errorEmbed("Faction Full", `**${faction}** is at capacity (**${lines.length}/${cap}** members).\n\nUse \`/faction setcap\` to increase the limit, or remove a member first.`)], ephemeral: true });
          }
          const addRoom = rankHasRoom(faction, rank);
          if (!addRoom.ok) {
            return interaction.reply({ embeds: [errorEmbed("Rank Full",
              `**${rank}** in **${faction}** is at its cap (**${addRoom.count}/${addRoom.cap}**).\n\nAdd them at a different rank, or raise the cap with \`/faction setrankcap\`.`)], ephemeral: true });
          }
          lines.push(playerId);
          if (!writeFactionFile(spawn, lines)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to \`${spawn}\`. Check file permissions.`)], ephemeral: true });
          }
          if (!addPlayerToRankFile(faction, playerId, rank)) {
            writeFactionFile(spawn, lines.filter(l => l.toLowerCase() !== playerId.toLowerCase()));
            return interaction.reply({ embeds: [errorEmbed("Rank File Write Failed", `Could not write to rank file for **${rank}**. Check file permissions.`)], ephemeral: true });
          }
          await setFactionRank(faction, playerId, rank);
          writeFactionAudit({ action: "add", faction, playerId, rank, by: interaction.user.tag });
          writeModLog({ action: "faction-add", playerId, faction, rank, by: interaction.user.tag });
          const rankFile = getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a";
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(`⚔️  Added to ${faction}`)
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",       value: `\`${playerId}\``,            inline: true },
              { name: "⚔️  Faction",       value: faction,                      inline: true },
              { name: "🎖️  Starting Rank", value: rankBadge(faction, rank),     inline: true },
              { name: "👥  Roster Size",    value: `${lines.length} / ${cap}`,  inline: true },
              { name: "🔒  Added By",       value: `${interaction.user}`,       inline: true },
              { name: "📁  Rank File",      value: `\`${rankFile}\``,           inline: true },
            ).setFooter({ text: "Main spawn file + rank file updated · audit logged" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove ── */
        if (sub === "remove") {
          const playerId = sanitizeId(interaction.options.getString("playerid"));
          const faction  = interaction.options.getString("faction");
          const spawn    = SPAWN_FILE_MAP[faction];
          if (!spawn) return interaction.reply({ embeds: [errorEmbed("Unknown Faction", `Faction \`${faction}\` has no configured spawn file.`)], ephemeral: true });
          if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
          const lines = readFactionFile(spawn);
          if (!lines) return interaction.reply({ embeds: [errorEmbed("File Unreadable", `Cannot read \`${spawn}\`.`)], ephemeral: true });
          const idx = lines.findIndex(l => l.toLowerCase() === playerId.toLowerCase());
          if (idx === -1) {
            return interaction.reply({ embeds: [warningEmbed("Not Whitelisted", `\`${playerId}\` is not in the **${faction}** spawn list.`)], ephemeral: true });
          }
          const oldRank = getFactionRank(faction, playerId);
          lines.splice(idx, 1);
          if (!writeFactionFile(spawn, lines)) {
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to \`${spawn}\`.`)], ephemeral: true });
          }
          removePlayerFromAllRankFiles(faction, playerId);
          await removeFactionRank(faction, playerId);
          writeFactionAudit({ action: "remove", faction, playerId, oldRank, by: interaction.user.tag });
          writeModLog({ action: "faction-remove", playerId, faction, oldRank, by: interaction.user.tag });
          const cap = getFactionCap(faction);
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle(`⚔️  Removed from ${faction}`)
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",    value: `\`${playerId}\``,             inline: true },
              { name: "⚔️  Faction",    value: faction,                       inline: true },
              { name: "🎖️  Was",         value: rankBadge(faction, oldRank),  inline: true },
              { name: "👥  Roster Size", value: `${lines.length} / ${cap}`,   inline: true },
              { name: "🔒  Removed By",  value: `${interaction.user}`,        inline: true },
            ).setFooter({ text: "Removed from spawn file and all rank files · audit logged" }).setTimestamp();
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
        await interaction.deferReply({ ephemeral: true });
        try {
          if (server === "both") {
            const [r1, r2] = await Promise.all([sendRcon(command, "server1"), sendRcon(command, "server2")]);
            return interaction.editReply({ embeds: [
              new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("📡  Raw RCON — Both Servers").setDescription(`${DIVIDER}`)
                .addFields(
                  { name: "📤  Signal",             value: `\`\`\`${command}\`\`\``,                                     inline: false },
                  { name: "1️⃣  Server 1 Response",  value: `\`\`\`${(r1.trim() || "no response").slice(0, 900)}\`\`\``, inline: false },
                  { name: "2️⃣  Server 2 Response",  value: `\`\`\`${(r2.trim() || "no response").slice(0, 900)}\`\`\``, inline: false },
                  { name: "🔒  By",                  value: `${interaction.user}`,                                         inline: false },
                ).setTimestamp()
            ]});
          }
          const result = await sendRcon(command, server);
          writeModLog({ action: "manual-rcon", command, server, by: interaction.user.tag });
          await logAction(new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("📡  Manual RCON")
            .addFields({ name: "Signal", value: `\`${command}\``, inline: true }, { name: "Server", value: serverLabel(server), inline: true }, { name: "By", value: interaction.user.tag, inline: true }).setTimestamp());
          return interaction.editReply({ embeds: [
            new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("📡  RCON Transmission Complete").setDescription(`${DIVIDER}`)
              .addFields(
                { name: "📤  Signal",  value: `\`\`\`${command}\`\`\``,                                             inline: false },
                { name: "🖥️  Server",  value: `${serverEmoji(server)}  ${serverLabel(server)}`,                     inline: true },
                { name: "🔒  By",      value: `${interaction.user}`,                                                inline: true },
                { name: "📥  Response",value: `\`\`\`${(result.trim() || "no response").slice(0, 1000)}\`\`\``,    inline: false },
              ).setTimestamp()
          ]});
        } catch (err) {
          return interaction.editReply({ embeds: [errorEmbed("RCON Failed", `Cannot reach **${serverLabel(server)}**.\n\`\`\`${err.message}\`\`\`\nCheck \`/ping\` for server status.`)] });
        }
      }

      /* ─────────────────────────────────────────────────────
         ROTATEMAP
         ───────────────────────────────────────────────────── */
      case "rotatemap": {
        const server = interaction.options.getString("server");
        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("rm_confirm").setLabel("Yes, rotate now").setStyle(ButtonStyle.Danger).setEmoji("🔄"),
          new ButtonBuilder().setCustomId("rm_cancel").setLabel("Cancel").setStyle(ButtonStyle.Secondary)
        );
        const msg = await interaction.reply({
          embeds: [warningEmbed("Confirm Map Rotation",
            `> *"The battle lines are shifting. You sure about this?"*\n\n${DIVIDER}\n\nThis will **end the current round** on **${serverLabel(server)}** for all online players.`
          ).setFooter({ text: "Expires in 30 seconds" })],
          components: [row], ephemeral: true, fetchReply: true,
        });
        try {
          const btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: 30_000, filter: i => i.user.id === interaction.user.id });
          if (btn.customId === "rm_cancel") return btn.update({ embeds: [new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("🪖  Rotation Cancelled").setDescription("Map rotation cancelled.").setTimestamp()], components: [] });
          await btn.deferUpdate();
          await sendRconBoth("Rotatemap", server);
          writeModLog({ action: "rotatemap", server, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("🔄  Map Rotation Initiated")
            .setDescription(`> *"Find new ground, soldier."*\n\n${DIVIDER}`)
            .addFields({ name: "🖥️  Server", value: `${serverEmoji(server)}  ${serverLabel(server)}`, inline: true }, { name: "🔒  By", value: `${interaction.user}`, inline: true })
            .setTimestamp();
          brand(embed); await logAction(embed);
          return btn.editReply({ embeds: [embed], components: [] });
        } catch {
          return interaction.editReply({ embeds: [warningEmbed("Timed Out", "Confirmation expired. No changes made.")], components: [] });
        }
      }

      /* ─────────────────────────────────────────────────────
         ADDWAGE
         ───────────────────────────────────────────────────── */
      case "addwage": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const tierKey  = interaction.options.getString("tier");
        const tier     = WAGE_TIERS[tierKey];
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        if (!tier)     return interaction.reply({ embeds: [errorEmbed("Invalid Tier", "Unknown payment tier.")], ephemeral: true });
        const wages    = loadWages();
        const existing = wages.find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        if (!tier.weekly) {
          const current = readPlayerBalance(playerId) ?? 0;
          const newBal  = current + tier.amount;
          if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Ledger Write Failed", `Could not deposit **${tier.amount} caps** to \`${playerId}\`. Check \`MODSAVE_PATH\`.`)], ephemeral: true });
          writeModLog({ action: "givecaps", playerId, amount: tier.amount, reason: "Mercenary payment", by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💸  Mercenary Payment Issued")
            .setDescription(`> *"Caps now. No strings attached."*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",    value: `\`${playerId}\``,                          inline: true },
              { name: "💰  Payment",    value: `**${tier.amount.toLocaleString()} caps**`, inline: true },
              { name: "💵  New Balance",value: `**${newBal.toLocaleString()} caps**`,      inline: true },
              { name: "🔒  By",        value: `${interaction.user}`,                      inline: false },
            ).setFooter({ text: randomQuote("caps") }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }
        if (existing?.tier === tierKey) {
          const ts = Math.floor(existing.addedAt / 1000);
          return interaction.reply({ embeds: [warningEmbed("Already on Payroll", `\`${playerId}\` is already enrolled as **${tier.label}** (+${tier.amount}/wk).\n**Enrolled:** <t:${ts}:F> by **${existing.addedBy}**\n\nUse \`/removewage\` first to change tier.`)], ephemeral: true });
        }
        if (existing) {
          const old = WAGE_TIERS[existing.tier];
          existing.tier = tierKey; existing.updatedAt = Date.now(); existing.updatedBy = interaction.user.tag;
          saveWages(wages);
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("🔄  Payroll Tier Updated").setDescription(`${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",value: `\`${playerId}\``,                                    inline: true },
              { name: "⬇️  Old",    value: `${old?.label ?? "?"} (+${old?.amount ?? "?"}/wk)`,   inline: true },
              { name: "⬆️  New",    value: `**${tier.label}** (+${tier.amount}/wk)`,             inline: true },
              { name: "🔒  By",     value: `${interaction.user}`,                                inline: false },
            ).setFooter({ text: "Payroll updated — takes effect next cycle" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }
        wages.push({ playerId, tier: tierKey, addedBy: interaction.user.tag, addedAt: Date.now(), lastPaidAt: null, updatedAt: null, updatedBy: null });
        saveWages(wages);
        const bal = readPlayerBalance(playerId);
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💰  Courier Added to Payroll")
          .setDescription(`> *"A fair day's work for a fair day's pay."*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",    value: `\`${playerId}\``,                                              inline: true },
            { name: "📋  Tier",       value: `**${tier.label}**`,                                            inline: true },
            { name: "💵  Weekly",     value: `+**${tier.amount} caps/week**`,                                inline: true },
            { name: "💰  Balance",    value: bal !== null ? `${bal.toLocaleString()} caps` : "*no ledger*", inline: true },
            { name: "🔒  By",        value: `${interaction.user}`,                                          inline: true },
            { name: "⏰  Next Payout",value: "Within 7 days of enrolment",                                  inline: true },
          ).setFooter({ text: randomQuote("wages") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         REMOVEWAGE
         ───────────────────────────────────────────────────── */
      case "removewage": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const wages   = loadWages();
        const removed = wages.find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        if (!removed) return interaction.reply({ embeds: [warningEmbed("Not on Payroll", `\`${playerId}\` isn't enrolled.\nUse \`/wagelist\` to see who's on the books.`)], ephemeral: true });
        saveWages(wages.filter(w => w.playerId.toLowerCase() !== playerId.toLowerCase()));
        const tier = WAGE_TIERS[removed.tier];
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("📤  Removed from Payroll").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",value: `\`${playerId}\``,                                          inline: true },
            { name: "📋  Was",    value: `${tier?.label ?? removed.tier} (+${tier?.amount ?? "?"}/wk)`, inline: true },
            { name: "🔒  By",    value: `${interaction.user}`,                                       inline: true },
            { name: "ℹ️  Note",  value: "Existing balance unchanged. No further weekly payouts.",    inline: false },
          ).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         WAGELIST
         ───────────────────────────────────────────────────── */
      case "wagelist": {
        const wages = loadWages().filter(w => WAGE_TIERS[w.tier]?.weekly);
        if (!wages.length) return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("💰  Payroll — Empty").setDescription('> *"No couriers on the books yet."*\n\nUse `/addwage` to enrol someone.').setTimestamp()], ephemeral: true });
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
          new EmbedBuilder().setColor(NV.GOLD).setTitle("💰  Weekly Payroll — The House's Ledger")
            .setDescription(`${header}\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: "Wages disbursed automatically every 7 days" }).setTimestamp(),
          { perPage: 12, ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         CHECKBALANCE
         ───────────────────────────────────────────────────── */
      case "checkbalance": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const fp = getPlayerFilePath(playerId);
        if (!fp) return interaction.reply({ embeds: [errorEmbed("Vault Offline", "`MODSAVE_PATH` not set in `.env`.")], ephemeral: true });
        if (!fs.existsSync(fp)) return interaction.reply({ embeds: [warningEmbed("No Ledger Found", `\`${playerId}\` has no ledger yet.\nThey must join the server first, or be assigned a wage with \`/addwage\`.`)], ephemeral: true });
        const balance = readPlayerBalance(playerId);
        if (balance === null) return interaction.reply({ embeds: [errorEmbed("Ledger Corrupted", `Could not parse ledger for \`${playerId}\`.\nPath: \`${fp}\``)], ephemeral: true });
        const wage   = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier  = wage ? (WAGE_TIERS[wage.tier] ?? { label: wage.tier, amount: "?", weekly: true }) : null;
        const nextTs = wage?.lastPaidAt ? Math.floor((wage.lastPaidAt + WAGE_INTERVAL_MS) / 1000) : null;
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💵  Courier Ledger").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier", value: `\`${playerId}\``,                          inline: true },
            { name: "💰  Balance", value: `**${balance.toLocaleString()} caps**`,      inline: true },
            { name: "📋  Payroll", value: wTier ? `✅  ${wTier.label} (+${wTier.amount}/wk)` : "❌  Not enrolled", inline: true },
          ).setFooter({ text: randomQuote("caps") }).setTimestamp();
        if (nextTs) embed.addFields({ name: "⏰  Next Payout", value: `<t:${nextTs}:R>`, inline: true });
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         GIVECAPS
         ───────────────────────────────────────────────────── */
      case "givecaps": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const amount   = interaction.options.getInteger("amount");
        const reason   = interaction.options.getString("reason") ?? "Cap gift";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const current = readPlayerBalance(playerId) ?? 0;
        const newBal  = current + amount;
        if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Ledger Write Failed", "Check `MODSAVE_PATH`.")], ephemeral: true });
        writeModLog({ action: "givecaps", playerId, amount, reason, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💸  Caps Given").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",    value: `\`${playerId}\``,                      inline: true },
            { name: "💰  Given",      value: `**+${amount.toLocaleString()} caps**`, inline: true },
            { name: "💵  New Balance",value: `**${newBal.toLocaleString()} caps**`,  inline: true },
            { name: "📋  Reason",     value: reason,                                 inline: false },
            { name: "🔒  By",        value: `${interaction.user}`,                  inline: false },
          ).setFooter({ text: randomQuote("caps") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         TRANSFERCAPS
         ───────────────────────────────────────────────────── */
      case "transfercaps": {
        const fromId = sanitizeId(interaction.options.getString("from_id"));
        const toId   = sanitizeId(interaction.options.getString("to_id"));
        const amount = interaction.options.getInteger("amount");
        if (!fromId || !toId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        if (fromId.toLowerCase() === toId.toLowerCase()) return interaction.reply({ embeds: [errorEmbed("Invalid Transfer", "Cannot transfer caps to the same courier.")], ephemeral: true });
        const fromBal = readPlayerBalance(fromId);
        if (fromBal === null) return interaction.reply({ embeds: [errorEmbed("No Ledger", `\`${fromId}\` has no ledger file.`)], ephemeral: true });
        if (fromBal < amount) return interaction.reply({ embeds: [errorEmbed("Insufficient Caps", `\`${fromId}\` only has **${fromBal.toLocaleString()} caps**.`)], ephemeral: true });
        const toBal = readPlayerBalance(toId) ?? 0;
        // Two separate ledger files — write sequentially and roll the debit
        // back if the credit fails, so caps can never vanish mid-transfer.
        if (!writePlayerBalance(fromId, fromBal - amount)) {
          return interaction.reply({ embeds: [errorEmbed("Write Failed", "Could not debit the sender. Check `MODSAVE_PATH`.")], ephemeral: true });
        }
        if (!writePlayerBalance(toId, toBal + amount)) {
          writePlayerBalance(fromId, fromBal);   // refund — transfer aborted
          return interaction.reply({ embeds: [errorEmbed("Write Failed", "Could not credit the recipient — transfer rolled back, no caps moved.")], ephemeral: true });
        }
        writeModLog({ action: "transfercaps", fromId, toId, amount, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💱  Caps Transfer Complete").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "📤  From",  value: `\`${fromId}\`\n**${(fromBal - amount).toLocaleString()} caps** remaining`, inline: true },
            { name: "📥  To",    value: `\`${toId}\`\n**${(toBal + amount).toLocaleString()} caps** balance`,       inline: true },
            { name: "💸  Amount",value: `**${amount.toLocaleString()} caps**`,                                       inline: true },
            { name: "🔒  By",    value: `${interaction.user}`,                                                       inline: false },
          ).setFooter({ text: randomQuote("caps") }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         ADJUSTCAPS
         ───────────────────────────────────────────────────── */
      case "adjustcaps": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        const amount   = interaction.options.getInteger("amount");
        const reason   = interaction.options.getString("reason") ?? "Manual adjustment";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const current = readPlayerBalance(playerId) ?? 0;
        const newBal  = Math.max(0, current + amount);
        if (!writePlayerBalance(playerId, newBal)) return interaction.reply({ embeds: [errorEmbed("Write Failed", "Check `MODSAVE_PATH`.")], ephemeral: true });
        writeModLog({ action: "adjustcaps", playerId, amount, reason, by: interaction.user.tag });
        const pos = amount >= 0;
        const embed = new EmbedBuilder().setColor(pos ? NV.IRRAD_GREEN : NV.RUST_RED)
          .setTitle(`⚙️  Caps ${pos ? "Credited" : "Debited"}`).setDescription(`${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",     value: `\`${playerId}\``,                                    inline: true },
            { name: `${pos ? "📈" : "📉"}  Change`,value: `**${pos ? "+" : ""}${amount.toLocaleString()} caps**`, inline: true },
            { name: "💰  New Balance", value: `**${newBal.toLocaleString()} caps**`,                inline: true },
            { name: "📋  Reason",      value: reason,                                               inline: false },
            { name: "🔒  By",         value: `${interaction.user}`,                                inline: false },
          ).setFooter({ text: "Manual cap adjustment · logged" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         STATS
         ───────────────────────────────────────────────────── */
      case "stats": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const playtime = loadPlaytime();
        const minutes  = playtime[playerId] ?? null;
        const factions = getPlayerFactions(playerId);
        const onS1     = playerCache.server1.some(n => n.toLowerCase() === playerId.toLowerCase());
        const onS2     = playerCache.server2.some(n => n.toLowerCase() === playerId.toLowerCase());
        const online   = onS1 || onS2;
        const balance  = readPlayerBalance(playerId);
        const wage     = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier    = wage ? WAGE_TIERS[wage.tier] : null;
        const warns    = loadWarns()[playerId.toLowerCase()] ?? [];
        const tb       = loadBans().find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        const history  = getPlayerHistory(playerId);
        const notes    = getPlayerNotes(playerId);
        const lastSeen = getLastSeen(playerId);
        const donator  = isDonator(playerId);

        const fStr = factions === null ? "⚠️  Folder unreadable"
          : !factions.length ? "*No faction access*"
          : factions.map(f => {
              const rank = getFactionRank(f, playerId);
              return `${getFactionRankBadge(f, rank)}  **${f}** *(${rank})*`;
            }).join("\n");

        const statusStr = !online ? "🔴  Offline" : [onS1 && "🟢  Server 1", onS2 && "🟢  Server 2"].filter(Boolean).join("  +  ");
        const color = tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`🪪  Courier Dossier — ${playerId}`)
          .setDescription(
            tb ? hero(`Currently serving exile — ${formatTimeLeft(tb.expires)} remaining.`) :
            online ? hero("Currently active on the Strip.") :
            hero("Offline — last tracked playtime shown.")
          )
          .addFields(
            { name: "📡  Status",        value: statusStr,                                                          inline: true },
            { name: "⏱️  Playtime",      value: minutes !== null ? `**${formatPlaytime(minutes)}**` : "*No record*", inline: true },
            { name: "⚠️  Warnings",      value: warns.length ? `**${warns.length}** on record` : "Clean record",    inline: true },
            { name: "👁️  Last Seen",     value: online ? "🟢  Online now" : lastSeen ? `<t:${Math.floor(lastSeen / 1000)}:R>` : "*No record*", inline: true },
            { name: "📝  Staff Notes",   value: notes.length ? `**${notes.length}** — use \`/note list ${playerId}\`` : "*None*", inline: true },
            { name: "💎  Donator",       value: donator ? "✅  Yes" : "❌  No",                                       inline: true },
            { name: "⚔️  Faction Ranks", value: fStr,                                                               inline: false },
          );

        if (balance !== null) {
          embed.addFields({ name: "💰  Balance", value: `**${balance.toLocaleString()} caps**${wTier ? `  ·  Payroll: ${wTier.label} (+${wTier.amount}/wk)` : "  ·  Not on payroll"}`, inline: false });
        }
        if (tb) {
          const ts = Math.floor(tb.expires / 1000);
          embed.addFields({ name: "⏳  Active Exile", value: `Temp ban — *${tb.reason}*  ·  expires <t:${ts}:R>`, inline: false });
        }
        if (history.length) {
          embed.addFields({ name: "📋  Mod Actions", value: `**${history.length}** total — use \`/history ${playerId}\` to view`, inline: false });
        }

        brand(embed, { thumb: true, footer: { text: "Playtime tracked every 60s since deployment" } });
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         INSPECT  (owner only — full dossier incl. IPs & alts)
         ───────────────────────────────────────────────────── */
      case "inspect": {
        if (!isOwner(interaction.user.id)) return interaction.reply({ embeds: [ownerOnlyEmbed()], ephemeral: true });
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const key = playerId.toLowerCase();

        // gather everything
        const minutes  = loadPlaytime()[playerId] ?? null;
        const factions = getPlayerFactions(playerId);
        const onS1     = playerCache.server1.some(n => n.toLowerCase() === key);
        const onS2     = playerCache.server2.some(n => n.toLowerCase() === key);
        const online   = onS1 || onS2;
        const balance  = readPlayerBalance(playerId);
        const wage     = loadWages().find(w => w.playerId.toLowerCase() === key);
        const wTier    = wage ? WAGE_TIERS[wage.tier] : null;
        const warns    = loadWarns()[key] ?? [];
        const tb       = loadBans().find(b => b.playerId.toLowerCase() === key);
        const history  = getPlayerHistory(playerId);
        const notes    = getPlayerNotes(playerId);
        const lastSeen = getLastSeen(playerId);
        const donator  = isDonator(playerId);
        const known    = loadKnownPlayers()[key];
        // IP intel (owner-only) — CONFIRMED IPs only (same-line log pairings, not live correlation)
        let ips = [], alts = [], flagged = [];
        try { ips = ipBans.getConfirmedIPsForPlayer(playerId); alts = ipBans.getAltNamesOf(playerId); flagged = ips.filter(ip => ipBans.blacklist.includes(ip)); } catch {}

        const fStr = factions === null ? "⚠️  Folder unreadable"
          : !factions.length ? "*None*"
          : factions.map(f => `${getFactionRankBadge(f, getFactionRank(f, playerId))}  **${f}** *(${getFactionRank(f, playerId)})*`).join("\n");
        const statusStr = !online ? "🔴  Offline" : [onS1 && "🟢  S1", onS2 && "🟢  S2"].filter(Boolean).join(" + ");
        const color = flagged.length ? NV.LEGION_RED : tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`👁️  Full Dossier — ${playerId}`)
          .setDescription(hero("Everything on record. Owner eyes only."))
          .addFields(
            { name: "📡  Status",     value: statusStr,                                                              inline: true },
            { name: "⏱️  Playtime",   value: minutes !== null ? `**${formatPlaytime(minutes)}**` : "*None*",          inline: true },
            { name: "👁️  Last Seen",  value: online ? "Online now" : lastSeen ? `<t:${Math.floor(lastSeen / 1000)}:R>` : "*Never*", inline: true },
            { name: "💰  Balance",    value: balance !== null ? `**${balance.toLocaleString()}** caps` : "*No ledger*", inline: true },
            { name: "📋  Payroll",    value: wTier ? `${wTier.label} (+${wTier.amount}/wk)` : "❌",                    inline: true },
            { name: "💎  Donator",    value: donator ? "✅" : "❌",                                                    inline: true },
            { name: "⚠️  Warnings",   value: `**${warns.length}**`,                                                   inline: true },
            { name: "🗒️  Mod Actions", value: `**${history.length}**`,                                                inline: true },
            { name: "📝  Staff Notes", value: `**${notes.length}**`,                                                  inline: true },
            { name: "⚔️  Factions & Ranks", value: fStr, inline: false },
          );

        // ban status
        const banLines = [];
        if (tb) banLines.push(`⏳  **Temp ban** — *${tb.reason}* · expires <t:${Math.floor(tb.expires / 1000)}:R> · by ${tb.moderator}`);
        embed.addFields({ name: "🚫  Ban Status", value: banLines.length ? banLines.join("\n").slice(0, 1024) : "✅  No active bans", inline: false });

        // IP intel
        embed.addFields(
          { name: `🌐  Confirmed IPs (${ips.length})`, value: (ips.length ? ips.map(ip => `\`${ip}\`${ipBans.blacklist.includes(ip) ? " 🔨" : ""}`).join("  ·  ") : "*none confirmed yet*").slice(0, 1024), inline: false },
          { name: `🔗  Alt Accounts (${alts.length})`, value: (alts.length ? alts.map(a => `\`${a}\``).join("  ·  ") : "*none*").slice(0, 1024), inline: false },
        );
        if (flagged.length) embed.addFields({ name: "🛑  IP Flag", value: `**${flagged.length}** of their IP(s) are blacklisted — connecting accounts are auto-banned.`, inline: false });

        // recent staff notes (inline, since owner)
        if (notes.length) {
          const recent = notes.slice(-5).map((n, i) => `\`${i + 1}.\` ${n.text} — *${n.by}*`).join("\n");
          embed.addFields({ name: "📝  Latest Notes", value: recent.slice(0, 1024), inline: false });
        }

        const footerBits = [];
        if (known?.firstSeen) footerBits.push(`first seen ${new Date(known.firstSeen).toISOString().slice(0, 10)}`);
        if (known?.name && known.name !== playerId) footerBits.push(`display: ${known.name}`);
        brand(embed, { thumb: true, footer: { text: footerBits.join("  ·  ") || "Owner inspection" } });
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

    }
  } catch (err) {
    logger.error("Command", `/${interaction.commandName}: ${err.message}`, { stack: err.stack });
    const reply = {
      embeds: [errorEmbed("System Failure", `An internal error occurred processing \`/${interaction.commandName}\`.\n\n\`\`\`${err.message?.slice(0, 200) ?? "unknown"}\`\`\`\nCheck the server logs for the full stack trace.`)],
      ephemeral: true,
    };
    try {
      if (interaction.deferred || interaction.replied) return interaction.editReply(reply);
      return interaction.reply(reply);
    } catch {}
  }
});

/* ================================================================
   STARTUP
   ================================================================ */
// Only log in when run directly (`node index.js`). When required as a
// module (e.g. from tests) the helpers are exported instead, so unit tests
// can exercise the pure logic without opening a Discord connection.
if (require.main === module) {
  client.login(process.env.DISCORD_TOKEN);
}

module.exports = {
  FILES,
  // player notes
  getPlayerNotes, addPlayerNote, clearPlayerNotes,
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
  removeWarningAt,
  // bans (serialized)
  loadBans, upsertTempBan, removeBans,
  // donators
  DONATOR_FILE, readDonatorFile, writeDonatorFile, isDonator, addDonator, removeDonator,
  // owner / access
  isOwner, isBlacklisted,
  // ui / parsing helpers
  splitPages, extractPlayerNames, bar, dmStatusField,
  // faction rank caps
  getFactionRankCap, getFactionRankCaps, setFactionRankCap,
};
