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
} = require("discord.js");

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
]);
function isOwner(userId) { return OWNER_IDS.has(String(userId)); }

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
    "MODSAVE_PATH", "MOD_LOG_CHANNEL", "LEADERBOARD_CHANNEL", "LOG_LEVEL",
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
  HARDBAN:        "./hardban_registry.json",
  NOTES:          "./hardban_notes.json",
  WARNS:          "./warnings.json",
  MODLOG:         "./modlog.json",
  FACTION_RANKS:  "./faction_ranks.json",
  FACTION_CONFIG: "./faction_config.json",
  FACTION_AUDIT:  "./faction_audit.json",
  MENU_GRANTS:    "./menu_grants.json",
  BLACKLIST:      "./blacklist.json",
  PLAYER_NOTES:   "./player_notes.json",
  LASTSEEN:       "./lastseen.json",
};

const DEFAULTS = {
  [FILES.TEMPBAN]:        "[]",
  [FILES.WAGES]:          "[]",
  [FILES.PLAYTIME]:       "{}",
  [FILES.HARDBAN]:        "[]",
  [FILES.NOTES]:          "{}",
  [FILES.WARNS]:          "{}",
  [FILES.MODLOG]:         "[]",
  [FILES.FACTION_RANKS]:  "{}",
  [FILES.FACTION_CONFIG]: "{}",
  [FILES.FACTION_AUDIT]:  "[]",
  [FILES.MENU_GRANTS]:    "{}",
  [FILES.BLACKLIST]:      "{}",
  [FILES.PLAYER_NOTES]:   "{}",
  [FILES.LASTSEEN]:       "{}",
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
const saveBans          = (d) => safeWrite(FILES.TEMPBAN,       d);
const loadRoles         = () => safeRead(FILES.ROLES,          { modRoleId: "", adminRoleId: "", factionLeaderRoleId: "" });
const saveRoles         = (d) => safeWrite(FILES.ROLES,         d);
const loadWages         = () => safeRead(FILES.WAGES,          []);
const saveWages         = (d) => safeWrite(FILES.WAGES,         d);
const loadPlaytime      = () => safeRead(FILES.PLAYTIME,       {});
const savePlaytime      = (d) => safeWrite(FILES.PLAYTIME,      d);
const loadHardBans      = () => safeRead(FILES.HARDBAN,        []);
const saveHardBans      = (d) => safeWrite(FILES.HARDBAN,       d);
const loadNotes         = () => safeRead(FILES.NOTES,          {});
const saveNotes         = (d) => safeWrite(FILES.NOTES,         d);
const loadWarns         = () => safeRead(FILES.WARNS,          {});
const saveWarns         = (d) => safeWrite(FILES.WARNS,         d);
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
const loadBlacklist     = () => safeRead(FILES.BLACKLIST,      {});
const saveBlacklist     = (d) => safeWrite(FILES.BLACKLIST,     d);
const loadPlayerNotes   = () => safeRead(FILES.PLAYER_NOTES,   {});
const savePlayerNotes   = (d) => safeWrite(FILES.PLAYER_NOTES,  d);
const loadLastSeen      = () => safeRead(FILES.LASTSEEN,       {});
const saveLastSeen      = (d) => safeWrite(FILES.LASTSEEN,      d);

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
  { name: "Staff",   value: "staff",   menuId: "0011110000000000101000000000010 01101101000001" },
  { name: "Faction", value: "faction", menuId: "0000010000000000000000000000010 00100001000000" },
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
const RCON_HEALTH_INTERVAL_MS = 5 * 60 * 1000;

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
    order:   ["Recruit", "Legionnaire", "Veteran Legionnaire", "Legate"],
    default: "Recruit",
    badges:  {
      "Recruit":             "🪖",
      "Legionnaire":         "⚔️",
      "Veteran Legionnaire": "🎖️",
      "Legate":              "👑",
    },
    rankFiles: {
      "Recruit":             "legionrecruit.txt",
      "Legionnaire":         "legionlegionnaire.txt",
      "Veteran Legionnaire": "legionveteranlegionnaire.txt",
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
    order:   ["Low Rank", "High Rank"],
    default: "Low Rank",
    badges:  {
      "Low Rank":  "🪖",
      "High Rank": "👑",
    },
    rankFiles: {
      "Low Rank":  "khanslowrank.txt",
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

/* Donator whitelist file. Override the location with the DONATOR_PATH env
   var; defaults to the ModSave config dir alongside the faction role files. */
const DONATOR_FILE = process.env.DONATOR_PATH
  || "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/donator.txt";

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

const QUOTES = {
  ban:     [
    '"You\'re banned from the Lucky 38. Mr. House\'s orders."',
    '"You\'ve made an enemy of the Mojave. Enjoy the wasteland."',
    '"Even in the wasteland, there are rules. You broke them."',
    '"The Securitrons don\'t forgive. Neither do we."',
  ],
  unban:   [
    '"Every soul deserves a second chance in the Mojave. Don\'t waste yours."',
    '"The gates of the Strip open once more. Don\'t make us regret it."',
    '"Exile lifted. Welcome back to New Vegas — try not to shoot anyone."',
  ],
  warn:    [
    '"Consider this a warning, friend. We\'re watching."',
    '"The Strip has eyes everywhere. Don\'t test us again."',
    '"One more strike and the Securitrons handle it personally."',
  ],
  caps:    [
    '"War never changes. But caps? Caps are forever."',
    '"The House always collects. Today, it pays."',
    '"A courier without caps is just a wanderer."',
    '"In the Mojave, caps are the only truth that matters."',
  ],
  hardban: [
    '"Some debts can\'t be paid in caps."',
    '"The Mojave has no room for those who spit on its laws."',
    '"Persona non grata. Not welcome under any name, on any server."',
  ],
  system:  [
    '"All systems nominal. Securitron network active."',
    '"Maintenance cycle complete. The Strip never sleeps."',
    '"Mr. House is watching. Always watching."',
  ],
  wages:   [
    '"The House always pays its debts — eventually."',
    '"Caps distributed. The economy of the Mojave endures."',
    '"A fair day\'s work for a fair day\'s pay. Even in the apocalypse."',
  ],
  announce: [
    '"Attention all couriers on the Strip..."',
    '"Message from the Mojave Authority..."',
    '"Broadcast from the Lucky 38..."',
  ],
  faction: [
    '"Allegiances in the Mojave are written in blood and caps."',
    '"Every faction needs soldiers. Every soldier needs orders."',
    '"The wasteland belongs to those who organise."',
    '"Rank is earned. Loyalty is proven."',
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
  return String(raw ?? "").trim().replace(/[^a-zA-Z0-9_\-.]/g, "").slice(0, 64);
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
   ================================================================ */
function isBlacklisted(userId)  { return !!loadBlacklist()[String(userId)]; }
function getBlacklistEntry(userId) { return loadBlacklist()[String(userId)] ?? null; }

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
  const render = (p) => ({ embeds: [buildEmbed(pages[p], p, total)], components: total > 1 ? [row(p)] : [] });

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

function successEmbed(title, description, quoteCategory = "system") {
  return new EmbedBuilder().setColor(NV.AMBER)
    .setTitle(`✅  ${title}`).setDescription(description)
    .setFooter({ text: randomQuote(quoteCategory) }).setTimestamp();
}
function errorEmbed(title, description) {
  return new EmbedBuilder().setColor(NV.RUST_RED)
    .setTitle(`☢️  ${title}`).setDescription(description)
    .setFooter({ text: "Securitron network active. Incident logged." }).setTimestamp();
}
function warningEmbed(title, description) {
  return new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle(`⚠️  ${title}`).setDescription(description).setTimestamp();
}
function adminOnlyEmbed() {
  return new EmbedBuilder().setColor(NV.DEEP_BLACK)
    .setTitle("🎰  Access Denied — Mr. House's Domain")
    .setDescription('> *"I didn\'t survive two centuries to be overruled by the uninvited."*\n\nThis command is restricted to **Administrators** only.')
    .setFooter({ text: "Unauthorized access attempt logged." }).setTimestamp();
}
function modOnlyEmbed() {
  return new EmbedBuilder().setColor(NV.DEAD_GREY)
    .setTitle("🛡️  Clearance Required")
    .setDescription('> *"You don\'t have the credentials for this, friend."*\n\nThis command requires the **Moderator** role.')
    .setFooter({ text: "Access restricted. Civilian status confirmed." }).setTimestamp();
}
function blacklistedEmbed(entry) {
  const reason = entry?.reason ? `\n\n**Reason:** ${entry.reason}` : "";
  return new EmbedBuilder().setColor(NV.LEGION_RED)
    .setTitle("⛔  Blacklisted — Access Revoked")
    .setDescription(`> *"You're persona non grata around here. The Securitrons won't lift a finger for you."*\n\nYou have been **blacklisted** from using this bot. All commands are unavailable to you.${reason}`)
    .setFooter({ text: "Contact an administrator if you believe this is a mistake." }).setTimestamp();
}
function factionLeaderOnlyEmbed() {
  return new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("⚔️  Faction Authority Required")
    .setDescription('> *"Only faction leaders pull strings around here, stranger."*\n\nRequires the **Faction Leader** role (or Moderator).')
    .setFooter({ text: "Faction access not verified." }).setTimestamp();
}
function factionLeaderStrictEmbed() {
  return new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("⚔️  Faction Leader Authority Required")
    .setDescription('> *"Rank assignments are the sole domain of faction leadership."*\n\nThis action requires the **Faction Leader** role specifically.')
    .setFooter({ text: "Rank authority not verified." }).setTimestamp();
}
function emptyIdEmbed() {
  return new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("📭  No Courier ID Provided")
    .setDescription("A valid **Courier ID** or username is required.\n\n💡 *Start typing in the player field — autocomplete surfaces anyone currently online.*")
    .setFooter({ text: "Tip: Manual IDs are accepted if the player is offline." }).setTimestamp();
}
function rateLimitEmbed() {
  return new EmbedBuilder().setColor(NV.DEAD_GREY)
    .setTitle("⏱️  Slow Down, Courier")
    .setDescription("You're issuing commands too quickly. Wait a moment and try again.")
    .setFooter({ text: "Rate limit active." }).setTimestamp();
}

/* ================================================================
   HARD BAN HELPERS
   ================================================================ */
function findHardBanByAnyId(playerId) {
  const id = playerId.toLowerCase();
  return loadHardBans().find(e =>
    e.primaryId.toLowerCase() === id ||
    e.linkedIds.some(l => l.toLowerCase() === id)
  );
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
   PLAYER LIST EMBED
   ================================================================ */
function buildPlayerListEmbed(raw, server) {
  const data   = parseRcon(raw);
  const label  = serverLabel(server);
  const embed  = new EmbedBuilder().setTitle(`${serverEmoji(server)}  Active Couriers — ${label}`).setTimestamp();

  if (!data?.Successful) {
    return embed.setColor(NV.RUST_RED).setDescription(
      "### ☢️  Signal Lost\nCannot reach the server.\n\n**Possible causes:**\n· Server offline\n· RCON credentials wrong in `.env`\n· Network blocked"
    ).setFooter({ text: "Connection failed" });
  }
  const players = data.PlayerList ?? [];
  if (!players.length) {
    return embed.setColor(NV.IRRAD_GREEN)
      .setDescription("### 🌵  Wasteland is Quiet\n*No couriers online.*\nBe the first one out there.")
      .setFooter({ text: `${label} · 0 online` });
  }
  const lines = players.map((p, i) => {
    const name = p.name ?? p.Name ?? p.username ?? p.Username ?? "Unknown";
    const id   = p.id   ?? p.Id   ?? p.uniqueId ?? "";
    return `\`${String(i + 1).padStart(2, "0")}\`  **${name}**${id ? `  ·  \`${id}\`` : ""}`;
  }).join("\n");
  return embed.setColor(NV.IRRAD_GREEN).setDescription(lines)
    .setFooter({ text: `${label} · ${players.length} courier${players.length !== 1 ? "s" : ""} online` });
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
    await sendRconBoth(`Ban ${sanitizeId(playerId)}`, server);
    await update(FILES.TEMPBAN, [], (bans) => {
      const filtered = bans.filter(b => b.playerId.toLowerCase() !== key);
      filtered.push({ playerId, reason: `Auto-ban: ${escalation.label}`, expires, durationLabel: label, moderator: "Auto-Escalation", server });
      return filtered;
    });
    escalated = { type: "tempban", label };
    writeModLog({ action: "auto-tempban", playerId, reason: escalation.label, duration: label });
  } else if (escalation && escalation.action === "permban") {
    await sendRconBoth(`Ban ${sanitizeId(playerId)}`, server);
    escalated = { type: "permban" };
    writeModLog({ action: "auto-permban", playerId, reason: escalation.label });
  }

  return { count, escalated };
}

/* ================================================================
   TEMP BAN EXPIRY
   ================================================================ */
async function processExpiredBans() {
  const bans = loadBans(), now = Date.now();
  const active = [];
  let changed = false;
  for (const ban of bans) {
    if (ban.expires > now) { active.push(ban); continue; }
    try {
      await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server1");
      await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server2");
      changed = true;
      logger.info("Bans", `Expired ban lifted: ${ban.playerId}`);
      writeModLog({ action: "auto-unban", playerId: ban.playerId, reason: "Sentence served" });
      await logAction(
        new EmbedBuilder().setColor(NV.AMBER).setTitle("⏰  Sentence Served — Courier Released")
          .setDescription('> *"Every soul deserves a second chance in the Mojave."*')
          .addFields(
            { name: "Courier",          value: `\`${ban.playerId}\``,          inline: true },
            { name: "Original Offense", value: ban.reason,                     inline: true },
            { name: "Duration Served",  value: ban.durationLabel ?? "Unknown", inline: true },
            { name: "Originally Banned",value: `by ${ban.moderator}`,          inline: false },
          ).setFooter({ text: "Exile expired — access restored automatically" }).setTimestamp()
      );
    } catch (err) {
      logger.error("Bans", `Unban failed for ${ban.playerId}: ${err.message}`);
      active.push(ban);
    }
  }
  if (changed) saveBans(active);
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
  await logAction(embed);
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
    .setTitle(`☢️  New Vegas Caps — Top ${LEADERBOARD_TOP_N}`)
    .setFooter({ text: `Updated every 6h  ·  v${BOT_VERSION}` }).setTimestamp();
  if (!entries) return embed.setColor(NV.RUST_RED).setDescription("### ☢️  Vault Records Inaccessible\n`MODSAVE_PATH` not configured or unreadable.\nCheck your `.env` file.");
  if (!entries.length) return embed.setColor(NV.IRRAD_GREEN).setDescription("### 🌵  No Ledgers Found\nNo cap records on file yet.");
  return embed.setDescription(
    `> *"War never changes. But caps? Caps fluctuate."*\n\n${DIVIDER}\n` +
    entries.map((e, i) => `${rankLabel(i)}  **${e.playerId}**  ·  ${e.balance.toLocaleString()} caps`).join("\n") +
    `\n${DIVIDER}`
  );
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
setInterval(processExpiredBans,  60_000);
setInterval(postLeaderboard,     LEADERBOARD_INTERVAL_MS);
setInterval(rconHealthCheck,     RCON_HEALTH_INTERVAL_MS);
setInterval(() => {
  refreshPlayerCacheWithMenuReapply("server1");
  refreshPlayerCacheWithMenuReapply("server2");
  tickPlaytime();
}, 60_000);
setInterval(processWagePayout, WAGE_INTERVAL_MS);

setTimeout(postLeaderboard, 20_000);
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

/* ================================================================
   TOPIC MENU SYSTEM  (select-menu + modal driven commands)
   ================================================================
   Commands are grouped by topic into a handful of parent commands. Running
   a parent (e.g. /bans) shows a dropdown of that group's actions; picking
   one opens a modal to collect inputs, which is then dispatched to the same
   handler logic as before. A few commands that need Discord role/user/choice
   pickers (faction, setroles, blacklist) stay as native commands, since a
   text modal can't reproduce those pickers.
   ================================================================ */

// Modal field shorthands.  style: "short" | "para"
const F = {
  player:     { id: "playerid", label: "Courier ID / username",            style: "short", required: true,  max: 64 },
  playerInfo: { id: "playerid", label: "Courier ID / username",            style: "short", required: true,  max: 64 },
  server:     { id: "server",   label: "Server: server1 / server2 / both", style: "short", required: false, placeholder: "both", max: 16 },
  reasonOpt:  { id: "reason",   label: "Reason (optional)",                 style: "para",  required: false, max: 300 },
  reasonReq:  { id: "reason",   label: "Reason",                            style: "para",  required: true,  max: 300 },
  duration:   { id: "duration", label: "Duration: 1h 6h 1d 3d 5d 1w 2w 1mo 3mo 6mo 1y", style: "short", required: true, placeholder: "1d", max: 8 },
  notesOpt:   { id: "notes",    label: "Notes (optional)",                  style: "para",  required: false, max: 300 },
  noteReq:    { id: "note",     label: "Note",                              style: "para",  required: true,  max: 500 },
  amount:     { id: "amount",   label: "Amount (caps)",                     style: "short", required: true,  placeholder: "100", max: 12 },
  amountSigned:{id: "amount",   label: "Amount (+credit / -debit)",         style: "short", required: true,  placeholder: "-50", max: 12 },
};

const MENU_GROUPS = {
  bans: {
    label: "Bans & Exiles", emoji: "📜", description: "Temp/permanent bans, hard bans, ban records",
    actions: [
      { key: "tempban",       label: "Temp ban",          emoji: "⏳", tier: "mod",   fields: [F.player, F.duration, F.server, F.reasonReq] },
      { key: "unban",         label: "Unban / lift exile", emoji: "🔓", tier: "mod",  fields: [F.player, F.server] },
      { key: "checkban",      label: "Check ban status",  emoji: "🔎", tier: "public", fields: [F.player, F.server] },
      { key: "banlist",       label: "View ban list",     emoji: "📜", tier: "public", fields: [F.server] },
      { key: "permban",       label: "Permanent ban",     emoji: "💀", tier: "admin", fields: [F.player, F.server, F.reasonReq, F.notesOpt] },
      { key: "hardban",       label: "Hard ban (+ alts)", emoji: "🔨", tier: "admin", fields: [F.player, F.server, F.reasonReq, { id: "linked_id", label: "Linked alt ID (optional)", style: "short", required: false, max: 64 }, F.notesOpt] },
      { key: "addnote",       label: "Add hard-ban note", emoji: "📝", tier: "admin", fields: [F.player, F.noteReq] },
      { key: "hardbanlist",   label: "Hard ban registry", emoji: "🗂️", tier: "admin", fields: [] },
      { key: "cleartempbans", label: "Clear all temp bans", emoji: "🧹", tier: "admin", fields: [] },
    ],
  },
  moderation: {
    label: "Moderation", emoji: "🛡️", description: "Kicks, warnings, notes, player dossiers",
    actions: [
      { key: "kick",          label: "Kick",              emoji: "👢", tier: "mod",   fields: [F.player, F.server, F.reasonOpt] },
      { key: "warn",          label: "Warn",              emoji: "⚠️", tier: "mod",   fields: [F.player, F.reasonReq, F.server] },
      { key: "delwarn",       label: "Remove one warning", emoji: "🧽", tier: "mod",  fields: [F.player, { id: "number", label: "Warning number (see warnings)", style: "short", required: true, placeholder: "1", max: 4 }] },
      { key: "warnings",      label: "View warnings",     emoji: "📋", tier: "public", fields: [F.player] },
      { key: "clearwarnings", label: "Clear all warnings", emoji: "🧹", tier: "admin", fields: [F.player] },
      { key: "history",       label: "Mod history",       emoji: "🗒️", tier: "mod",   fields: [F.player] },
      { key: "note_add",      label: "Add staff note",    emoji: "📝", tier: "mod",   cmd: "note", sub: "add",   fields: [F.player, F.noteReq] },
      { key: "note_list",     label: "View staff notes",  emoji: "📑", tier: "mod",   cmd: "note", sub: "list",  fields: [F.player] },
      { key: "note_clear",    label: "Clear staff notes", emoji: "🗑️", tier: "admin", cmd: "note", sub: "clear", fields: [F.player] },
      { key: "stats",         label: "Player dossier",    emoji: "🪪", tier: "public", fields: [F.player] },
      { key: "seen",          label: "Last seen",         emoji: "👁️", tier: "public", fields: [F.player] },
    ],
  },
  economy: {
    label: "Economy & Caps", emoji: "💰", description: "Balances, gifts, transfers, wages",
    actions: [
      { key: "checkbalance",  label: "Check balance",     emoji: "💵", tier: "public", fields: [F.player] },
      { key: "givecaps",      label: "Give caps",         emoji: "💸", tier: "mod",   fields: [F.player, F.amount, F.reasonOpt] },
      { key: "transfercaps",  label: "Transfer caps",     emoji: "💱", tier: "admin", fields: [{ id: "from_id", label: "From courier ID", style: "short", required: true, max: 64 }, { id: "to_id", label: "To courier ID", style: "short", required: true, max: 64 }, F.amount] },
      { key: "adjustcaps",    label: "Adjust caps",       emoji: "⚙️", tier: "admin", fields: [F.player, F.amountSigned, F.reasonOpt] },
      { key: "addwage",       label: "Add wage / payout", emoji: "🪙", tier: "fl",    fields: [F.player, { id: "tier", label: "Tier: low_rank/mid_rank/high_rank/mercenary", style: "short", required: true, placeholder: "low_rank", max: 16 }] },
      { key: "removewage",    label: "Remove from payroll", emoji: "📤", tier: "fl",  fields: [F.player] },
      { key: "wagelist",      label: "View payroll",      emoji: "📃", tier: "public", fields: [] },
    ],
  },
  server: {
    label: "Server", emoji: "🖥️", description: "Status, players, broadcasts, map, raw RCON",
    actions: [
      { key: "ping",          label: "Ping / status",     emoji: "📡", tier: "public", fields: [] },
      { key: "listplayers",   label: "List players",      emoji: "👥", tier: "public", fields: [F.server] },
      { key: "serverinfo",    label: "Server info",       emoji: "🗺️", tier: "public", fields: [F.server] },
      { key: "find",          label: "Find player",       emoji: "🔍", tier: "public", fields: [{ id: "name", label: "Name (partial or full)", style: "short", required: true, max: 64 }] },
      { key: "announce",      label: "Announce",          emoji: "📢", tier: "mod",   fields: [{ id: "message", label: "Message (max 200)", style: "para", required: true, max: 200 }, F.server] },
      { key: "rotatemap",     label: "Rotate map",        emoji: "🔄", tier: "admin", fields: [F.server] },
      { key: "manual",        label: "Manual RCON",       emoji: "🛰️", tier: "admin", fields: [{ id: "command", label: "Raw RCON command", style: "short", required: true, max: 200 }, F.server] },
    ],
  },
  config: {
    label: "Admin / Config", emoji: "🔧", description: "Menu grants and donator file",
    actions: [
      { key: "givemenu",      label: "Grant menu",        emoji: "🎛️", tier: "admin", fields: [F.player, F.server, { id: "menu", label: "Menu: staff / faction", style: "short", required: true, placeholder: "staff", max: 16 }] },
      { key: "stripmenu",     label: "Revoke menu",       emoji: "🗑️", tier: "admin", fields: [F.player, F.server, { id: "menu", label: "Menu: staff / faction", style: "short", required: true, placeholder: "staff", max: 16 }] },
      { key: "donator_add",   label: "Add donator",       emoji: "💎", tier: "admin", cmd: "donator", sub: "add",    fields: [F.player] },
      { key: "donator_remove",label: "Remove donator",    emoji: "➖", tier: "admin", cmd: "donator", sub: "remove", fields: [F.player] },
      { key: "donator_list",  label: "List donators",     emoji: "📃", tier: "admin", cmd: "donator", sub: "list",   fields: [] },
    ],
  },
};

// Flat lookup: action key -> action (with its group key attached).
const ACTION_BY_KEY = {};
for (const [groupKey, group] of Object.entries(MENU_GROUPS)) {
  for (const a of group.actions) ACTION_BY_KEY[a.key] = { ...a, group: groupKey };
}

// Permission tiers for the native (non-menu) commands.
const NATIVE_TIERS = { help: "public", faction: "fl", setroles: "admin", blacklist: "admin" };

/* ---- permission + access helpers ---- */
function accessDenialEmbed(tier, member) {
  if (tier === "admin") return hasAdminRole(member) ? null : adminOnlyEmbed();
  if (tier === "fl")    return (hasModRole(member) || hasFactionLeaderRole(member)) ? null : factionLeaderOnlyEmbed();
  if (tier === "mod")   return hasModRole(member) ? null : modOnlyEmbed();
  return null; // public
}

function normalizeServer(raw) {
  const s = String(raw ?? "").trim().toLowerCase();
  if (!s || ["both", "all", "b"].includes(s)) return "both";
  if (["2", "s2", "server2", "server 2", "two"].includes(s)) return "server2";
  return "server1";
}

/* Bridges option reads so the same handler works for chat commands, modal
   submissions, and (input-less) select menus. */
function optionAccessor(interaction) {
  const isChat = interaction.isChatInputCommand && interaction.isChatInputCommand();
  if (isChat) {
    const op = interaction.options;
    return {
      getString:  (n) => op.getString(n),
      getInteger: (n) => op.getInteger(n),
      getRole:    (n) => op.getRole(n),
      getUser:    (n) => op.getUser(n),
      getSubcommand: () => op.getSubcommand(),
      getFocused: (x) => op.getFocused(x),
    };
  }
  // modal submit or select menu — read from text fields if present
  const read = (n) => {
    try { const v = interaction.fields?.getTextInputValue(n); return v == null || v === "" ? null : v; }
    catch { return null; }
  };
  return {
    getString:  (n) => (n === "server" ? normalizeServer(read(n)) : read(n)),
    getInteger: (n) => { const v = read(n); if (v == null) return null; const x = parseInt(v, 10); return Number.isNaN(x) ? null : x; },
    getRole:    () => null,
    getUser:    () => null,
    getSubcommand: () => interaction.__sub ?? null,
    getFocused: () => ({ name: "", value: "" }),
  };
}

/* ---- menu + modal builders ---- */
function buildGroupMenuMessage(member, groupKey) {
  const group = MENU_GROUPS[groupKey];
  const allowed = group.actions.filter(a => !accessDenialEmbed(a.tier, member));
  const embed = new EmbedBuilder().setColor(NV.AMBER)
    .setTitle(`${group.emoji}  ${group.label}`)
    .setDescription(`> *Choose an action from the menu below.*\n\n${DIVIDER}\n${group.description}`)
    .setFooter({ text: `${BOT_COPYRIGHT}` }).setTimestamp();
  if (!allowed.length) {
    embed.setColor(NV.DEAD_GREY).setDescription("You don't have access to any actions in this group.");
    return { embeds: [embed], ephemeral: true };
  }
  const select = new StringSelectMenuBuilder()
    .setCustomId(`grp:${groupKey}`)
    .setPlaceholder(`${group.label} — pick an action…`)
    .addOptions(allowed.slice(0, 25).map(a => ({
      label: a.label.slice(0, 100),
      value: a.key,
      emoji: a.emoji,
      description: `${a.tier === "public" ? "Everyone" : a.tier === "mod" ? "Moderator" : a.tier === "fl" ? "Faction leader" : "Admin"}${a.fields.length ? "" : " · runs immediately"}`.slice(0, 100),
    })));
  return { embeds: [embed], components: [new ActionRowBuilder().addComponents(select)], ephemeral: true };
}

function buildActionModal(action) {
  const modal = new ModalBuilder().setCustomId(`act:${action.key}`).setTitle(action.label.slice(0, 45));
  for (const f of action.fields.slice(0, 5)) {
    const input = new TextInputBuilder()
      .setCustomId(f.id)
      .setLabel(f.label.slice(0, 45))
      .setStyle(f.style === "para" ? TextInputStyle.Paragraph : TextInputStyle.Short)
      .setRequired(!!f.required);
    if (f.placeholder) input.setPlaceholder(f.placeholder);
    if (f.max) input.setMaxLength(f.max);
    modal.addComponents(new ActionRowBuilder().addComponents(input));
  }
  return modal;
}

const commands = [
  new SlashCommandBuilder().setName("help").setDescription("Show all commands and your current access level"),
  // Topic hub commands — each opens a dropdown of its actions (modals collect input)
  ...Object.entries(MENU_GROUPS).map(([key, g]) =>
    new SlashCommandBuilder().setName(key).setDescription(`${g.emoji} ${g.label} — ${g.description}`.slice(0, 100))),
  new SlashCommandBuilder().setName("blacklist")
    .setDescription("🔒 Admin — Bar a Discord user from using ALL bot commands")
    .addSubcommand(s => s.setName("add")
      .setDescription("Blacklist a Discord user from every command")
      .addUserOption(o => o.setName("user").setDescription("Discord user to blacklist").setRequired(true))
      .addStringOption(o => o.setName("reason").setDescription("Reason (shown to the user and logged)")))
    .addSubcommand(s => s.setName("remove")
      .setDescription("Remove a Discord user from the blacklist")
      .addUserOption(o => o.setName("user").setDescription("Discord user to un-blacklist").setRequired(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("View all blacklisted Discord users")),
  new SlashCommandBuilder().setName("setroles")
    .setDescription("🔒 Admin — Configure role permissions")
    .addRoleOption(o => o.setName("mod_role").setDescription("Moderator role"))
    .addRoleOption(o => o.setName("admin_role").setDescription("Admin role"))
    .addRoleOption(o => o.setName("faction_leader_role").setDescription("Faction Leader role")),

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
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices)))
    .addSubcommand(s => s.setName("rank")
      .setDescription("⚔️ Faction Leader — Set or change a member's rank within a faction")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("New rank to assign (faction-specific)").setRequired(true).setAutocomplete(true)))
    .addSubcommand(s => s.setName("transfer")
      .setDescription("🛡️ Mod — Transfer a player from one faction to another")
      .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
      .addStringOption(o => o.setName("from_faction").setDescription("Current faction").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("to_faction").setDescription("Destination faction").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Rank in new faction (default: lowest rank)").setAutocomplete(true)))
    .addSubcommand(s => s.setName("list")
      .setDescription("List all members of a faction with their ranks")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addIntegerOption(o => o.setName("page").setDescription("Page number (25 members per page, default: 1)").setMinValue(1)))
    .addSubcommand(s => s.setName("overview")
      .setDescription("Show all factions with member counts and officers at a glance"))
    .addSubcommand(s => s.setName("audit")
      .setDescription("View recent add/remove/rank changes for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addIntegerOption(o => o.setName("page").setDescription("Page number (15 entries per page, default: 1)").setMinValue(1)))
    .addSubcommand(s => s.setName("setcap")
      .setDescription("🔒 Admin — Set the maximum member cap for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addIntegerOption(o => o.setName("cap").setDescription("Maximum number of members (1–500)").setRequired(true).setMinValue(1).setMaxValue(500)))
    .addSubcommand(s => s.setName("setrankcap")
      .setDescription("🔒 Admin — Set the per-rank member cap within a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Rank to cap (faction-specific)").setRequired(true).setAutocomplete(true))
      .addIntegerOption(o => o.setName("cap").setDescription("Max members at this rank (0 = unlimited)").setRequired(true).setMinValue(0).setMaxValue(500))),
].map(c => c.toJSON());

/* ================================================================
   READY
   ================================================================ */
client.once("ready", async () => {
  logger.info("Bot", `${client.user.tag} online — v${BOT_VERSION}`);
  try {
    client.user.setPresence({
      activities: [{ name: "over the Mojave  ·  /help", type: ActivityType.Watching }],
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
  refreshPlayerCache("server1");
  refreshPlayerCache("server2");
  setTimeout(rconHealthCheck, 5_000);
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
      return interaction.reply({ embeds: [blacklistedEmbed(getBlacklistEntry(interaction.user.id))], ephemeral: true }).catch(() => {});
    }
    return;
  }

  /* ── Autocomplete ─────────────────────────────────────── */
  if (interaction.isAutocomplete()) {
    const o = interaction.options;
    const focused  = o.getFocused(true);
    const cmdName  = interaction.commandName;

    if (focused.name === "rank" && cmdName === "faction") {
      const faction = o.getString("faction");
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

    const server  = o.getString("server") ?? null;
    const query   = focused.value.toLowerCase();
    const choices = getPlayerChoices(server, query);
    if (query && !choices.find(c => c.value.toLowerCase() === query)) {
      choices.unshift({ name: `${focused.value} (manual entry)`, value: focused.value });
    }
    return interaction.respond(choices.slice(0, 25)).catch(() => {});
  }

  if (interaction.isChatInputCommand())  return handleChatCommand(interaction);
  if (interaction.isStringSelectMenu() && interaction.customId.startsWith("grp:")) return handleGroupSelect(interaction);
  if (interaction.isModalSubmit()       && interaction.customId.startsWith("act:")) return handleModalSubmit(interaction);
});

/* ================================================================
   COMMAND DISPATCH  (chat hubs → select menu → modal → runCommand)
   ================================================================ */
async function handleChatCommand(interaction) {
  const name = interaction.commandName;
  // Topic hub command → show its action dropdown (actions filtered by access)
  if (MENU_GROUPS[name]) {
    return interaction.reply(buildGroupMenuMessage(interaction.member, name));
  }
  // Native command (help, faction, setroles, blacklist)
  const tier   = NATIVE_TIERS[name] ?? "public";
  const denied = accessDenialEmbed(tier, interaction.member);
  if (denied) return interaction.reply({ embeds: [denied], ephemeral: true });
  if (tier === "fl" && !isOwner(interaction.user.id) && !checkRateLimit(interaction.user.id, name, 4000)) {
    return interaction.reply({ embeds: [rateLimitEmbed()], ephemeral: true });
  }
  return runCommand(interaction, name);
}

async function handleGroupSelect(interaction) {
  const action = ACTION_BY_KEY[interaction.values?.[0]];
  if (!action) return interaction.reply({ embeds: [errorEmbed("Unknown Action", "That action is no longer available.")], ephemeral: true });
  const denied = accessDenialEmbed(action.tier, interaction.member);
  if (denied) return interaction.reply({ embeds: [denied], ephemeral: true });
  if (action.fields && action.fields.length) {
    return interaction.showModal(buildActionModal(action)).catch(() => {});
  }
  interaction.__sub = action.sub ?? null;          // input-less action → run now
  return runCommand(interaction, action.cmd ?? action.key);
}

async function handleModalSubmit(interaction) {
  const action = ACTION_BY_KEY[interaction.customId.slice(4)];
  if (!action) return interaction.reply({ embeds: [errorEmbed("Unknown Action", "That action is no longer available.")], ephemeral: true });
  const denied = accessDenialEmbed(action.tier, interaction.member);
  if (denied) return interaction.reply({ embeds: [denied], ephemeral: true });
  interaction.__sub = action.sub ?? null;
  return runCommand(interaction, action.cmd ?? action.key);
}

async function runCommand(interaction, name) {
  const o = optionAccessor(interaction);
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
            `💡 *Commands are grouped by topic. Run a group command below, pick an action from the dropdown, and fill in the pop-up form.*`
          )
          .addFields(
            { name: "🗂️  Command Groups",
              value: [
                "📜  `/bans` — temp/permanent bans, hard bans, ban records",
                "🛡️  `/moderation` — kicks, warnings, notes, dossiers, last-seen",
                "💰  `/economy` — balances, gifts, transfers, wages",
                "🖥️  `/server` — status, players, broadcasts, map, raw RCON",
                "🔧  `/config` — menu grants & donator file",
                "*Each opens a dropdown; only actions you can use are shown.*",
              ].join("\n") },
            { name: "⚔️  Faction (native command)",
              value: [
                "`/faction add|remove|rank|transfer <id> <faction> [rank]`",
                "`/faction list|overview|audit <faction> [page]`",
                "`/faction setcap|setrankcap <faction> [rank] <cap>` *(admin)*",
                "*Kept native so faction & rank pickers + autocomplete still work.*",
              ].join("\n") },
            { name: "🔑  Other native commands",
              value: "`/help`  ·  `/setroles` *(admin — role pickers)*  ·  `/blacklist add|remove|list` *(admin — user picker)*" },
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
              ].join("\n") },
          )
          .setTimestamp().setFooter({ text: `Mojave Authority Bot  ·  ${BUILD_ID}  ·  ${BOT_COPYRIGHT}` });
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
        const color = (s1ok && s2ok) ? NV.IRRAD_GREEN : (s1ok || s2ok) ? NV.AMBER : NV.RUST_RED;
        const headline = (s1ok && s2ok) ? "All systems nominal — Securitron network active."
          : (s1ok || s2ok) ? "Partial connectivity — one server unreachable."
          : "⚠️  Both servers unreachable — check RCON config.";
        return interaction.editReply({ embeds: [
          new EmbedBuilder().setColor(color).setTitle("📡  System Status — Mojave Authority Bot")
            .setDescription(`> *${headline}*\n\n${DIVIDER}`)
            .addFields(
              { name: "🤖  Bot",       value: `🟢  Online\n\`${client.ws.ping}ms\``,                           inline: true },
              { name: "1️⃣  Server 1", value: s1ok ? "🟢  Reachable" : "🔴  Unreachable",                      inline: true },
              { name: "2️⃣  Server 2", value: s2ok ? "🟢  Reachable" : "🔴  Unreachable",                      inline: true },
              { name: "⚡  RTT",       value: `\`${rtt}ms\``,                                                   inline: true },
              { name: "⏱️  Uptime",    value: formatUptime(Date.now() - BOT_START_MS),                          inline: true },
              { name: "👥  Cached",    value: `S1: \`${playerCache.server1.length}\`  S2: \`${playerCache.server2.length}\``, inline: true },
              { name: "🔖  Build",     value: `\`${BUILD_ID}\``,                                                inline: true },
              { name: "💾  Mod Log",   value: `\`${loadModLog().length}\` entries`,                             inline: true },
              { name: "⚠️  Open Bans", value: `\`${loadBans().length}\` active`,                               inline: true },
            ).setTimestamp().setFooter({ text: `${BOT_COPYRIGHT}  ·  authored by ${BOT_AUTHOR}` })
        ]});
      }

      /* ─────────────────────────────────────────────────────
         LISTPLAYERS
         ───────────────────────────────────────────────────── */
      case "listplayers": {
        const server = o.getString("server");
        await interaction.deferReply();
        if (server === "both") {
          const [r1, r2] = await Promise.all([sendRcon("RefreshList", "server1"), sendRcon("RefreshList", "server2")]);
          setPlayerCacheFromData("server1", parseRcon(r1));
          setPlayerCacheFromData("server2", parseRcon(r2));
          return interaction.editReply({ embeds: [buildPlayerListEmbed(r1, "server1"), buildPlayerListEmbed(r2, "server2")] });
        }
        const result = await sendRcon("RefreshList", server);
        setPlayerCacheFromData(server, parseRcon(result));
        return interaction.editReply({ embeds: [buildPlayerListEmbed(result, server)] });
      }

      /* ─────────────────────────────────────────────────────
         SERVERINFO
         ───────────────────────────────────────────────────── */
      case "serverinfo": {
        const server = o.getString("server");
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
          return new EmbedBuilder()
            .setColor(info.ok ? NV.IRRAD_GREEN : NV.RUST_RED)
            .setTitle(`${serverEmoji(srv)}  ${info.serverName}`)
            .addFields(
              { name: "🗺️  Map",     value: info.mapLabel,                          inline: true },
              { name: "🎮  Mode",    value: info.gameMode,                          inline: true },
              { name: "👥  Players", value: `${info.players} / ${info.maxPlayers}`, inline: true },
              { name: "📡  Status",  value: info.ok ? "🟢  Online" : "🔴  Offline", inline: true },
            )
            .setTimestamp().setFooter({ text: `${serverLabel(srv)} · live data` });
        });
        return interaction.editReply({ embeds });
      }

      /* ─────────────────────────────────────────────────────
         FIND
         ───────────────────────────────────────────────────── */
      case "find": {
        const query = o.getString("name").toLowerCase();
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
            new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("🔍  No Matches Found")
              .setDescription(`No couriers matching **"${query}"** are online on either server.\n\n*Try a shorter search term, or check \`/listplayers\` for the full list.*`)
              .setTimestamp()
          ]});
        }
        const lines = matches.map((m) => {
          const srvStr = m.servers.map(s => s === "server1" ? "S1" : "S2").join("+");
          const warn   = loadWarns()[m.name.toLowerCase()]?.length ?? 0;
          const hb     = findHardBanByAnyId(m.name) ? " 🔨" : "";
          return `\`[${srvStr}]\`  **${m.name}**${hb}${warn ? `  ·  ⚠️ ${warn} warn${warn !== 1 ? "s" : ""}` : ""}`;
        });
        return interaction.editReply({ embeds: [
          new EmbedBuilder().setColor(NV.AMBER).setTitle(`🔍  Search Results — "${query}"`)
            .setDescription(lines.join("\n"))
            .setFooter({ text: `${matches.length} match${matches.length !== 1 ? "es" : ""} · 🔨 = hard banned  ·  ⚠️ = warnings on record` })
            .setTimestamp()
        ]});
      }

      /* ─────────────────────────────────────────────────────
         KICK  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "kick": {
        const playerId = sanitizeId(o.getString("playerid"));
        const server   = o.getString("server");
        const reason   = o.getString("reason") ?? "No reason provided";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        await sendRconBoth(`Kick ${playerId}`, server);
        writeModLog({ action: "kick", playerId, reason, by: interaction.user.tag, server });
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("👢  Courier Ejected from the Strip")
          .setDescription(`> *"Get out. Don't make us ask twice."*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier", value: `\`${playerId}\``,                                  inline: true },
            { name: "🖥️  Server",  value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "🛡️  By",      value: `${interaction.user}`,                              inline: true },
            { name: "📋  Reason",  value: reason,                                             inline: false },
          ).setFooter({ text: "Kick logged — no ban issued" }).setTimestamp();
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         WARN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "warn": {
        const playerId  = sanitizeId(o.getString("playerid"));
        const reasonKey = o.getString("reason");
        const server    = o.getString("server") ?? "server1";
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
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         WARNINGS
         ───────────────────────────────────────────────────── */
      case "warnings": {
        const playerId = sanitizeId(o.getString("playerid"));
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
        return interaction.reply({ embeds: [
          new EmbedBuilder().setColor(count >= 5 ? NV.RUST_RED : NV.NCR_TAN)
            .setTitle(`⚠️  Warning Record — ${playerId}`)
            .setDescription(
              `**${count}** warning${count !== 1 ? "s" : ""} on record\n` +
              (next ? `Next escalation at **${next.count}** warnings: *${next.label}*` : "**⛔  Maximum threshold exceeded — perm ban eligible**") +
              `\n\n${DIVIDER}\n` + lines.join("\n")
            ).setTimestamp()
        ], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         CLEARWARNINGS
         ───────────────────────────────────────────────────── */
      case "clearwarnings": {
        const playerId = sanitizeId(o.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const warns = loadWarns();
        const key   = playerId.toLowerCase();
        const count = warns[key]?.length ?? 0;
        if (!count) {
          return interaction.reply({ embeds: [warningEmbed("No Warnings", `\`${playerId}\` has no warnings to clear.`)], ephemeral: true });
        }
        delete warns[key];
        saveWarns(warns);
        writeModLog({ action: "clearwarnings", playerId, count, by: interaction.user.tag });
        const embed = successEmbed("Warnings Cleared", `**${count}** warning${count !== 1 ? "s" : ""} cleared for \`${playerId}\`.\n\n**Cleared by:** ${interaction.user}`);
        await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         DELWARN  (remove one warning by number)
         ───────────────────────────────────────────────────── */
      case "delwarn": {
        const playerId = sanitizeId(o.getString("playerid"));
        const number   = o.getInteger("number");
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
        await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         SEEN  (last time a courier was online)
         ───────────────────────────────────────────────────── */
      case "seen": {
        const playerId = sanitizeId(o.getString("playerid"));
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
        const sub      = o.getSubcommand();
        const playerId = sanitizeId(o.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });

        if (sub === "add") {
          const text  = o.getString("note").trim().slice(0, 500);
          if (!text) return interaction.reply({ embeds: [errorEmbed("Empty Note", "The note cannot be empty.")], ephemeral: true });
          const count = await addPlayerNote(playerId, text, interaction.user.tag);
          writeModLog({ action: "note-add", playerId, reason: text, by: interaction.user.tag });
          const embed = successEmbed("Note Added", `Staff note added to \`${playerId}\` *(now ${count} note${count !== 1 ? "s" : ""})*.\n\n**Note:** ${text}\n**By:** ${interaction.user}`);
          await logAction(embed);
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
          await logAction(embed);
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
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle(`📝  Staff Notes — ${playerId}`)
          .setDescription(`**${notes.length}** note${notes.length !== 1 ? "s" : ""} on record\n\n${DIVIDER}`);
        for (const f of chunkFields(lines, "Notes")) embed.addFields(f);
        embed.setFooter({ text: "Staff notes · internal only" }).setTimestamp();
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         HISTORY
         ───────────────────────────────────────────────────── */
      case "history": {
        const playerId = sanitizeId(o.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const history = getPlayerHistory(playerId);
        if (!history.length) {
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("📋  No Mod History Found")
              .setDescription(`\`${playerId}\` has no moderation history on record.`).setTimestamp()
          ], ephemeral: true });
        }
        const ICONS = { kick: "👢", warn: "⚠️", tempban: "⏳", unban: "🔓", permban: "💀", hardban: "🔨", "auto-unban": "⏰", "auto-tempban": "🤖", "auto-permban": "🤖", clearwarnings: "🧹", delwarn: "🧹", "note-add": "📝", "note-clear": "🗑️", "donator-add": "💎", "donator-remove": "💎", "wage-payout": "💰", givecaps: "💸", adjustcaps: "⚙️", "faction-add": "⚔️", "faction-remove": "🚪", "faction-rank": "🎖️", "faction-transfer": "↔️" };
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
         TEMPBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "tempban": {
        const playerId    = sanitizeId(o.getString("playerid"));
        const durationKey = o.getString("duration");
        const server      = o.getString("server");
        const reasonKey   = o.getString("reason");
        const reason      = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        const { ms, label } = BAN_DURATIONS[durationKey];
        const expires        = Date.now() + ms;
        const existing       = loadBans();
        const replaced       = existing.find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        const newBans        = existing.filter(b => b.playerId.toLowerCase() !== playerId.toLowerCase());
        newBans.push({ playerId, reason, expires, durationLabel: label, moderator: interaction.user.tag, server });
        await sendRconBoth(`Ban ${playerId}`, server);
        saveBans(newBans);
        writeModLog({ action: "tempban", playerId, reason, duration: label, by: interaction.user.tag, server });
        const ts = Math.floor(expires / 1000);
        const embed = new EmbedBuilder().setColor(NV.RUST_RED).setTitle("⏳  Courier Exiled from the Mojave")
          .setDescription(`> *${randomQuote("ban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",  value: `\`${playerId}\``,                                  inline: true },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "⏱️  Duration", value: `**${label}**`,                                     inline: true },
            { name: "⚖️  Offense",  value: reason,                                             inline: false },
            { name: "🔓  Expires",  value: `<t:${ts}:F>  ·  <t:${ts}:R>`,                    inline: true },
            { name: "🛡️  By",       value: `${interaction.user}`,                              inline: true },
          )
          .setFooter({ text: replaced ? `Replaced earlier exile: ${replaced.reason}` : "Auto-lifted when timer expires" })
          .setTimestamp();
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         UNBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "unban": {
        const playerId = sanitizeId(o.getString("playerid"));
        const server   = o.getString("server");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        await sendRconBoth(`Unban ${playerId}`, server);
        const before  = loadBans().length;
        const newBans = loadBans().filter(b => b.playerId.toLowerCase() !== playerId.toLowerCase());
        saveBans(newBans);
        writeModLog({ action: "unban", playerId, by: interaction.user.tag, server });
        const removed = before !== newBans.length;
        const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("🔓  Exile Lifted — Welcome Back to the Strip")
          .setDescription(`> *${randomQuote("unban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",     value: `\`${playerId}\``,   inline: true },
            { name: "🖥️  Server",      value: serverLabel(server), inline: true },
            { name: "🛡️  Pardoned By", value: `${interaction.user}`, inline: true },
            { name: "📋  Record",       value: removed ? "✅  Temp ban record cleared." : "ℹ️  No temp ban record — RCON Unban sent.", inline: false },
          ).setFooter({ text: randomQuote("unban") }).setTimestamp();
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         CHECKBAN
         ───────────────────────────────────────────────────── */
      case "checkban": {
        const playerId = sanitizeId(o.getString("playerid"));
        const server   = o.getString("server");
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const hb = findHardBanByAnyId(playerId);
        if (hb) {
          const ts     = Math.floor(hb.bannedAt / 1000);
          const notes  = loadNotes()[hb.primaryId];
          const noteStr = notes?.length ? notes.map((n, i) => `\`${i + 1}.\`  ${n.text}  *(${n.by})*`).join("\n") : null;
          const embed = new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("🔨  Hard Ban — Repeat Offender Registry")
            .setDescription(`> *"Not welcome under any name, on any server."*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  Queried ID",  value: `\`${playerId}\``,                                                                       inline: true },
              { name: "📁  Record",      value: hb.primaryId.toLowerCase() === playerId.toLowerCase() ? "Primary account" : `Alt of \`${hb.primaryId}\``, inline: true },
              { name: "⚖️  Offense",     value: hb.reason,                                                                                inline: false },
              { name: "🔗  Known Alts",  value: hb.linkedIds.length ? hb.linkedIds.map(id => `\`${id}\``).join("  ·  ") : "*none*",      inline: false },
              { name: "📅  Banned",      value: `<t:${ts}:F> by **${hb.bannedBy}**`,                                                      inline: false },
            ).setFooter({ text: "Hard ban · permanent · all servers" }).setTimestamp();
          if (noteStr) embed.addFields({ name: "📝  Staff Notes", value: noteStr });
          return interaction.reply({ embeds: [embed] });
        }
        const tb = loadBans().find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        if (tb) {
          const ts = Math.floor(tb.expires / 1000);
          return interaction.reply({ embeds: [
            new EmbedBuilder().setColor(NV.RUST_RED).setTitle("⏳  Temporary Exile Active")
              .setDescription(`${DIVIDER}`)
              .addFields(
                { name: "🎯  Courier",   value: `\`${playerId}\``,                  inline: true },
                { name: "🖥️  Server",    value: serverLabel(server),                inline: true },
                { name: "⏱️  Duration",  value: tb.durationLabel ?? "?",            inline: true },
                { name: "⚖️  Offense",   value: tb.reason,                          inline: false },
                { name: "🛡️  By",        value: tb.moderator,                       inline: true },
                { name: "⏰  Remaining", value: `**${formatTimeLeft(tb.expires)}**`, inline: true },
                { name: "🔓  Expires",   value: `<t:${ts}:F>  ·  <t:${ts}:R>`,    inline: false },
              ).setFooter({ text: "Auto-lifted when timer expires" }).setTimestamp()
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
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("✅  No Exile Found")
              .setDescription(`\`${playerId}\` is free — no active exiles on any server.\n\n*No temp bans, hard bans, or permanent bans detected.*`)
              .setTimestamp()
          ]});
        }
        return interaction.editReply({ embeds: [
          new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("💀  Permanent Exile Active")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "🎯  Courier",   value: `\`${playerId}\``,                                                              inline: true },
              { name: "🖥️  Banned On", value: [b1 && "**Server 1**", b2 && "**Server 2**"].filter(Boolean).join("  +  "),  inline: true },
            ).setFooter({ text: "Permanent exile — use /unban to lift" }).setTimestamp()
        ]});
      }

      /* ─────────────────────────────────────────────────────
         BANLIST
         ───────────────────────────────────────────────────── */
      case "banlist": {
        const server = o.getString("server");
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
            new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("✅  Exile Registry Clear")
              .setDescription('> *"The Mojave is peaceful — for now."*\n\nNo active exiles on any server.').setTimestamp()
          ]});
        }
        // Flatten every exile into a single tagged line so long lists paginate
        // cleanly instead of being truncated at Discord's 4096-char limit.
        const lines = [
          ...tempBans.map(b => `⏳  \`${b.playerId}\`  —  expires <t:${Math.floor(b.expires / 1000)}:R>  ·  *${b.reason}*`),
          ...pb1.map(b => `💀  \`${extractId(b)}\`  ·  *Permanent · S1*`),
          ...pb2.map(b => `💀  \`${extractId(b)}\`  ·  *Permanent · S2*`),
        ];
        const total = lines.length;
        const header = `> *"The Strip keeps its records."*\n\n${DIVIDER}\n**${total}** active exile${total !== 1 ? "s" : ""}  ·  ⏳ ${tempBans.length} temp  ·  💀 ${pb1.length + pb2.length} permanent`;
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.LEGION_RED).setTitle(`📜  Exile Registry — ${serverLabel(server)}`)
            .setDescription(`${header}\n${DIVIDER}\n${pageLines.join("\n")}`)
            .setFooter({ text: `${total} exile${total !== 1 ? "s" : ""} active` }).setTimestamp(),
          { perPage: 15 });
      }

      /* ─────────────────────────────────────────────────────
         PERMBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "permban": {
        const playerId  = sanitizeId(o.getString("playerid"));
        const server    = o.getString("server");
        const reasonKey = o.getString("reason");
        const notes     = o.getString("notes") ?? null;
        const reason    = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                          // ← ADDED
        await sendRconBoth(`Ban ${playerId}`, server);
        saveBans(loadBans().filter(b => b.playerId.toLowerCase() !== playerId.toLowerCase()));
        writeModLog({ action: "permban", playerId, reason, by: interaction.user.tag, server });
        const embed = new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("💀  Permanent Exile Issued")
          .setDescription(`> *${randomQuote("ban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",  value: `\`${playerId}\``,                                  inline: true },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "⏱️  Sentence", value: "**Permanent**",                                    inline: true },
            { name: "⚖️  Offense",  value: reason,                                             inline: false },
            { name: "🔒  Admin",    value: `${interaction.user}`,                              inline: false },
          ).setFooter({ text: randomQuote("ban") }).setTimestamp();
        if (notes) embed.addFields({ name: "📝  Notes", value: notes });
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });      // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         HARDBAN  ← deferReply added (after all non-RCON early returns)
         ───────────────────────────────────────────────────── */
      case "hardban": {
        const playerId  = sanitizeId(o.getString("playerid"));
        const server    = o.getString("server");
        const reasonKey = o.getString("reason");
        const linkedId  = sanitizeId(o.getString("linked_id") ?? "");
        const notes     = o.getString("notes") ?? null;
        const reason    = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        const registry = loadHardBans();
        const existing = registry.find(e =>
          e.primaryId.toLowerCase() === playerId.toLowerCase() ||
          e.linkedIds.some(l => l.toLowerCase() === playerId.toLowerCase())
        );
        // This branch does no RCON — keep as plain reply
        if (existing && !linkedId && !notes) {
          return interaction.reply({ embeds: [warningEmbed("Already Hard Banned",
            `\`${playerId}\` is already in the registry under \`${existing.primaryId}\`.\n\n**Known alts:** ${existing.linkedIds.map(id => `\`${id}\``).join("  ·  ") || "*none*"}\n\nTo add an alt, re-run with \`linked_id\`. To add a note, use \`/addnote\`.`
          )], ephemeral: true });
        }
        // Past here we always hit RCON — defer now
        await interaction.deferReply();                         // ← ADDED
        if (existing) {
          if (linkedId && !existing.linkedIds.map(l => l.toLowerCase()).includes(linkedId.toLowerCase())) existing.linkedIds.push(linkedId);
          if (notes) {
            const ns = loadNotes();
            if (!ns[existing.primaryId]) ns[existing.primaryId] = [];
            ns[existing.primaryId].push({ text: notes, by: interaction.user.tag, at: Date.now() });
            saveNotes(ns);
          }
          existing.updatedAt = Date.now(); existing.updatedBy = interaction.user.tag;
          saveHardBans(registry);
          await sendRconBoth(`Ban ${playerId}`, server);
          if (linkedId) await sendRconBoth(`Ban ${linkedId}`, server);
          saveBans(loadBans().filter(b => {
            const id = b.playerId.toLowerCase();
            return id !== playerId.toLowerCase() && (!linkedId || id !== linkedId.toLowerCase());
          }));
          writeModLog({ action: "hardban-update", playerId, linkedId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("🔨  Hard Ban Record Updated").setDescription(`${DIVIDER}`)
            .addFields(
              { name: "🎯  Primary ID",    value: `\`${existing.primaryId}\``,                                           inline: true },
              { name: "🔗  New Alt",       value: linkedId ? `\`${linkedId}\`` : "*none added*",                         inline: true },
              { name: "📋  All Known Alts",value: existing.linkedIds.map(id => `\`${id}\``).join("  ·  ") || "*none*",  inline: false },
              { name: "🔒  Updated By",    value: `${interaction.user}`,                                                 inline: false },
            ).setFooter({ text: "Hard ban registry updated" }).setTimestamp();
          if (notes) embed.addFields({ name: "📝  Note Added", value: notes });
          await logAction(embed);
          return interaction.editReply({ embeds: [embed] });   // ← CHANGED
        }
        registry.push({ primaryId: playerId, linkedIds: linkedId ? [linkedId] : [], reason, server: serverLabel(server), bannedBy: interaction.user.tag, bannedAt: Date.now(), updatedAt: null, updatedBy: null });
        saveHardBans(registry);
        if (notes) { const ns = loadNotes(); ns[playerId] = [{ text: notes, by: interaction.user.tag, at: Date.now() }]; saveNotes(ns); }
        await sendRconBoth(`Ban ${playerId}`, server);
        if (linkedId) await sendRconBoth(`Ban ${linkedId}`, server);
        saveBans(loadBans().filter(b => b.playerId !== playerId && b.playerId !== linkedId));
        writeModLog({ action: "hardban", playerId, linkedId, reason, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("🔨  Hard Ban Issued — Persona Non Grata")
          .setDescription(`> *${randomQuote("hardban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "🎯  Courier",  value: `\`${playerId}\``,                                  inline: true },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "⏱️  Sentence", value: "**Permanent · Hard Ban**",                         inline: true },
            { name: "⚖️  Offense",  value: reason,                                             inline: false },
          );
        if (linkedId) embed.addFields({ name: "🔗  Linked Account", value: `\`${linkedId}\`  — also banned and linked`, inline: false });
        if (notes)    embed.addFields({ name: "📝  Notes", value: notes, inline: false });
        embed.addFields({ name: "🔒  Admin", value: `${interaction.user}`, inline: false }).setFooter({ text: randomQuote("hardban") }).setTimestamp();
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });     // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         ADDNOTE
         ───────────────────────────────────────────────────── */
      case "addnote": {
        const playerId = sanitizeId(o.getString("playerid"));
        const note     = o.getString("note").trim();
        const hb = findHardBanByAnyId(playerId);
        if (!hb) return interaction.reply({ embeds: [errorEmbed("Not in Registry", `\`${playerId}\` has no hard ban record. Use \`/hardban\` to add them first.`)], ephemeral: true });
        const ns = loadNotes();
        if (!ns[hb.primaryId]) ns[hb.primaryId] = [];
        ns[hb.primaryId].push({ text: note, by: interaction.user.tag, at: Date.now() });
        saveNotes(ns);
        const embed = successEmbed("Note Appended", `Note added to \`${hb.primaryId}\`'s hard ban record.\n\n**Note:** ${note}\n**Added by:** ${interaction.user}`);
        await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         HARDBANLIST
         ───────────────────────────────────────────────────── */
      case "hardbanlist": {
        const registry = loadHardBans(), notes = loadNotes();
        if (!registry.length) {
          return interaction.reply({ embeds: [new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("🔨  Hard Ban Registry").setDescription("> *\"No repeat offenders on record.\"*\n\nThe registry is clear.").setTimestamp()], ephemeral: true });
        }
        const lines = registry.map((e, i) => {
          const ts  = Math.floor(e.bannedAt / 1000);
          const nn  = notes[e.primaryId];
          return [
            `\`${String(i + 1).padStart(2, "0")}\`  **${e.primaryId}**  —  *${e.reason}*`,
            e.linkedIds.length ? `↳  Alts: ${e.linkedIds.map(id => `\`${id}\``).join("  ·  ")}` : "↳  *no alts*",
            nn?.length ? `↳  📝  ${nn.length} note${nn.length !== 1 ? "s" : ""}` : "",
            `↳  Banned <t:${ts}:R> by **${e.bannedBy}**`,
          ].filter(Boolean).join("\n");
        });
        const header = `> *${randomQuote("hardban")}*\n\n${DIVIDER}\n**${registry.length}** flagged  ·  *Use \`/addnote <id>\` to append notes.*`;
        return paginate(interaction, lines, (pageLines) =>
          new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("🔨  Hard Ban Registry — Persona Non Grata")
            .setDescription(`${header}\n${DIVIDER}\n${pageLines.join("\n\n")}`)
            .setFooter({ text: "Hard ban registry · admin only" }).setTimestamp(),
          { perPage: 6, ephemeral: true });
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
          embeds: [warningEmbed("Confirm Mass Clearance",
            `> *"Are you sure? Every exile gets pardoned."*\n\n${DIVIDER}\n` +
            `This will lift **${bans.length}** exile${bans.length !== 1 ? "s" : ""} and unban all on both servers.\n\n` +
            bans.map(b => `·  \`${b.playerId}\`  —  *${b.reason}*`).join("\n")
          ).setFooter({ text: "Expires in 30 seconds" })],
          components: [row], ephemeral: true, fetchReply: true,
        });
        try {
          const btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: 30_000, filter: i => i.user.id === interaction.user.id });
          if (btn.customId === "ctb_cancel") {
            return btn.update({ embeds: [new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("🪖  Stand Down").setDescription("Clearance cancelled — all exiles remain active.").setTimestamp()], components: [] });
          }
          await btn.deferUpdate();
          const ok = [], fail = [];
          for (const ban of bans) {
            try { await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server1"); await sendRcon(`Unban ${sanitizeId(ban.playerId)}`, "server2"); ok.push(ban.playerId); }
            catch { fail.push(ban.playerId); }
          }
          saveBans(bans.filter(b => fail.includes(b.playerId)));
          writeModLog({ action: "cleartempbans", count: ok.length, by: interaction.user.tag });
          const lines = [...ok.map(id => `✅  \`${id}\``), ...fail.map(id => `☢️  \`${id}\`  — failed, kept on record`)];
          const embed = new EmbedBuilder().setColor(NV.AMBER).setTitle("🧹  Temp Bans Cleared")
            .setDescription(`> *"Clean slate."*\n\n${DIVIDER}\n**${ok.length}** released${fail.length ? `  ·  **${fail.length}** failed` : ""}\n\n${lines.join("\n")}`)
            .addFields({ name: "🔒  By", value: `${interaction.user}`, inline: false }).setTimestamp();
          await logAction(embed);
          return btn.editReply({ embeds: [embed], components: [] });
        } catch {
          return interaction.editReply({ embeds: [warningEmbed("Timed Out", "Confirmation expired. No changes made.")], components: [] });
        }
      }

      /* ─────────────────────────────────────────────────────
         SETROLES
         ───────────────────────────────────────────────────── */
      case "setroles": {
        const modRole   = o.getRole("mod_role");
        const adminRole = o.getRole("admin_role");
        const flRole    = o.getRole("faction_leader_role");
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
        await logAction(embed);
        return interaction.reply({ embeds: [embed], ephemeral: true });
      }

      /* ─────────────────────────────────────────────────────
         BLACKLIST  (admin — bar a Discord user from all commands)
         ───────────────────────────────────────────────────── */
      case "blacklist": {
        const sub = o.getSubcommand();

        if (sub === "list") {
          const bl      = loadBlacklist();
          const entries = Object.entries(bl);
          if (!entries.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("⛔  Blacklist — Empty")
                .setDescription("No Discord users are currently blacklisted.").setTimestamp()
            ], ephemeral: true });
          }
          const lines = entries
            .sort((a, b) => (b[1].at ?? 0) - (a[1].at ?? 0))
            .map(([uid, e], i) => {
              const ts = e.at ? `  ·  <t:${Math.floor(e.at / 1000)}:R>` : "";
              return `\`${String(i + 1).padStart(2, "0")}\`  <@${uid}> \`${uid}\`${e.reason ? `  —  *${e.reason}*` : ""}  ·  by **${e.by ?? "?"}**${ts}`;
            });
          const embed = new EmbedBuilder().setColor(NV.LEGION_RED)
            .setTitle(`⛔  Command Blacklist — ${entries.length} user${entries.length !== 1 ? "s" : ""}`)
            .setDescription(`> *"Names in the little black book don't get served."*\n\n${DIVIDER}`);
          for (const f of chunkFields(lines, "Blacklisted Users")) embed.addFields(f);
          embed.setFooter({ text: "Blacklisted users are barred from every bot command." }).setTimestamp();
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        const target = o.getUser("user");

        if (sub === "add") {
          const reason = o.getString("reason")?.trim() || null;
          if (target.id === interaction.user.id) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Target", "You can't blacklist yourself.")], ephemeral: true });
          }
          if (target.id === client.user.id) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Target", "You can't blacklist the bot.")], ephemeral: true });
          }
          if (isOwner(target.id)) {
            return interaction.reply({ embeds: [errorEmbed("Invalid Target", "That user is a hardcoded owner and cannot be blacklisted.")], ephemeral: true });
          }
          const bl = loadBlacklist();
          if (bl[target.id]) {
            return interaction.reply({ embeds: [warningEmbed("Already Blacklisted", `<@${target.id}> is already blacklisted.\n\nUse \`/blacklist remove\` to lift it.`)], ephemeral: true });
          }
          bl[target.id] = { reason, by: interaction.user.tag, at: Date.now() };
          saveBlacklist(bl);
          writeModLog({ action: "blacklist-add", targetUserId: target.id, reason, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("⛔  User Blacklisted")
            .setDescription(`> *"Consider their access revoked. Every command, every server."*\n\n${DIVIDER}`)
            .addFields(
              { name: "🎯  User",   value: `<@${target.id}>  \`${target.id}\``, inline: false },
              { name: "📋  Reason", value: reason ?? "*No reason provided*",     inline: false },
              { name: "🔒  By",     value: `${interaction.user}`,                inline: false },
            ).setFooter({ text: "User can no longer use any bot command." }).setTimestamp();
          await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        if (sub === "remove") {
          const bl = loadBlacklist();
          if (!bl[target.id]) {
            return interaction.reply({ embeds: [warningEmbed("Not Blacklisted", `<@${target.id}> is not on the blacklist.`)], ephemeral: true });
          }
          delete bl[target.id];
          saveBlacklist(bl);
          writeModLog({ action: "blacklist-remove", targetUserId: target.id, by: interaction.user.tag });
          const embed = successEmbed("Blacklist Lifted", `<@${target.id}> can use bot commands again.\n\n**Lifted by:** ${interaction.user}`);
          await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        break;
      }

      /* ─────────────────────────────────────────────────────
         DONATOR  (admin — manage the donator whitelist file)
         ───────────────────────────────────────────────────── */
      case "donator": {
        const sub = o.getSubcommand();

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
          const embed = new EmbedBuilder().setColor(NV.GOLD)
            .setTitle(`💎  Donators — ${lines.length}`)
            .setDescription(`> *"The House remembers its most generous patrons."*\n\n${DIVIDER}`);
          for (const f of chunkFields(out, "Donators")) embed.addFields(f);
          embed.setFooter({ text: DONATOR_FILE }).setTimestamp();
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        const playerId = sanitizeId(o.getString("playerid"));
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
            ).setFooter({ text: "Written to the donator file." }).setTimestamp();
          await logAction(embed);
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
          await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        break;
      }

      /* ─────────────────────────────────────────────────────
         ANNOUNCE
         ───────────────────────────────────────────────────── */
      case "announce": {
        const message = sanitizeMessage(o.getString("message"));
        const server  = o.getString("server");
        if (!message.trim()) return interaction.reply({ embeds: [errorEmbed("Empty Message", "Cannot broadcast an empty message.")], ephemeral: true });
        await interaction.deferReply();
        const { s1, s2 } = await sendRconBoth(`Notify ${message}`, server);
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
        writeModLog({ action: "announce", message, by: interaction.user.tag, server, delivered: allOk });
        const deliveryNote = allOk
          ? "✅  Sent via RCON `Notify` — visible in-game if your build supports it."
          : anyOk
            ? "⚠️  One server may not support `Notify`. Message logged here regardless."
            : "⚠️  Server gave no acknowledgement — your Pavlov build may not support `Notify`. Message logged here only.";
        const embed = new EmbedBuilder().setColor(allOk ? NV.BLUE_VATS : NV.NCR_TAN).setTitle("📢  Broadcast Sent")
          .setDescription(`> *${randomQuote("announce")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "📣  Message",  value: `> ${message}`,                                     inline: false },
            { name: "🖥️  Server",   value: `${serverEmoji(server)}  ${serverLabel(server)}`,   inline: true },
            { name: "🛡️  By",       value: `${interaction.user}`,                              inline: true },
            { name: "📡  Delivery", value: deliveryNote,                                       inline: false },
          ).setFooter({ text: "RCON Notify broadcast" }).setTimestamp();
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         GIVEMENU / STRIPMENU  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "givemenu":
      case "stripmenu": {
        const playerId  = sanitizeId(o.getString("playerid"));
        const server    = o.getString("server");
        const menuValue = o.getString("menu");
        const menuMeta  = MENUS.find(m => m.value === menuValue);
        const menuId    = menuMeta?.menuId ?? menuValue;
        const isGive    = name === "givemenu";
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        await interaction.deferReply();                         // ← ADDED
        await sendRconBoth(`${isGive ? "GiveMenu" : "RemoveMenu"} ${playerId} ${menuId}`, server);
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
        await logAction(embed);
        return interaction.editReply({ embeds: [embed] });     // ← CHANGED
      }

      /* ─────────────────────────────────────────────────────
         FACTION — all subcommands
         ───────────────────────────────────────────────────── */
      case "faction": {
        const sub = o.getSubcommand();

        /* ── setcap (admin only) ── */
        if (sub === "setcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], ephemeral: true });
          }
          const faction = o.getString("faction");
          const cap     = o.getInteger("cap");
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
          await logAction(embed);
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        /* ── setrankcap (admin only) ── */
        if (sub === "setrankcap") {
          if (!hasAdminRole(interaction.member)) {
            return interaction.reply({ embeds: [adminOnlyEmbed()], ephemeral: true });
          }
          const faction = o.getString("faction");
          const rank    = o.getString("rank");
          const cap     = o.getInteger("cap");
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
          await logAction(embed);
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
          const faction  = o.getString("faction");
          const page     = Math.max(1, o.getInteger("page") ?? 1);
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
          const PAGE_SIZE  = 25;
          const totalPages = Math.ceil(members.length / PAGE_SIZE);
          const safePage   = Math.min(page, totalPages);
          const slice      = members.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE);
          const cap        = getFactionCap(faction);
          const lines = slice.map((m, i) => {
            const globalIdx = (safePage - 1) * PAGE_SIZE + i + 1;
            return `\`${String(globalIdx).padStart(2, "0")}\`  ${getFactionRankBadge(faction, m.rank)}  **${m.playerId}**  ·  *${m.rank}*`;
          });
          const rankOrder = getFactionRankOrder(faction).slice().reverse();
          const summary   = rankOrder.map(r => {
            const n = members.filter(m => m.rank === r).length;
            const rcap = getFactionRankCap(faction, r);
            if (!n && !rcap) return null;
            const count = rcap ? `${n}/${rcap}${n > rcap ? "⚠️" : ""}` : `${n}`;
            return `${getFactionRankBadge(faction, r)} ${r}: **${count}**`;
          }).filter(Boolean).join("  ·  ");
          const embed = new EmbedBuilder().setColor(NV.GOLD)
            .setTitle(`⚔️  ${faction} — Roster (Page ${safePage}/${totalPages})`)
            .setDescription(
              `**${members.length}/${cap}** members${members.length > cap ? " ⚠️ over cap" : ""}  ·  ${summary}\n\n${DIVIDER}\n` +
              lines.join("\n")
            )
            .setFooter({ text: `${SPAWN_FILE_MAP[faction]}  ·  Page ${safePage} of ${totalPages}  ·  ${PAGE_SIZE} per page` })
            .setTimestamp();
          return interaction.reply({ embeds: [embed] });
        }

        /* ── audit (public, paginated) ── */
        if (sub === "audit") {
          const faction   = o.getString("faction");
          const page      = Math.max(1, o.getInteger("page") ?? 1);
          const allAudit  = loadFactionAudit().filter(e => e.faction === faction).reverse();
          if (!allAudit.length) {
            return interaction.reply({ embeds: [
              new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle(`📋  ${faction} — Audit Log`)
                .setDescription("No faction changes recorded yet for this faction.")
                .setTimestamp()
            ], ephemeral: true });
          }
          const PAGE_SIZE  = 15;
          const totalPages = Math.ceil(allAudit.length / PAGE_SIZE);
          const safePage   = Math.min(page, totalPages);
          const slice      = allAudit.slice((safePage - 1) * PAGE_SIZE, safePage * PAGE_SIZE);
          const ACTION_ICONS = {
            "add":          "➕",
            "remove":       "➖",
            "rank":         "🎖️",
            "transfer-in":  "📥",
            "transfer-out": "📤",
          };
          const lines = slice.map(e => {
            const ts     = Math.floor(e.at / 1000);
            const icon   = ACTION_ICONS[e.action] ?? "📌";
            const detail = e.rank ? ` → **${e.rank}**` : e.oldRank ? ` *(was ${e.oldRank})*` : "";
            return `${icon}  \`${e.action}\`  **${e.playerId}**${detail}  ·  by *${e.by}*  ·  <t:${ts}:R>`;
          });
          const embed = new EmbedBuilder().setColor(NV.AMBER)
            .setTitle(`📋  ${faction} — Audit Log (Page ${safePage}/${totalPages})`)
            .setDescription(`**${allAudit.length}** total changes *(newest first)*\n\n${DIVIDER}\n` + lines.join("\n"))
            .setFooter({ text: `Page ${safePage} of ${totalPages}  ·  15 entries per page` })
            .setTimestamp();
          return interaction.reply({ embeds: [embed], ephemeral: true });
        }

        /* ── rank (Faction Leader ONLY) ── */
        if (sub === "rank") {
          if (!hasFactionLeaderRole(interaction.member)) {
            return interaction.reply({ embeds: [factionLeaderStrictEmbed()], ephemeral: true });
          }
          const playerId = sanitizeId(o.getString("playerid"));
          const faction  = o.getString("faction");
          const rank     = o.getString("rank");
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
          await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── transfer (Mod+) ── */
        if (sub === "transfer") {
          if (!hasModRole(interaction.member)) {
            return interaction.reply({ embeds: [modOnlyEmbed()], ephemeral: true });
          }
          const playerId    = sanitizeId(o.getString("playerid"));
          const fromFaction = o.getString("from_faction");
          const toFaction   = o.getString("to_faction");
          const rawRank     = o.getString("rank");
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
          await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── add ── */
        if (sub === "add") {
          const playerId = sanitizeId(o.getString("playerid"));
          const faction  = o.getString("faction");
          const rawRank  = o.getString("rank");
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
          await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        /* ── remove ── */
        if (sub === "remove") {
          const playerId = sanitizeId(o.getString("playerid"));
          const faction  = o.getString("faction");
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
          await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }

        break;
      }

      /* ─────────────────────────────────────────────────────
         MANUAL
         ───────────────────────────────────────────────────── */
      case "manual": {
        const command = o.getString("command");
        const server  = o.getString("server");
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
        const server = o.getString("server");
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
          await logAction(embed);
          return btn.editReply({ embeds: [embed], components: [] });
        } catch {
          return interaction.editReply({ embeds: [warningEmbed("Timed Out", "Confirmation expired. No changes made.")], components: [] });
        }
      }

      /* ─────────────────────────────────────────────────────
         ADDWAGE
         ───────────────────────────────────────────────────── */
      case "addwage": {
        const playerId = sanitizeId(o.getString("playerid"));
        const tierKey  = o.getString("tier");
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
          await logAction(embed);
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
          await logAction(embed);
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
        await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         REMOVEWAGE
         ───────────────────────────────────────────────────── */
      case "removewage": {
        const playerId = sanitizeId(o.getString("playerid"));
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
        await logAction(embed);
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
        const playerId = sanitizeId(o.getString("playerid"));
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
        const playerId = sanitizeId(o.getString("playerid"));
        const amount   = o.getInteger("amount");
        const reason   = o.getString("reason") ?? "Cap gift";
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
        await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         TRANSFERCAPS
         ───────────────────────────────────────────────────── */
      case "transfercaps": {
        const fromId = sanitizeId(o.getString("from_id"));
        const toId   = sanitizeId(o.getString("to_id"));
        const amount = o.getInteger("amount");
        if (!fromId || !toId) return interaction.reply({ embeds: [emptyIdEmbed()], ephemeral: true });
        if (fromId.toLowerCase() === toId.toLowerCase()) return interaction.reply({ embeds: [errorEmbed("Invalid Transfer", "Cannot transfer caps to the same courier.")], ephemeral: true });
        const fromBal = readPlayerBalance(fromId);
        if (fromBal === null) return interaction.reply({ embeds: [errorEmbed("No Ledger", `\`${fromId}\` has no ledger file.`)], ephemeral: true });
        if (fromBal < amount) return interaction.reply({ embeds: [errorEmbed("Insufficient Caps", `\`${fromId}\` only has **${fromBal.toLocaleString()} caps**.`)], ephemeral: true });
        const toBal = readPlayerBalance(toId) ?? 0;
        if (!writePlayerBalance(fromId, fromBal - amount) || !writePlayerBalance(toId, toBal + amount)) return interaction.reply({ embeds: [errorEmbed("Write Failed", "Transfer failed. Check `MODSAVE_PATH`.")], ephemeral: true });
        writeModLog({ action: "transfercaps", fromId, toId, amount, by: interaction.user.tag });
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("💱  Caps Transfer Complete").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "📤  From",  value: `\`${fromId}\`\n**${(fromBal - amount).toLocaleString()} caps** remaining`, inline: true },
            { name: "📥  To",    value: `\`${toId}\`\n**${(toBal + amount).toLocaleString()} caps** balance`,       inline: true },
            { name: "💸  Amount",value: `**${amount.toLocaleString()} caps**`,                                       inline: true },
            { name: "🔒  By",    value: `${interaction.user}`,                                                       inline: false },
          ).setFooter({ text: randomQuote("caps") }).setTimestamp();
        await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         ADJUSTCAPS
         ───────────────────────────────────────────────────── */
      case "adjustcaps": {
        const playerId = sanitizeId(o.getString("playerid"));
        const amount   = o.getInteger("amount");
        const reason   = o.getString("reason") ?? "Manual adjustment";
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
        await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         STATS
         ───────────────────────────────────────────────────── */
      case "stats": {
        const playerId = sanitizeId(o.getString("playerid"));
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
        const hb       = findHardBanByAnyId(playerId);
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
        const color = hb ? NV.LEGION_RED : tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;

        const embed = new EmbedBuilder().setColor(color)
          .setTitle(`📋  Courier Dossier — ${playerId}`)
          .setDescription(
            hb ? `> *"Not welcome under any name."*\n\n${DIVIDER}` :
            tb ? `> *"Currently serving exile — ${formatTimeLeft(tb.expires)} remaining."*\n\n${DIVIDER}` :
            online ? `> *"Currently active on the Strip."*\n\n${DIVIDER}` :
            `> *"Offline — last tracked playtime shown."*\n\n${DIVIDER}`
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
        if (hb) {
          embed.addFields({ name: "🔨  Hard Ban Registry", value: `**Repeat offender** — *${hb.reason}*  ·  banned by ${hb.bannedBy}`, inline: false });
        }
        if (history.length) {
          embed.addFields({ name: "📋  Mod Actions", value: `**${history.length}** total — use \`/history ${playerId}\` to view`, inline: false });
        }

        embed.setFooter({ text: "Playtime tracked every 60s since bot deployment" }).setTimestamp();
        return interaction.reply({ embeds: [embed] });
      }

    }
  } catch (err) {
    logger.error("Command", `/${name}: ${err.message}`, { stack: err.stack });
    const reply = {
      embeds: [errorEmbed("System Failure", `An internal error occurred processing \`${name}\`.\n\n\`\`\`${err.message?.slice(0, 200) ?? "unknown"}\`\`\`\nCheck the server logs for the full stack trace.`)],
      ephemeral: true,
    };
    try {
      if (interaction.deferred || interaction.replied) return interaction.editReply(reply);
      return interaction.reply(reply);
    } catch {}
  }
}

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
  // warnings
  removeWarningAt,
  // donators
  DONATOR_FILE, readDonatorFile, writeDonatorFile, isDonator, addDonator, removeDonator,
  // owner / access
  isOwner, isBlacklisted,
  // ui / parsing helpers
  splitPages, extractPlayerNames,
  // faction rank caps
  getFactionRankCap, getFactionRankCaps, setFactionRankCap,
  // topic menu system
  MENU_GROUPS, ACTION_BY_KEY, normalizeServer,
};
