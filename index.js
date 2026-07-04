// Mojave Authority - our Pavlov VR moderation bot for the New Vegas RP servers.
// (c) 2026 bs9zw69ff9-source. Private project, please don't redistribute.
require("dotenv").config();
const fs     = require("fs");
const net    = require("net");
const crypto = require("crypto");
const path   = require("path");
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

/* Roles granted when a staff application is accepted (see /acceptstaffapp). */
const STAFF_ROLE_IDS = ["1517243775175622808", "1498172888224628776"];
/* Channel where accepted-staff welcome announcements are posted. */
const STAFF_ANNOUNCE_CHANNEL = "1516187330145161278";

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

// ---- data files ----
const FILES = {
  TEMPBAN:        "./tempbans.json",
  ROLES:          "./roles.json",
  WAGES:          "./wages.json",
  PLAYTIME:       "./playtime.json",
  MODLOG:         "./modlog.json",
  FACTION_RANKS:  "./faction_ranks.json",
  FACTION_CONFIG: "./faction_config.json",
  FACTION_AUDIT:  "./faction_audit.json",
  FACTION_BACKUP: "./faction_backup.json",
  MENU_GRANTS:    "./menu_grants.json",
  LASTSEEN:       "./lastseen.json",
  KNOWN:          "./known_players.json",
  USER_BLACKLIST: "./user_blacklist.json",
  USER_UNBARRED:  "./user_unbarred.json",
  MENU_PANEL:     "./menu_panel.json",
  MENU_ROLES:     "./menu_roles.json",
  MENU_LINKS:     "./menu_links.json",
};

const DEFAULTS = {
  [FILES.TEMPBAN]:        "[]",
  [FILES.WAGES]:          "[]",
  [FILES.PLAYTIME]:       "{}",
  [FILES.MODLOG]:         "[]",
  [FILES.FACTION_RANKS]:  "{}",
  [FILES.FACTION_CONFIG]: "{}",
  [FILES.FACTION_AUDIT]:  "[]",
  [FILES.FACTION_BACKUP]: "{}",
  [FILES.MENU_GRANTS]:    "{}",
  [FILES.LASTSEEN]:       "{}",
  [FILES.KNOWN]:          "{}",
  [FILES.USER_BLACKLIST]: "[]",
  [FILES.USER_UNBARRED]:  "[]",
  [FILES.MENU_PANEL]:     "{}",
  [FILES.MENU_ROLES]:     "{}",
  [FILES.MENU_LINKS]:     "{}",
  [FILES.ROLES]:          JSON.stringify({ modRoleId: "", adminRoleId: "", factionLeaderRoleId: "" }, null, 2),
};

for (const [file, def] of Object.entries(DEFAULTS)) {
  if (!fs.existsSync(file)) {
    fs.writeFileSync(file, def);
    logger.info("Init", `Created ${file}`);
  }
}

// ---- atomic file i/o  +  in-memory cache  +  mutation serialization ----
const _cache  = new Map(); // file -> parsed value
const _queues = new Map(); // file -> Promise (tail of the per-file chain)

function _rawRead(file, fallback) {
  try { return JSON.parse(fs.readFileSync(file, "utf8")); }
  catch (err) {
    if (err.code === "ENOENT") {            // missing -> create it with the fallback so it exists going forward
      ensureFile(file, JSON.stringify(fallback === undefined ? {} : fallback, null, 2));
      return fallback;
    }
    logger.warn("IO", `Read failed for ${file}: ${err.message}`);
    return fallback;
  }
}

// Keep files the bot writes into the Steam/Pavlov tree owned by `steam`, not root.
// When the bot runs as root, anything it writes - and any directory it has to
function intendedOwner(startDir) {
  let probe = startDir;
  for (let i = 0; i < 16; i++) {
    let st; try { st = fs.statSync(probe); } catch { return null; }
    if (st.uid !== 0 || st.gid !== 0) return { uid: st.uid, gid: st.gid };
    const parent = path.dirname(probe);
    if (parent === probe) return null;                       // reached "/"
    probe = parent;
  }
  return null;
}

function matchTreeOwner(filePath) {
  try {
    if (!process.getuid || process.getuid() !== 0) return;   // only root can chown to another user
    const owner = intendedOwner(path.dirname(filePath));
    if (!owner) return;                                      // entirely root-owned (bot's own tree) - leave it
    // Hand the file - and any root-owned directories we had to create on the way -
    // to the real owner, stopping once we reach a directory it already owns.
    let cur = filePath;
    for (let i = 0; i < 16; i++) {
      let st; try { st = fs.statSync(cur); } catch { break; }
      if (st.uid !== owner.uid || st.gid !== owner.gid) { try { fs.chownSync(cur, owner.uid, owner.gid); } catch {} }
      const parent = path.dirname(cur);
      if (parent === cur) break;
      let pst; try { pst = fs.statSync(parent); } catch { break; }
      if (pst.uid === owner.uid && pst.gid === owner.gid) break;   // parent already correct - done
      cur = parent;
    }
  } catch {}
}

// Create a file (and its parent dirs) with default content if it doesn't exist yet,
// so any access of a missing file transparently produces it instead of failing.
function ensureFile(fp, defaultContent = "") {
  try {
    if (fs.existsSync(fp)) return false;
    fs.mkdirSync(path.dirname(fp), { recursive: true });
    fs.writeFileSync(fp, defaultContent, "utf8");
    matchTreeOwner(fp);
    logger.info("Init", `Created missing file ${fp}`);
    return true;
  } catch (err) { logger.warn("IO", `Could not create ${fp}: ${err.message}`); return false; }
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
    .sort((a, b) => (b.lastSeen ?? 0) - (a.lastSeen ?? 0))
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
const STAFF_MENU_ID = "0011110000000000101000000000000 10101101000000";
const MENUS = [
  { name: "Staff",      value: "staff",     menuId: STAFF_MENU_ID },
  // High Staff uses the SAME bit code as Staff, but the grant also runs AddMod + AddAccessManager.
  { name: "High Staff", value: "highstaff", menuId: STAFF_MENU_ID },
  { name: "Faction",    value: "faction",   menuId: "0000000000000000000000000000010 00110000000001" },
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
const RCON_HEALTH_INTERVAL_MS = 5 * 60 * 1000;

// ---- faction-specific rank system ----
const FACTION_RANKS = {
  "NCR": {
    order:   ["Private", "Corporal", "Sergeant", "Medic", "Heavy", "Power Armor", "MP", "Ranger", "Lieutenant", "Officer"],
    default: "Private",
    badges:  {
      "Private":     "",
      "Corporal":    "",
      "Sergeant":    "",
      "Medic":       "",
      "Heavy":       "",
      "Power Armor": "",
      "MP":          "",
      "Ranger":      "",
      "Lieutenant":  "",
      "Officer":     "",
    },
    rankFiles: {
      "Private":     "ncrprivate.txt",
      "Corporal":    "ncrcorporal.txt",
      "Sergeant":    "ncrsergeant.txt",
      "Medic":       "ncrmedix.txt",
      "Heavy":       "ncrheavy.txt",
      "Power Armor": "ncrpowerarmor.txt",
      "MP":          "ncrmp.txt",
      "Ranger":      "ncrranger.txt",
      "Lieutenant":  "ncrlieutenant.txt",
      "Officer":    "ncrofficer.txt",
    },
  },
  "Legion": {
    order:   ["Recruit", "Legionnaire", "Explorer", "Slavemaster", "Prime Legionary", "Veteran Legionnaire", "Vexalarius", "Centurion", "Assassin", "Praetorian", "Legate"],
    default: "Recruit",
    badges:  {
      "Recruit":             "",
      "Legionnaire":         "",
      "Explorer":            "",
      "Slavemaster":         "",
      "Prime Legionary":     "",
      "Veteran Legionnaire": "",
      "Vexalarius":          "",
      "Centurion":           "",
      "Assassin":            "",
      "Praetorian":          "",
      "Legate":              "",
    },
    rankFiles: {
      "Recruit":             "legionrecruit.txt",
      "Legionnaire":         "legionlegionnaire.txt",
      "Explorer":            "legionexplorer.txt",
      "Slavemaster":         "legionslavemaster.txt",
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
    order:   ["Brigadeless", "Truth and Justice", "Research", "56th Rifle Brigade", "Recon", "Demolition", "Mechanized", "Hellfire", "Officer"],
    default: "Brigadeless",
    badges:  {
      "Brigadeless":        "",
      "Truth and Justice":  "",
      "Research":           "",
      "56th Rifle Brigade": "",
      "Recon":              "",
      "Demolition":         "",
      "Mechanized":         "",
      "Hellfire":           "",
      "Officer":            "",
    },
    rankFiles: {
      "Brigadeless":        "enclavebrigadeless.txt",
      "Truth and Justice":  "enclavetruthandjustice.txt",
      "Research":           "enclaveresearch.txt",
      "56th Rifle Brigade": "enclave56thriflebrigade.txt",
      "Recon":              "enclaverecon.txt",
      "Demolition":         "enclavedemolition.txt",
      "Mechanized":         "enclavemechanized.txt",
      "Hellfire":           "enclavehellfire.txt",
      "Officer":            "enclaveofficer.txt",
    },
  },
  "Khans": {
    order:   ["Prospect", "Enforcer", "Beserker", "Seargent", "Skirmisher", "Marksmen"],
    default: "Prospect",
    badges:  {
      "Prospect":   "",
      "Enforcer":   "",
      "Beserker":   "",
      "Seargent":   "",
      "Skirmisher": "",
      "Marksmen":   "",
    },
    rankFiles: {
      "Prospect":   "khansprospect.txt",
      "Enforcer":   "khansenforcer.txt",
      "Beserker":   "khansbeserker.txt",
      "Seargent":   "khansseargent.txt",
      "Skirmisher": "khansskirmisher.txt",
      "Marksmen":   "khansmarksmen.txt",
    },
  },
  "Brotherhood of Steel": {
    order:   ["Initiate", "Knight", "Paladin", "Elder"],
    default: "Initiate",
    badges:  {
      "Initiate": "",
      "Knight":   "",
      "Paladin":  "",
      "Elder":    "",
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
const MODSAVE_SYNC_INTERVAL_MS = 5 * 60 * 1000;
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

// ---- fallout: new vegas theme ----
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
  red:   0x8B0000,   // LEGION_RED  - ban / block / active
  green: 0x39FF14,   // IRRAD_GREEN - cleared / lifted / no bans
  grey:  0xFFB000,   // AMBER       - neutral info (lists, checks, connection log)
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

// ---- input sanitization ----
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
function md5(text) { return crypto.createHash("md5").update(text).digest("hex"); }

function formatKD(playerId) {
  let k = null;
  try { k = ipBans.getKD(playerId); } catch {}
  if (!k || !(k.kills + k.deaths)) return "*No record*";
  const ratio = (k.deaths ? k.kills / k.deaths : k.kills).toFixed(2);
  return `**${k.kills}** / **${k.deaths}**  ·  ${ratio}`;
}

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
async function paginate(interaction, lines, buildEmbed, { perPage = 12, ephemeral = false, idleMs = 120_000 } = {}) {
  const pages = splitPages(lines, perPage);
  const total = pages.length;
  let page = 0;
  const row = (p) => new ActionRowBuilder().addComponents(
    new ButtonBuilder().setCustomId("pg_prev").setLabel("Prev").setStyle(ButtonStyle.Secondary).setDisabled(p === 0),
    new ButtonBuilder().setCustomId("pg_ind").setLabel(`Page ${p + 1} / ${total}`).setStyle(ButtonStyle.Secondary).setDisabled(true),
    new ButtonBuilder().setCustomId("pg_next").setLabel("Next").setStyle(ButtonStyle.Secondary).setDisabled(p >= total - 1),
  );
  const render = (p) => ({ embeds: [brand(buildEmbed(pages[p], p, total))], components: total > 1 ? [row(p)] : [] });

  if (interaction.deferred || interaction.replied) await interaction.editReply(render(page));
  else await interaction.reply({ ...render(page), ...(ephemeral ? { flags: MessageFlags.Ephemeral } : {}) });
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

// ---- embed builders ----
const DIVIDER = "▓▒░▒▓▒░▒▓▒░▒▓▒░▒▓▒░▒▓▒░▒▓";
const RULE    = "·--·--·--·--·--·--·--·--·--·--·";
const BRAND_NAME = "MOJAVE AUTHORITY";

// ---- visual system  (consistent branding across every embed) ----
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
const pip = (ok) => (ok ? "[OK]" : "[!!]");

/* A blockquote-styled hero line used at the top of feature embeds. */
function hero(quoteText) { return `> *${quoteText}*\n${RULE}`; }

/* Ban / IP embeds: stamp them with the full Mojave Authority branding (author
   header, avatar, timestamp) + an optional footer — same look as everything else. */
function clinical(embed, footer) {
  return brand(embed, footer ? { footer } : {});
}

// ---- embed builders ----
function successEmbed(title, description, quoteCategory = "system") {
  return brand(new EmbedBuilder().setColor(NV.AMBER)
    .setTitle(`${title}`)
    .setDescription(`${description}`),
    { footer: { text: randomQuote(quoteCategory) } });
}
function errorEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.RUST_RED)
    .setTitle(`${title}`)
    .setDescription(`${description}`),
    { footer: { text: "Securitron network active · incident logged" } });
}
function warningEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle(`${title}`)
    .setDescription(`${description}`));
}
function adminOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEEP_BLACK)
    .setTitle("Access Denied — Mr. House's Domain")
    .setDescription('> *"I didn\'t survive two centuries to be overruled by the uninvited."*\n\nThis command is restricted to **Administrators** only.'),
    { footer: { text: "Unauthorized access attempt logged" } });
}
function ownerOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEEP_BLACK)
    .setTitle("Owner Eyes Only")
    .setDescription('> *"Some files even Mr. House keeps to himself."*\n\nThis command is restricted to the **bot owner**.'),
    { footer: { text: "Unauthorized access attempt logged" } });
}
function modOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
    .setTitle("Clearance Required")
    .setDescription('> *"You don\'t have the credentials for this, friend."*\n\nThis command requires the **Moderator** role.'),
    { footer: { text: "Access restricted · civilian status confirmed" } });
}
function blacklistedEmbed(entry) {
  const reason = entry?.reason ? `\n\n**Reason:** ${entry.reason}` : "";
  return brand(new EmbedBuilder().setColor(NV.LEGION_RED)
    .setTitle("Blacklisted — Access Revoked")
    .setDescription(`> *"You're persona non grata around here. The Securitrons won't lift a finger for you."*\n\nYou have been **blacklisted** from using this bot. All commands are unavailable to you.${reason}`),
    { footer: { text: "Contact an administrator if you believe this is a mistake" } });
}
function factionLeaderOnlyEmbed() {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("Faction Authority Required")
    .setDescription('> *"Only faction leaders pull strings around here, stranger."*\n\nRequires the **Faction Leader** role (or Moderator).'),
    { footer: { text: "Faction access not authorized" } });
}
function factionLeaderStrictEmbed() {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("Faction Leader Authority Required")
    .setDescription('> *"Rank assignments are the sole domain of faction leadership."*\n\nThis action requires the **Faction Leader** role specifically.'),
    { footer: { text: "Rank authority not authorized" } });
}
function emptyIdEmbed() {
  return brand(new EmbedBuilder().setColor(NV.NCR_TAN)
    .setTitle("No Courier ID Provided")
    .setDescription("A valid **Courier ID** or username is required.\n\n*Start typing in the player field — autocomplete surfaces anyone currently online.*"),
    { footer: { text: "Tip: manual IDs are accepted if the player is offline" } });
}
function rateLimitEmbed() {
  return brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
    .setTitle("Slow Down, Courier")
    .setDescription("You're issuing commands too quickly. Wait a moment and try again."),
    { footer: { text: "Rate limit active" } });
}

// ---- rcon ----
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
    // always cleared - otherwise every call leaked a live timer for `timeoutMs`.
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
  // Interactive commands use this - keep it snappy (2.5s, 1 retry) so a slow/down
  // server can't make a slash command spin for ~10s ("infinite load"). allSettled:
  // a failure on one server must not abort/mask the command on the other.
  const T = 2500, R = 1;
  if (server === "both") {
    const [r1, r2] = await Promise.allSettled([sendRcon(command, "server1", T, R), sendRcon(command, "server2", T, R)]);
    if (r1.status === "rejected") logger.warn("RCON", `[server1] "${command}" failed: ${r1.reason?.message || r1.reason}`);
    if (r2.status === "rejected") logger.warn("RCON", `[server2] "${command}" failed: ${r2.reason?.message || r2.reason}`);
    return {
      s1: r1.status === "fulfilled" ? r1.value : null,
      s2: r2.status === "fulfilled" ? r2.value : null,
      ok1: r1.status === "fulfilled",
      ok2: r2.status === "fulfilled",
    };
  }
  try { const v = await sendRcon(command, server, T, R); return { s1: v, s2: null, ok1: true, ok2: false }; }
  catch (err) { logger.warn("RCON", `[${server}] "${command}" failed: ${err.message}`); return { s1: null, s2: null, ok1: false, ok2: false }; }
}

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
  for (const delay of [8000, 25000]) {              // re-check after the disconnect save (and a late one)
    setTimeout(() => {
      try {
        const after = readPlayerBalance(name);
        if (after == null || after < before) {
          writePlayerBalance(name, before);
          logger.info("Caps", `Restored ${name}'s caps after kick: ${after ?? "missing"} -> ${before}`);
        }
      } catch (e) { logger.warn("Caps", `balance restore failed for ${name}: ${e.message}`); }
    }, delay);
  }
}

/* Immediately remove a (possibly in-game) player from every reachable server via
   RCON Kick. blacklist.txt only blocks RECONNECTS, so without this an already-connected
   player keeps playing until they leave. Fire-and-forget + bounded RCON so it never
   delays or hangs the ban (sendRconBoth is 2.5s/1-retry and never throws). */
function kickEverywhere(name) {
  // Kick by USERNAME - this gamemode matches players by name, not hex/EOS id.
  const target = sanitizeId(name);
  if (!target) return;
  preserveBalanceAcrossKick(name);             // don't let the kick wipe their caps
  Promise.resolve(sendRconBoth(`Kick ${target}`, "both"))
    .then(r => logger.info("Bans", `Kick ${target} -> s1=${r.ok1 ? "ok" : "fail"} s2=${r.ok2 ? "ok" : "fail"}`))
    .catch(err => logger.warn("Bans", `Kick ${target} failed: ${err.message}`));
}

/* Ban a player by writing their name to blacklist.txt on EVERY install (synced),
   kick them off now (RCON) so they don't keep playing, AND flag every IP we've ever
   seen them connect from for alt enforcement. The blacklist write is file-based so it
   never hangs; the kick is fire-and-forget.
   Returns ipBans' enforcement summary plus the blacklist outcome:
   { ids, ips, alts, field, blacklist: { name, servers }, ok }. */
// opts.permanent = true flags the account's EOS id for permanent evasion-catching.
// Temp bans (default) only blacklist the name + flag IPs - never the EOS id, so a
// returning player after their temp ban lifts isn't wrongly auto-banned.
async function banWithIp(playerId, server = "both", opts = {}) {
  const name = sanitizeBanName(playerId);
  let bl = { name, servers: 0 };
  try { bl = blacklistAdd(name); }
  catch (err) { logger.error("Bans", `blacklist add failed for "${name}": ${err.message}`); }
  logger.info("Bans", `Blacklisted "${name}" on ${bl.servers}/${PAVLOV_BASES.length} install(s)`);
  kickEverywhere(name);                        // remove them immediately if they're online
  let enf;
  try { enf = ipBans.blacklistPlayer(name, { flagId: opts.permanent === true }); }
  catch (err) { logger.warn("IPBan", `IP enforcement failed for ${name}: ${err.message}`); enf = { ids: [], ips: [], alts: [], field: null }; }
  return { ...enf, blacklist: bl, ok: bl.servers > 0 };
}
// Lift a ban: remove the name from blacklist.txt on both installs + clear IP flags.
function unbanEverywhere(playerId) {
  const name = sanitizeBanName(playerId);
  let bl = { name, removed: 0 };
  try { bl = blacklistRemove(name); } catch (err) { logger.error("Bans", `blacklist remove failed for "${name}": ${err.message}`); }
  let cleared = null;
  try { cleared = ipBans.unblacklistPlayer(name); } catch {}
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
function textify(payload) {
  if (!payload || typeof payload !== "object" || !Array.isArray(payload.embeds) || !payload.embeds.length) return payload;
  const { first, extra } = textifyChunks(payload);
  if (extra.length) first.content = `${first.content.slice(0, 1980)}\n-# (truncated)`;
  return first;
}
// Patch an interaction's output methods so ANY embed payload goes out as text.
// Interaction responses are already native replies (Discord shows the
function patchInteractionOutput(interaction) {
  for (const m of ["reply", "editReply", "followUp", "update"]) {
    const orig = typeof interaction[m] === "function" ? interaction[m].bind(interaction) : null;
    if (!orig) continue;
    interaction[m] = async (payload, ...args) => {
      if (typeof payload === "string") payload = { content: payload };
      if (!payload || typeof payload !== "object") return orig(payload, ...args);
      const hasEmbeds = Array.isArray(payload.embeds) && payload.embeds.length;
      if (!hasEmbeds && !payload.content) return orig(payload, ...args);   // component-only edits etc.
      let first = payload, extra = [];
      if (hasEmbeds) ({ first, extra } = textifyChunks(payload)); else first = { ...payload };
      const res = await orig(first, ...args);
      for (const c of extra) { try { await interaction.followUp({ content: c, flags: first.flags }); } catch {} }
      return res;
    };
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

// ---- punishment dm notice ----
async function dmPunishmentNotice(discordUser, { action, color, playerId, reason, fields = [] }) {
  if (!discordUser) return null;
  const embed = brand(new EmbedBuilder().setColor(color)
    .setTitle(`Moderation Notice — ${action}`)
    .setDescription(hero("A moderation action has been taken on your account."))
    .addFields(
      { name: "Courier",  value: `\`${playerId}\``, inline: true },
      { name: "Action",   value: `**${action}**`,   inline: true },
      ...(reason ? [{ name: "Reason", value: reason, inline: false }] : []),
      ...fields,
    ),
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
async function dmEmbed(discordUser, embed) {
  try { await discordUser.send(textify({ embeds: [brand(embed)] })); return true; }
  catch (err) { logger.warn("DM", `Could not DM ${discordUser.id}: ${err.message}`); return false; }
}

// ---- player cache ----
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
  const online = [...new Set([...playerCache.server1, ...playerCache.server2])].filter(Boolean);
  if (!online.length) return;
  update(FILES.PLAYTIME, {}, (pt) => {
    for (const id of online) pt[id] = (pt[id] ?? 0) + 1;
    return pt;
  });
  recordLastSeen(online);
}

// ---- faction file helpers ----
function readFactionFile(spawnFile) {
  const fp = path.join(FACTION_ROLES_PATH, spawnFile);
  try {
    return fs.readFileSync(fp, "utf8").split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  } catch (err) { if (err.code === "ENOENT") { ensureFile(fp, ""); return []; } return null; }
}

// A normal whitelist op (add / remove / rank / transfer) changes a roster by at most
// a couple of entries. If a write would DELETE more than this many existing entries it
const FACTION_BULK_DROP_LIMIT = 5;
const FACTION_BAK_DIR = "./faction_file_bak";   // bot-local pre-write snapshots (NOT in the game tree)

// Keep one rolling pre-write copy of a faction file so any bad write is recoverable by hand.
function backupFactionFile(spawnFile, content) {
  try {
    fs.mkdirSync(FACTION_BAK_DIR, { recursive: true });
    fs.writeFileSync(path.join(FACTION_BAK_DIR, spawnFile + ".bak"), content, "utf8");
  } catch { /* best-effort; never block the real write */ }
}

function writeFactionFile(spawnFile, lines, opts = {}) {
  const fp = path.join(FACTION_ROLES_PATH, spawnFile);
  if (!Array.isArray(lines)) {
    logger.error("Faction", `Refused write to ${spawnFile}: payload is not an array`);
    return false;
  }
  // ---- Destruction guard: confirm we are not silently wiping a populated roster. ----
  if (!opts.allowBulk) {
    let raw = null;
    try { raw = fs.readFileSync(fp, "utf8"); }
    catch (e) {
      if (e.code !== "ENOENT") {   // can't read current file to compare -> refuse rather than risk a wipe
        logger.error("Faction", `Refused write to ${spawnFile}: cannot read current contents (${e.code}). Aborting to protect the roster.`);
        return false;
      }
    }
    if (raw !== null) {
      const current = raw.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
      backupFactionFile(spawnFile, raw);   // snapshot BEFORE we change anything
      if (current.length) {
        const keep    = new Set(lines.map(l => String(l).toLowerCase()));
        const dropped = current.filter(l => !keep.has(l.toLowerCase())).length;
        if (dropped > FACTION_BULK_DROP_LIMIT) {
          logger.error("Faction", `REFUSED write to ${spawnFile}: would remove ${dropped} of ${current.length} entries (limit ${FACTION_BULK_DROP_LIMIT}). Suspected corruption — roster left untouched.`);
          return false;
        }
      }
    }
  }
  if (writeGameFile(fp, lines.join("\n") + "\n")) return true;   // mirrored to every install
  logger.error("Faction", `Write failed for ${spawnFile}`);
  return false;
}

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
  try { files = fs.readdirSync(base).filter(f => f.endsWith(".txt")); }
  catch (e) { return { ok: false, error: e.code || e.message }; }
  let wiped = 0;
  for (const f of files) { if (writePlayerBalance(path.basename(f, ".txt"), 0)) wiped++; }
  return { ok: true, wiped, total: files.length };
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
      const expires   = /^perm/i.test(p.unban) || !p.unban ? null : easternNoonUTC(p.unban);   // lift at noon Eastern that day
      bans.push(expires
        ? { playerId: p.name, reason: p.reason, moderator: "in-game", at: Date.now(), expires, durationLabel: "until " + p.unban }
        : { playerId: p.name, reason: p.reason, moderator: "in-game", at: Date.now(), permanent: true });
      added++;
    }
    if (added) logger.info("Bans", `Imported ${added} ban(s) from the modsave banlist into the database`);
    return bans;
  });
}

/* ---------------- faction whitelist snapshot (save/load) ---------------- */
// Snapshot every faction file (spawn + rank rosters) to a JSON we control, so the
// owner can restore the whole whitelist set after accidents/wipes.
function saveFactionBackup() {
  let files;
  try { files = fs.readdirSync(FACTION_ROLES_PATH).filter(f => f.endsWith(".txt")); }
  catch (e) { return { ok: false, error: e.code || e.message }; }
  const data = { savedAt: Date.now(), files: {} };
  for (const f of files) { const lines = readFactionFile(f); if (lines) data.files[f] = lines; }
  if (!safeWrite(FILES.FACTION_BACKUP, data)) return { ok: false, error: "could not write backup" };
  return { ok: true, count: Object.keys(data.files).length };
}
// Restore the last snapshot, writing every saved file back (mirrored to all installs).
function loadFactionBackup() {
  const data = safeRead(FILES.FACTION_BACKUP, null);
  if (!data || !data.files || !Object.keys(data.files).length) return { ok: false, empty: true };
  // Snapshot the live rosters before overwriting, so a mistaken restore can be undone by hand.
  for (const f of Object.keys(data.files)) {
    try { backupFactionFile(f, fs.readFileSync(path.join(FACTION_ROLES_PATH, f), "utf8")); } catch {}
  }
  let restored = 0;
  // allowBulk: a restore legitimately rewrites whole rosters (may remove many entries).
  for (const [f, lines] of Object.entries(data.files)) { if (writeFactionFile(f, lines, { allowBulk: true })) restored++; }
  return { ok: true, restored, savedAt: data.savedAt };
}

function memberHasRoleId(member, roleId) {
  if (!roleId || !member) return false;
  const r = member.roles;
  if (r?.cache && typeof r.cache.has === "function") return r.cache.has(roleId);   // GuildMember
  if (Array.isArray(r)) return r.includes(roleId);                                 // raw interaction member
  return false;
}





// ---- temp ban expiry ----
async function processExpiredBans() {
  const now = Date.now();
  const lifted = [];
  for (const ban of loadBans()) {
    if (ban.permanent || !ban.expires || ban.expires > now) continue;   // never auto-lift permanent bans
    try {
      unbanEverywhere(ban.playerId);   // remove from blacklist.txt (both installs) + clear IP flags
      lifted.push(ban.playerId);
      logger.info("Bans", `Expired ban lifted: ${ban.playerId}`);
      writeModLog({ action: "auto-unban", playerId: ban.playerId, reason: "Sentence served" });
      await logBan(
        clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Sentence Served — Courier Released")
          .setDescription('> *"Every soul deserves a second chance in the Mojave."*')
          .addFields(
            { name: "Courier",          value: `\`${ban.playerId}\``,          inline: true },
            { name: "Original Offense", value: ban.reason,                     inline: true },
            { name: "Duration Served",  value: ban.durationLabel ?? "Unknown", inline: true },
            { name: "Originally Banned",value: `by ${ban.moderator}`,          inline: false },
          ), "Exile expired — access restored automatically")
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

// ---- leaderboard ----
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
  return i === 0 ? "" : i === 1 ? "" : i === 2 ? "" : `\`#${String(i + 1).padStart(2)}\``;
}

function buildLeaderboardEmbed() {
  const entries = buildLeaderboardData();
  const embed = new EmbedBuilder().setColor(NV.GOLD)
    .setTitle(`New Vegas Caps — Top ${LEADERBOARD_TOP_N}`);
  if (!entries) return brand(embed.setColor(NV.RUST_RED)
    .setDescription(`${hero("Vault records inaccessible.")}\n\`MODSAVE_PATH\` not configured or unreadable — check your \`.env\`.`),
    { footer: { text: `Updated every 30s` } });
  if (!entries.length) return brand(embed.setColor(NV.IRRAD_GREEN)
    .setDescription(`${hero("No ledgers found.")}\nNo cap records on file yet.`),
    { footer: { text: `Updated every 30s` } });
  const top = entries[0]?.balance || 1;
  const body = entries.map((e, i) => {
    const meter = i < 5 ? `  \`${bar(e.balance, top, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  ·  ${e.balance.toLocaleString()} caps${meter}`;
  }).join("\n");
  return brand(embed.setDescription(`${hero("War never changes. But caps? Caps fluctuate.")}\n${body}`),
    { thumb: true, footer: { text: `Updated every 30s` } });
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

// ---- playtime leaderboard ----
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
    .setTitle(`Most Active Couriers — Top ${LEADERBOARD_TOP_N}`);
  if (!entries.length) return brand(embed
    .setDescription(`${hero("No playtime tracked yet.")}\nPlaytime accrues while couriers are online (sampled every 60s).`),
    { footer: { text: `Updated every 30s` } });
  const top = entries[0]?.minutes || 1;
  const body = entries.map((e, i) => {
    const meter = i < 5 ? `  \`${bar(e.minutes, top, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  ·  ${formatPlaytime(e.minutes)}${meter}`;
  }).join("\n");
  return brand(embed.setDescription(`${hero("Time served in the Mojave.")}\n${body}`),
    { thumb: true, footer: { text: `Updated every 30s` } });
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
  const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Live Player List")
    .setDescription(hero(`**${total}** courier${total !== 1 ? "s" : ""} roaming the Mojave right now.`))
    .addFields({ name: `Server 1 (${s1.length})`, value: fmt(s1), inline: true });
  if (hasServer2) embed.addFields({ name: `Server 2 (${s2.length})`, value: fmt(s2), inline: true });
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

// Delete every message in a channel - used on startup so stale leaderboard/player-list
// messages from previous runs don't pile up (the bot re-posts a fresh one). Bulk-deletes
async function purgeChannel(channelId) {
  if (!channelId) return 0;
  let channel;
  try { channel = await client.channels.fetch(channelId); } catch { return 0; }
  if (!channel || typeof channel.bulkDelete !== "function") return 0;
  let total = 0;
  for (let pass = 0; pass < 12; pass++) {              // up to ~1200 messages
    let msgs;
    try { msgs = await channel.messages.fetch({ limit: 100 }); } catch { break; }
    if (!msgs.size) break;
    let removed = 0;
    try { const d = await channel.bulkDelete(msgs, true); removed = d.size; } catch {}   // true = ignore >14d
    if (removed < msgs.size) {                         // leftovers are >14d - bulkDelete skips them
      for (const m of msgs.values()) { try { await m.delete(); removed++; } catch {} }
    }
    total += removed;
    if (removed === 0) break;                          // nothing deletable (e.g. missing permission) - stop
  }
  return total;
}

// Clear all leaderboard/player-list channels, then post fresh single messages.
async function refreshLeaderboardChannels() {
  const channels = [...new Set([process.env.LEADERBOARD_CHANNEL, PLAYTIME_LB_CHANNEL, PLAYERLIST_CHANNEL].filter(Boolean))];
  for (const ch of channels) {
    try { const n = await purgeChannel(ch); if (n) logger.info("Purge", `Cleared ${n} old message(s) from channel ${ch}`); }
    catch (e) { logger.warn("Purge", `Could not purge ${ch}: ${e.message}`); }
  }
  // reset tracked ids (channel is now empty) so the next post creates a fresh message
  lastLeaderboardMsgId = null; lastPlaytimeLbMsgId = null; lastPlayerListMsgId = null;
  postLeaderboard(); postPlaytimeLeaderboard(); postPlayerList();
}

/* Find the Discord user to DM for a Pavlov username, by matching the guild member
   whose server NICKNAME (or display name) equals the name. Returns a User or null. */
async function dmUserForPavlov(name, guild) {
  const key = String(name ?? "").trim().toLowerCase();
  if (!key || !guild) return null;
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
      if (hadHS) await sendRconBoth(`RemoveAccessManager ${t}`, "both");
      for (const m of MENUS) for (const srv of ["server1", "server2", "both"]) await removeMenuGrant(link.name, srv, m.value);
      await clearMenuLink(interaction.user.id);
      logAction(clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Menu Removed (self)")
        .addFields({ name: "Discord", value: `${interaction.user}`, inline: true }, { name: "In-game", value: `\`${link.name}\``, inline: true })));
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
    .addFields(
      { name: "Discord", value: `${interaction.user}`, inline: true },
      { name: "In-game", value: `\`${name}\``,          inline: true },
      { name: "Menu",    value: meta.name,               inline: true },
    )));
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
  for (const srv of (hasServer2 ? ["server1", "server2"] : ["server1"])) {
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
setInterval(postLeaderboard,         LEADERBOARD_INTERVAL_MS);
setInterval(postPlaytimeLeaderboard, LEADERBOARD_INTERVAL_MS);
setInterval(postPlayerList,          PLAYERLIST_INTERVAL_MS);
setInterval(rconHealthCheck,         RCON_HEALTH_INTERVAL_MS);
setInterval(async () => {
  await refreshPlayerCache("server1");
  if (hasServer2) await refreshPlayerCache("server2");
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
  new SlashCommandBuilder().setName("flush")
    .setDescription("Randomly kick one online player from a server")
    .addStringOption(serverOption),
  new SlashCommandBuilder().setName("seen")
    .setDescription("Show when a courier was last seen online")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("staffactivity")
    .setDescription("Admin — All moderation actions taken by a staff member")
    .addUserOption(o => o.setName("staff").setDescription("Staff member to audit").setRequired(true)),
  new SlashCommandBuilder().setName("tempban")
    .setDescription("Exile a courier for a set period")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID or username").setRequired(true).setAutocomplete(true))
    .addStringOption(o => o.setName("date").setDescription("Unban date (YYYY-MM-DD) — lifts at 12pm Eastern that day").setRequired(true).setAutocomplete(true))
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
  new SlashCommandBuilder().setName("permban")
    .setDescription("Admin — Permanently exile a courier")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addStringOption(serverOption)
    .addStringOption(o => o.setName("reason").setDescription("Grounds").setRequired(true).addChoices(...BAN_REASONS))
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
  new SlashCommandBuilder().setName("acceptstaffapp")
    .setDescription("Admin — Accept a staff application: DM the applicant and grant staff roles")
    .addUserOption(o => o.setName("user").setDescription("The accepted applicant").setRequired(true)),
  new SlashCommandBuilder().setName("denystaffapp")
    .setDescription("Admin — Deny a staff application: DM the applicant (no other action)")
    .addUserOption(o => o.setName("user").setDescription("The denied applicant").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Optional reason shown in the DM")),
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
    .addSubcommand(s => s.setName("setcap")
      .setDescription("Admin — Set the maximum member cap for a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addIntegerOption(o => o.setName("cap").setDescription("Maximum number of members (1–500)").setRequired(true).setMinValue(1).setMaxValue(500)))
    .addSubcommand(s => s.setName("setrankcap")
      .setDescription("Admin — Set the per-rank member cap within a faction")
      .addStringOption(o => o.setName("faction").setDescription("Faction name").setRequired(true).addChoices(...factionChoices))
      .addStringOption(o => o.setName("rank").setDescription("Rank to cap (faction-specific)").setRequired(true).setAutocomplete(true))
      .addIntegerOption(o => o.setName("cap").setDescription("Max members at this rank (0 = unlimited)").setRequired(true).setMinValue(0).setMaxValue(500))),

  new SlashCommandBuilder().setName("manual")
    .setDescription("Admin — Send a raw RCON command")
    .addStringOption(o => o.setName("command").setDescription("Raw RCON signal").setRequired(true))
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
  new SlashCommandBuilder().setName("adjustcaps")
    .setDescription("Admin — Manually add or subtract caps from a courier's ledger")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true))
    .addIntegerOption(o => o.setName("amount").setDescription("Caps to add (positive) or subtract (negative)").setRequired(true))
    .addStringOption(o => o.setName("reason").setDescription("Reason for adjustment (logged)")),
  new SlashCommandBuilder().setName("stats")
    .setDescription("Courier dossier: playtime, factions, balance, and mod history")
    .addStringOption(o => o.setName("playerid").setDescription("Courier ID").setRequired(true).setAutocomplete(true)),
  new SlashCommandBuilder().setName("kd")
    .setDescription("Kill/death stats — a courier's K/D, or the leaderboard")
    .addStringOption(o => o.setName("playerid").setDescription("Courier (leave blank for the K/D leaderboard)").setRequired(false).setAutocomplete(true)),
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
    // Fired on every LIVE join - re-grant recorded RCON menus (the server drops a
    // player's menu on disconnect, so a rejoin needs the grant re-applied).
    onConnect: async ({ name }) => {
      try { scheduleMenuRegrant(name); } catch (e) { logger.warn("Menus", `re-grant schedule failed: ${e.message}`); }
    },
    // Fired once a player's IP is CONFIRMED (the same-line disconnect pairing) -
    // posts an accurate name, ID, IP entry to the connection-feed webhook.
    onConfirm: async ({ name, ip, server, record }) => {
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

      const embed = clinical(new EmbedBuilder().setColor(CLIN.green)
        .setTitle(`Player Information: ${name}`)
        .setDescription("Database Info")
        .addFields(
          { name: "EOS ID",          value: rec.id ? `\`${rec.id}\`` : "unknown", inline: false },
          { name: "First Seen",      value: fmt(rec.firstSeen),                  inline: true },
          { name: "Last Seen",       value: fmt(rec.lastSeen),                   inline: true },
          { name: "Login Count",     value: String(rec.logins ?? 0),             inline: true },
          { name: "Possible Alts",   value: (rec.alts && rec.alts.length ? rec.alts.join(", ") : "None").slice(0, 1024), inline: false },
          { name: "Bypass Auto-Ban", value: rec.bypass ? "Yes" : "No",           inline: true },
          { name: "Server",          value: srvName,                             inline: true },
          { name: "Log Scan Results", value: rec.flagged ? "Flagged — matches the blacklist" : "No matches", inline: false },
          { name: "Last Activity",   value: fmt(lastActivity),                   inline: false },
          { name: "Recent Connections", value: "```\n" + (connLines.length ? connLines.join("\n") : "no records").slice(0, 1000) + "\n```", inline: false },
        ), "Connection log · Mojave Authority");
      postFeed(embed);
    },
    // Fired when someone CONNECTS (live log) matching a blacklisted username/IP:
    // ban that username on both servers (Shack bans by name, not hex id).
    onAutoBan: async ({ name, ip, reason }) => {
      // A TEMP-banned player bouncing off the blacklist still shows up in the log as
      // a join attempt from their flagged IP/EOS id. Their temp ban already covers
      const existing = loadBans().find(b => _sameId(b.playerId, name));
      if (existing && !existing.permanent && existing.expires && existing.expires > Date.now()) {
        logger.info("IPGuard", `${name} tried to join while temp-banned — blocked, no escalation`);
        return;
      }
      const res = await banWithIp(name, "both", { permanent: true });
      const ok  = res?.ok;
      try { await upsertPermBan({ playerId: name, reason: `Auto-ban — ${reason || "blacklist match"}`, moderator: "IP-Guard" }); } catch {}   // show in /banlist
      writeModLog({ action: "auto-ipban", playerId: name, reason: `Auto-ban — ${reason || "blacklist match"}${ip ? ` (${ip})` : ""}${ok ? "" : " [BLACKLIST WRITE FAILED]"}`, by: "IP-Guard" });
      logger.warn("IPGuard", `Auto-banned ${name} — ${reason || "blacklist match"}${ip ? ` (${ip})` : ""} — blacklisted on ${res?.blacklist?.servers ?? 0}/${PAVLOV_BASES.length} install(s)`);
      const banEmbed = clinical(new EmbedBuilder().setColor(ok ? CLIN.red : CLIN.grey)
        .setTitle(ok ? "Blacklisted Courier Blocked" : "Blacklist Match — WRITE FAILED")
        .setDescription(ok ? `${hero(randomQuote("autoban"))}` : "> *The order went out, but the ledger wouldn't take it. This courier is NOT blacklisted — check the file paths and ban them by hand.*")
        .addFields(
          { name: "Courier", value: `\`${name}\``,            inline: true },
          { name: "IP",      value: `\`${ip ?? "unknown"}\``, inline: true },
          { name: "EOS ID",  value: (() => { try { const r = ipBans.getRecord(name); return r?.id ? `\`${r.id}\`` : "unknown"; } catch { return "unknown"; } })(), inline: true },
          { name: "Reason",  value: reason || "blacklist match", inline: true },
          { name: "Blacklisted on", value: `${res?.blacklist?.servers ?? 0} of ${PAVLOV_BASES.length} install(s)`, inline: false },
        ), "Auto-ban · blacklist.txt · both servers");
      await logBan(banEmbed);   // dedicated ban-log channel (falls back to mod-log)
      postFeed(banEmbed);       // also surface it in the connection feed
    },
  });
  refreshPlayerCache("server1");
  if (hasServer2) refreshPlayerCache("server2");
  try { healTreeOwnership(); } catch (e) { logger.warn("Init", `ownership heal failed: ${e.message}`); }
  try { const r = syncAllModSave(); if (r.installs > 1 && !r.off) logger.info("Sync", `ModSave sync on startup — ${r.synced} file(s) propagated across ${r.installs} installs`); } catch (e) { logger.warn("Sync", `ModSave sync failed: ${e.message}`); }
  try { ensureFactionFiles(); } catch (e) { logger.warn("Init", `faction file build failed: ${e.message}`); }
  try { reconcileBlacklists(); } catch (e) { logger.warn("Blacklist", `reconcile failed: ${e.message}`); }
  try { await importBlacklistToBans(); } catch (e) { logger.warn("Bans", `blacklist import failed: ${e.message}`); }
  try { await importModsaveBanlist(); } catch (e) { logger.warn("Bans", `modsave banlist import failed: ${e.message}`); }   // pull in-game-menu bans into the DB
  try { syncModsaveBanlist(); } catch {}   // then (re)build the custom ban-message file
  setTimeout(rconHealthCheck, 5_000);
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
  logger.info("Bot", "Queues drained — exiting.");
  process.exit(0);
}
process.on("SIGINT",  () => { shutdown("SIGINT").catch(() => process.exit(1)); });
process.on("SIGTERM", () => { shutdown("SIGTERM").catch(() => process.exit(1)); });
process.on("uncaughtException",  err => logger.error("Uncaught",  err.message, { stack: err.stack }));
process.on("unhandledRejection", r   => logger.error("Unhandled", String(r)));

// ---- interactions ----
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
  const PUBLIC         = ["help", "ping", "serverinfo", "find", "checkban", "wagelist", "checkbalance", "stats", "seen", "kd"];
  const MOD_COMMANDS   = ["kick", "tempban", "unban", "announce", "givecaps"];
  const FL_COMMANDS    = ["addwage", "removewage", "faction"];
  const ADMIN_COMMANDS = ["permban", "cleartempbans", "setroles", "givemenu", "stripmenu", "manual", "adjustcaps", "donator", "acceptstaffapp", "denystaffapp", "staffactivity"];

  const name = interaction.commandName;

  if (!PUBLIC.includes(name)) {
    if (ADMIN_COMMANDS.includes(name) && !hasAdminRole(interaction.member)) {
      return interaction.reply({ embeds: [adminOnlyEmbed()], flags: MessageFlags.Ephemeral });
    }
    if (FL_COMMANDS.includes(name) && !hasModRole(interaction.member) && !hasFactionLeaderRole(interaction.member)) {
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
          .setTitle("Mojave Authority — Command Roster")
          .setDescription(
            `> *"War. War never changes. But the rules of the Strip — those we enforce."*\n\n${DIVIDER}\n` +
            `**Your Access:** ${badge}\nMod: ${mStr}  ·  Admin: ${aStr}  ·  Faction: ${fStr}\n${DIVIDER}\n\n` +
            `*Autocomplete works in all Courier ID and Rank fields — faction ranks are filtered per faction.*`
          )
          .addFields(
            { name: "Public",
              value: "`/help` `/ping` `/serverinfo` `/find` `/checkban` `/stats` `/checkbalance` `/wagelist` `/seen`\n`/faction list` `/faction audit`" },
            { name: "Moderator",
              value: [
                "`/kick <id> <server> [reason]` — Eject",
                "`/flush <server>` — Randomly kick one online player",
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
                "`/clearallbans` — *Owner only* — unban everyone (clears blacklist.txt)",
                "`/acceptstaffapp <user>` — DM acceptance + grant staff roles",
                "`/denystaffapp <user> [reason]` — DM a denial (no other action)",
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
      case "ping": {
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
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
          .setTitle("System Status")
          .setDescription(`${hero(headline)}\n${health}  ·  **${okCount + 1}/3** nodes online`)
          .addFields(
            { name: "Bot",        value: `${pip(true)}  Online\n\`gateway ${wsPing}ms\``,                    inline: true },
            { name: "Server 1",   value: s1ok ? `${pip(true)}  Reachable` : `${pip(false)}  Unreachable`,    inline: true },
            { name: "Server 2",   value: s2ok ? `${pip(true)}  Reachable` : `${pip(false)}  Unreachable`,    inline: true },
            { name: "RTT",        value: `\`${bar(Math.min(rtt, 1000), 1000, 10)}\`\n\`${rtt}ms\``,           inline: true },
            { name: "Uptime",     value: `\`${formatUptime(Date.now() - BOT_START_MS)}\``,                    inline: true },
            { name: "Cached",     value: `S1 \`${playerCache.server1.length}\` · S2 \`${playerCache.server2.length}\``, inline: true },
            { name: "Mod Log",    value: `\`${loadModLog().length}\` entries`,                                inline: true },
            { name: "Open Bans",  value: `\`${loadBans().length}\` active`,                                  inline: true },
          );
        brand(embed, { thumb: true, footer: { text: BOT_COPYRIGHT } });
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
            .setTitle(`${info.serverName}`)
            .setDescription(`${pip(info.ok)}  ${info.ok ? "Online" : "Offline"}  ·  \`${bar(info.players, Number(info.maxPlayers) || info.players || 1, 10)}\``)
            .addFields(
              { name: "Map",     value: info.mapLabel,                          inline: true },
              { name: "Mode",    value: info.gameMode,                          inline: true },
              { name: "Players", value: `${info.players} / ${info.maxPlayers}`, inline: true },
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
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        await Promise.all(hasServer2 ? [refreshPlayerCache("server1"), refreshPlayerCache("server2")] : [refreshPlayerCache("server1")]);
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
            brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("No Matches Found")
              .setDescription(`${hero(`No couriers matching "${query}" are online.`)}\n*Try a shorter search term.*`))
          ]});
        }
        const lines = matches.map((m) => {
          const srvStr = m.servers.map(s => s === "server1" ? "S1" : "S2").join("+");
          return `\`[${srvStr}]\`  **${m.name}**`;
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
        await sendRconBoth(`Kick ${playerId}`, server);          // kick by USERNAME (gamemode matches names)
        writeModLog({ action: "kick", playerId, reason, by: interaction.user.tag, server });
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Courier Ejected from the Strip")
          .setDescription(`> *${randomQuote("kick")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "Courier", value: `\`${playerId}\``,                                  inline: true },
            { name: "Server",  value: `${serverLabel(server)}`,   inline: true },
            { name: "By",      value: `${interaction.user}`,                              inline: true },
            { name: "Reason",  value: reason,                                             inline: false },
          ).setFooter({ text: "Kick logged — no ban issued" }).setTimestamp();
        const kTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId, interaction.guild);
        const kDm = await dmPunishmentNotice(kTarget, {
          action: "Kick", color: NV.NCR_TAN, playerId, reason,
          fields: [{ name: "Server", value: serverLabel(server), inline: true }],
        });
        const kDmField = dmStatusField(kDm, kTarget);
        if (kDmField) embed.addFields(kDmField);
        brand(embed); await logAction(embed);
        return interaction.editReply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         FLUSH — randomly kick one online player from a server
         ───────────────────────────────────────────────────── */
      case "flush": {
        if (!hasModRole(interaction.member)) return interaction.reply({ embeds: [modOnlyEmbed()], flags: MessageFlags.Ephemeral });
        const server = interaction.options.getString("server");
        await interaction.deferReply();
        const servers = server === "both" ? (hasServer2 ? ["server1", "server2"] : ["server1"]) : [server];
        const pool = [];
        for (const srv of servers) {
          try { for (const p of await getOnlinePlayers(srv)) if (p.name) pool.push({ ...p, srv }); } catch {}
        }
        if (!pool.length) {
          return interaction.editReply({ embeds: [warningEmbed("Nothing to Flush", "No players are currently online on the selected server.")] });
        }
        const pick   = pool[Math.floor(Math.random() * pool.length)];
        const target = sanitizeId(pick.name);        // kick by USERNAME
        preserveBalanceAcrossKick(pick.name);                   // don't let the kick wipe their caps
        let kicked = false;
        try { await sendRcon(`Kick ${target}`, pick.srv, 2500, 1); kicked = true; } catch {}
        writeModLog({ action: "flush-kick", playerId: pick.name, server: pick.srv, by: interaction.user.tag });
        const embed = brand(new EmbedBuilder().setColor(kicked ? NV.AMBER : NV.NCR_TAN).setTitle("Flush — Random Kick")
          .setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Flushed",  value: `\`${pick.name}\``,                                  inline: true },
            { name: "Server",   value: `${serverLabel(pick.srv)}`,                          inline: true },
            { name: "By",       value: `${interaction.user}`,                               inline: true },
            { name: "Pool",     value: `Picked at random from **${pool.length}** online player(s)`, inline: false },
          ).setFooter({ text: kicked ? "Random kick — no ban issued" : "Kick command sent (no RCON confirmation)" }).setTimestamp());
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
         SEEN  (last time a courier was online)
         ───────────────────────────────────────────────────── */
      case "seen": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const key    = playerId.toLowerCase();
        const onS1   = playerCache.server1.some(n => n.toLowerCase() === key);
        const onS2   = playerCache.server2.some(n => n.toLowerCase() === key);
        const online = onS1 || onS2;
        const last   = getLastSeen(playerId);
        let color, desc;
        if (online) {
          const where = [onS1 && "Server 1", onS2 && "Server 2"].filter(Boolean).join("  +  ");
          color = NV.IRRAD_GREEN;
          desc  = `**Online right now** on ${where}.`;
        } else if (last) {
          color = NV.AMBER;
          desc  = `Last seen <t:${Math.floor(last / 1000)}:R>  ·  <t:${Math.floor(last / 1000)}:F>`;
        } else {
          color = NV.DEAD_GREY;
          desc  = "No sighting on record. This courier hasn't been seen online since the bot started tracking.";
        }
        const embed = new EmbedBuilder().setColor(color).setTitle(`Last Seen — ${playerId}`)
          .setDescription(`${DIVIDER}\n${desc}`)
          .setFooter({ text: "Presence sampled every 60s" }).setTimestamp();
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
      }

      /* ─────────────────────────────────────────────────────
         NOTE  (freeform staff notes on any courier)
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         HISTORY
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
          { perPage: 12, flags: MessageFlags.Ephemeral });
      }

      /* ─────────────────────────────────────────────────────
         TEMPBAN  ← deferReply added
         ───────────────────────────────────────────────────── */
      case "tempban": {
        const playerId    = sanitizeBanName(interaction.options.getString("playerid"));
        const dateStr     = interaction.options.getString("date");
        const server      = interaction.options.getString("server");
        const reasonKey   = interaction.options.getString("reason");
        const reason      = BAN_REASON_LABELS[reasonKey] ?? reasonKey;
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const expires = easternNoonUTC(dateStr);                 // lifts at 12pm Eastern on that date
        if (!expires || expires <= Date.now()) {
          return interaction.reply({ embeds: [errorEmbed("Invalid Unban Date",
            `Enter a **future** date as \`YYYY-MM-DD\` (e.g. \`${new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10)}\`). The ban lifts at **12pm Eastern** that day.`)], flags: MessageFlags.Ephemeral });
        }
        await interaction.deferReply();
        const label = `until ${new Date(expires).toISOString().slice(0, 10)}`;
        const replaced       = loadBans().find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        const ipEnf = await banWithIp(playerId, server);
        await upsertTempBan({ playerId, reason, expires, durationLabel: label, moderator: interaction.user.tag, server });
        writeModLog({ action: "tempban", playerId, reason, duration: label, by: interaction.user.tag, server });
        const ts = Math.floor(expires / 1000);
        const embed = clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Courier Exiled from the Mojave")
          .setDescription(`> *${randomQuote("ban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "Courier",  value: `\`${playerId}\``,                                inline: true },
            { name: "Server",   value: `${serverLabel(server)}`, inline: true },
            { name: "Duration", value: `**${label}**`,                                   inline: true },
            { name: "Offense",  value: reason,                                           inline: false },
            { name: "Expires",  value: `<t:${ts}:F>  ·  <t:${ts}:R>`,                     inline: true },
            { name: "By",       value: `${interaction.user}`,                            inline: true },
          ), replaced ? `Replaced earlier exile: ${replaced.reason}` : "Auto-lifted when timer expires");
        const tbTarget = interaction.options.getUser("discord_user") || await dmUserForPavlov(playerId, interaction.guild);
        const tbDm = await dmPunishmentNotice(tbTarget, {
          action: "Temporary Ban", color: NV.RUST_RED, playerId, reason,
          fields: [
            { name: "Duration", value: `**${label}**`,            inline: true },
            { name: "Server",   value: serverLabel(server),       inline: true },
            { name: "Expires",  value: `<t:${ts}:F>  ·  <t:${ts}:R>`, inline: false },
          ],
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
        await removeBans(playerId);
        const { blacklist: bl, cleared: c } = unbanEverywhere(playerId);   // blacklist.txt (both installs) + IP flags
        writeModLog({ action: "unban", playerId, by: interaction.user.tag, server });
        const ipLifted = c && (c.ips + c.names) > 0
          ? `Cleared ${c.ips} IP(s) and ${c.names} username flag(s).`
          : "Nothing was flagged for this player.";
        const embed = clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("Exile Lifted — Welcome Back to the Strip")
          .setDescription(`> *${randomQuote("unban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "Courier",     value: `\`${playerId}\``,     inline: true },
            { name: "Pardoned By", value: `${interaction.user}`, inline: true },
            { name: "Record",       value: removed ? "Temp ban record cleared." : "No temp ban record.", inline: false },
            { name: "Blacklist",    value: bl.removed ? `Removed from blacklist.txt on ${bl.removed} install(s).` : "Was not on blacklist.txt.", inline: false },
            { name: "IP Enforcement", value: ipLifted, inline: false },
          ));
        await logBan(embed);
        return interaction.editReply({ embeds: [embed] });
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
        if (tb && !tb.permanent && tb.expires) {
          const ts = Math.floor(tb.expires / 1000);
          return interaction.reply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Temporary Exile Active")
              .setDescription(`${DIVIDER}`)
              .addFields(
                { name: "Courier",   value: `\`${playerId}\``,                  inline: true },
                { name: "Server",    value: serverLabel(server),                inline: true },
                { name: "Duration",  value: tb.durationLabel ?? "?",            inline: true },
                { name: "Offense",   value: tb.reason,                          inline: false },
                { name: "By",        value: tb.moderator,                       inline: true },
                { name: "Remaining", value: `**${formatTimeLeft(tb.expires)}**`, inline: true },
                { name: "Expires",   value: `<t:${ts}:F>  ·  <t:${ts}:R>`,       inline: false },
              ), "Auto-lifted when timer expires")
          ]});
        }
        const hits = blacklistHas(playerId);   // which installs list this name in blacklist.txt
        if (tb && tb.permanent) {   // permanent ban recorded in the ban JSON
          return interaction.reply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Active")
              .setDescription(`${DIVIDER}`)
              .addFields(
                { name: "Courier", value: `\`${playerId}\``,                          inline: true },
                { name: "Offense", value: tb.reason ?? "Permanent ban",               inline: true },
                { name: "By",      value: tb.moderator ?? "?",                         inline: true },
                { name: "On file", value: hits.length ? hits.map(n => `Server ${n}`).join(" + ") : "ban JSON", inline: false },
              ), "Permanent — use /unban to lift")
          ]});
        }
        if (!hits.length) {
          return interaction.reply({ embeds: [
            clinical(new EmbedBuilder().setColor(CLIN.green).setTitle("No Exile Found")
              .setDescription(`${hero("This courier walks free.")}\n\`${playerId}\` is not on blacklist.txt on either server.`))
          ]});
        }
        return interaction.reply({ embeds: [
          clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Active")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Courier",   value: `\`${playerId}\``,                                          inline: true },
              { name: "Banned On", value: hits.map(n => `**Server ${n}**`).join("  +  "),             inline: true },
            ), "Blacklisted — use /unban to lift")
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
        await interaction.deferReply();
        const ipEnf = await banWithIp(playerId, server, { permanent: true });
        await upsertPermBan({ playerId, reason, moderator: interaction.user.tag, server });   // record in the ban JSON (supersedes any temp)
        writeModLog({ action: "permban", playerId, reason, by: interaction.user.tag, server });
        const embed = clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Permanent Exile Issued")
          .setDescription(`> *${randomQuote("ban")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "Courier",  value: `\`${playerId}\``,                                inline: true },
            { name: "Server",   value: `${serverLabel(server)}`, inline: true },
            { name: "Sentence", value: "**Permanent**",                                  inline: true },
            { name: "Offense",  value: reason,                                           inline: false },
            { name: "Admin",    value: `${interaction.user}`,                            inline: false },
          ));
        if (notes) embed.addFields({ name: "Notes", value: notes });
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
        const bans = loadBans().filter(b => !b.permanent && b.expires);   // temp bans only - leave permanent bans in place
        if (!bans.length) return interaction.reply({ embeds: [successEmbed("Registry Clear", "No active temporary exiles to remove.")], flags: MessageFlags.Ephemeral });
        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("ctb_confirm").setLabel(`Clear all ${bans.length} temp ban${bans.length !== 1 ? "s" : ""}`).setStyle(ButtonStyle.Danger),
          new ButtonBuilder().setCustomId("ctb_cancel").setLabel("Cancel").setStyle(ButtonStyle.Secondary)
        );
        await interaction.reply({
          embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Confirm Mass Clearance")
            .setDescription(`> *"Are you sure? Every exile gets pardoned."*\n\n${DIVIDER}\n` +
              `This will lift **${bans.length}** exile${bans.length !== 1 ? "s" : ""} and unban all on both servers.\n\n` +
              bans.map(b => `·  \`${b.playerId}\`  —  *${b.reason}*`).join("\n").slice(0, 3500)), "Expires in 30 seconds")],
          components: [row], flags: MessageFlags.Ephemeral,
        });
        const msg = await interaction.fetchReply();
        try {
          const btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: 30_000, filter: i => i.user.id === interaction.user.id });
          if (btn.customId === "ctb_cancel") {
            return btn.update({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Stand Down").setDescription("Clearance cancelled — all exiles remain active."))], components: [] });
          }
          await btn.deferUpdate();
          const ok = [], fail = [];
          for (const ban of bans) {
            try { unbanEverywhere(ban.playerId); ok.push(ban.playerId); }   // blacklist.txt (both installs) + IP flags
            catch { fail.push(ban.playerId); }
          }
          await removeBans(...ok);   // drop only those actually lifted; keep failures & any concurrent additions
          writeModLog({ action: "cleartempbans", count: ok.length, by: interaction.user.tag });
          const lines = [...ok.map(id => `\`${id}\``), ...fail.map(id => `\`${id}\`  — failed, kept on record`)];
          const embed = clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("Temp Bans Cleared")
            .setDescription(`> *"Clean slate."*\n\n${DIVIDER}\n**${ok.length}** released${fail.length ? `  ·  **${fail.length}** failed` : ""}\n\n${lines.join("\n")}`.slice(0, 4000))
            .addFields({ name: "By", value: `${interaction.user}`, inline: false }));
          await logBan(embed);
          return btn.editReply({ embeds: [embed], components: [] });
        } catch {
          return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Timed Out").setDescription("Confirmation expired. No changes made."))], components: [] });
        }
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

        // confirmation gate (irreversible)
        const row = new ActionRowBuilder().addComponents(
          new ButtonBuilder().setCustomId("cab_confirm").setLabel(`Unban all ${names.length}`).setStyle(ButtonStyle.Danger),
          new ButtonBuilder().setCustomId("cab_cancel").setLabel("Cancel").setStyle(ButtonStyle.Secondary),
        );
        const preview = names.slice(0, 30).map(n => `·  \`${n}\``).join("\n") + (names.length > 30 ? `\n…and ${names.length - 30} more` : "");
        const msg = await interaction.editReply({
          embeds: [clinical(new EmbedBuilder().setColor(CLIN.red).setTitle("Confirm — Pardon the Whole Mojave")
            .setDescription(`> *"A clean slate for the whole Mojave."*\n\n${DIVIDER}\n` +
              `Removes **${names.length}** courier(s) from blacklist.txt on both servers and lifts their IP/username flags. This cannot be undone.\n\n${preview}`.slice(0, 4000)), "Expires in 30 seconds")],
          components: [row],
        });
        let btn;
        try { btn = await msg.awaitMessageComponent({ componentType: ComponentType.Button, time: 30_000, filter: i => i.user.id === interaction.user.id }); }
        catch { return interaction.editReply({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Timed Out").setDescription("Confirmation expired. No bans were lifted."))], components: [] }); }
        if (btn.customId === "cab_cancel") {
          return btn.update({ embeds: [clinical(new EmbedBuilder().setColor(NV.DEAD_GREY).setTitle("Stand Down").setDescription("Cancelled — all bans remain in place."))], components: [] });
        }
        await btn.deferUpdate();

        let ok = 0, failed = 0;
        for (const n of names) {
          try { unbanEverywhere(n); ok++; }   // remove from blacklist.txt (both installs) + lift IP flags
          catch (e) { failed++; logger.warn("ClearAllBans", `Unban ${n} failed: ${e.message}`); }
        }
        await removeBans(...names);   // clear the bot's temp-ban records
        writeModLog({ action: "clearallbans", count: ok, by: interaction.user.tag });
        const embed = clinical(new EmbedBuilder().setColor(CLIN.grey).setTitle("All Exiles Pardoned")
          .setDescription(`> *"A clean slate for the whole Mojave."*\n\n${DIVIDER}\nRemoved **${names.length}** courier(s) from blacklist.txt on both servers and lifted their IP/username flags.`)
          .addFields(
            { name: "Unbanned", value: `**${ok}**${failed ? `  ·  ${failed} failed` : ""}`, inline: true },
            { name: "By",       value: `${interaction.user}`, inline: true },
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
          .setDescription(changes.join("\n"))
          .addFields({ name: "By", value: `${interaction.user}`, inline: false }).setFooter({ text: "Takes effect immediately" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
      }

      /* ─────────────────────────────────────────────────────
         ACCEPT STAFF APP  (DM the applicant + grant staff roles)
         ───────────────────────────────────────────────────── */
      case "acceptstaffapp": {
        const user = interaction.options.getUser("user");
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });

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
          .setTitle("Staff Application Accepted")
          .setDescription(RULE)
          .addFields(
            { name: "Applicant", value: `<@${user.id}>  \`${user.id}\``, inline: false },
            { name: "DM",        value: sent ? "Acceptance DM delivered" : "Couldn't DM (DMs closed / bot blocked)", inline: false },
            { name: "Roles",     value: rolesGranted ? `Granted <@&${STAFF_ROLE_IDS[0]}> & <@&${STAFF_ROLE_IDS[1]}>` : `Could not grant roles — ${roleErr || "check the bot's role position & Manage Roles permission"}`, inline: false },
            { name: "Announced",  value: announced ? `Posted in <#${STAFF_ANNOUNCE_CHANNEL}>` : `Couldn't post announcement — ${announceErr || "check the channel ID & bot permissions"}`, inline: false },
            { name: "By",        value: `${interaction.user}`, inline: false },
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
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });

        const dm = new EmbedBuilder().setColor(NV.NCR_TAN)
          .setTitle("Nuclear RP — Staff Application Update")
          .setDescription(
            `Thanks for applying to the Nuclear RP staff team.\n\n` +
            `After review, we've decided **not to accept your application at this time.** ` +
            `You're welcome to apply again when applications reopen.\n\n` +
            `Thanks for your interest.`
          )
          .setFooter({ text: "Nuclear RP Staff Team" });
        if (reason) dm.addFields({ name: "Note from the team", value: reason });
        const sent = await dmEmbed(user, dm);

        // Deny does nothing else - no roles, no logging beyond this reply.
        const embed = new EmbedBuilder().setColor(NV.NCR_TAN)
          .setTitle("Staff Application Denied")
          .setDescription(RULE)
          .addFields(
            { name: "Applicant", value: `<@${user.id}>  \`${user.id}\``, inline: false },
            { name: "DM",        value: sent ? "Denial DM delivered" : "Couldn't DM (DMs closed / bot blocked)", inline: false },
            { name: "By",        value: `${interaction.user}`, inline: false },
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
            { perPage: 20, flags: MessageFlags.Ephemeral });
        }

        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });

        if (sub === "add") {
          const { ok, already } = addDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], flags: MessageFlags.Ephemeral });
          if (already) return interaction.reply({ embeds: [warningEmbed("Already a Donator", `\`${playerId}\` is already in the donator file.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "donator-add", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Donator Added")
            .setDescription(`> *"A generous soul joins the ranks of the Strip's patrons."*\n\n${DIVIDER}`)
            .addFields(
              { name: "Courier", value: `\`${playerId}\``,        inline: true },
              { name: "Added By", value: `${interaction.user}`,   inline: true },
              { name: "File",     value: `\`${DONATOR_FILE}\``,   inline: false },
            ).setFooter({ text: "Written to the donator file." }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
        }

        if (sub === "remove") {
          const { ok, missing } = removeDonator(playerId);
          if (!ok) return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not write to the donator file.\n\`${DONATOR_FILE}\`\nCheck the path and file permissions.`)], flags: MessageFlags.Ephemeral });
          if (missing) return interaction.reply({ embeds: [warningEmbed("Not a Donator", `\`${playerId}\` is not in the donator file.`)], flags: MessageFlags.Ephemeral });
          writeModLog({ action: "donator-remove", playerId, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Donator Removed")
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Courier",   value: `\`${playerId}\``,      inline: true },
              { name: "Removed By", value: `${interaction.user}`, inline: true },
            ).setFooter({ text: "Removed from the donator file." }).setTimestamp();
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
        const { s1, s2 } = await sendRconBoth(`Notify ${target} ${message}`, server);
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
        const a1 = ackOne(s1);
        const a2 = ackOne(s2);
        const acks = [a1, a2].filter(v => v !== null);
        const allOk  = acks.length > 0 && acks.every(Boolean);
        const anyOk  = acks.some(Boolean);
        writeModLog({ action: "announce", message, target, by: interaction.user.tag, server, delivered: allOk });
        const deliveryNote = allOk
          ? "Sent via RCON `Notify` — visible in-game if your build supports it."
          : anyOk
            ? "One server may not support `Notify`. Message logged here regardless."
            : "Server gave no acknowledgement — your Pavlov build may not support `Notify`. Message logged here only.";
        const embed = new EmbedBuilder().setColor(allOk ? NV.BLUE_VATS : NV.NCR_TAN).setTitle("Broadcast Sent")
          .setDescription(`> *${randomQuote("announce")}*\n\n${DIVIDER}`)
          .addFields(
            { name: "Message",  value: `> ${message}`,                                     inline: false },
            { name: "Target",   value: isAll ? "**All players**" : `\`${target}\``,         inline: true },
            { name: "Server",   value: `${serverLabel(server)}`,   inline: true },
            { name: "By",       value: `${interaction.user}`,                              inline: true },
            { name: "Delivery", value: deliveryNote,                                       inline: false },
          ).setFooter({ text: "RCON Notify broadcast" }).setTimestamp();
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
          .setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Courier", value: `\`${playerId}\``,            inline: true },
            { name: "Server",  value: `${serverLabel(server)}`,    inline: true },
            { name: "Menu",    value: menuMeta?.name ?? menuValue,  inline: true },
            { name: "Granted By", value: `${interaction.user}`,     inline: false },
            { name: "Persistence", value: "Recorded for tracking. Not re-applied automatically on rejoin.", inline: false },
          ).setTimestamp();
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
          await sendRconBoth(`RemoveAccessManager ${target}`, server);
          applied.push(`RemoveAccessManager ${target}`);
        }
        // Clear every menu grant record for this player on the affected server(s).
        for (const m of MENUS) {
          if (server === "both") { removeMenuGrant(playerId, "server1", m.value); removeMenuGrant(playerId, "server2", m.value); }
          removeMenuGrant(playerId, server, m.value);
        }
        const embed = brand(new EmbedBuilder().setColor(NV.NCR_TAN)
          .setTitle("Menu Access Revoked")
          .setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Courier", value: `\`${playerId}\``,         inline: true },
            { name: "Server",  value: `${serverLabel(server)}`, inline: true },
            { name: "Revoked By", value: `${interaction.user}`,  inline: false },
            { name: "Applied", value: `\`\`\`\n${applied.join("\n")}\n\`\`\``, inline: false },
          ).setTimestamp());
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
        for (const pid of holders) for (const m of MENUS) {
          removeMenuGrant(pid, "server1", m.value);
          removeMenuGrant(pid, "server2", m.value);
          removeMenuGrant(pid, "both", m.value);
        }

        const embed = brand(new EmbedBuilder().setColor(NV.LEGION_RED)
          .setTitle("Mass Menu Revocation")
          .setDescription(hero("Cleared menu access for every courier on both servers."))
          .addFields(
            { name: "Applied",        value: "`ClearMenuAccess`\n`ClearAccessManagers`", inline: true },
            { name: "Server",         value: "Both servers",                      inline: true },
            { name: "Grants cleared", value: `**${holders.length}**`,             inline: true },
          ).setTimestamp());
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
         SYNCFACTIONROLES — apply role→rank whitelists now
         ───────────────────────────────────────────────────── */

      /* ─────────────────────────────────────────────────────
         SETWHITELISTCHANNEL — post the faction whitelist panel here

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
            logAction(e1);
            return sub.editReply({ embeds: [e1] });
          }
          if (choice === "wipe_money") {
            await sub.deferReply({ flags: MessageFlags.Ephemeral });
            if (val.toUpperCase() !== "WIPE") return sub.editReply({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **WIPE** to confirm — no money was wiped."))] });
            const r = wipeAllMoney();
            const e = brand(new EmbedBuilder().setColor(NV.LEGION_RED).setTitle("Money Wiped")
              .setDescription(hero(r.ok ? `Set **${r.wiped}** of ${r.total} player balance(s) to **0**.` : `Wipe failed: ${r.error}`)).setTimestamp());
            logAction(e);
            return sub.editReply({ embeds: [e] });
          }
          if (choice === "load_factions") {
            await sub.deferReply({ flags: MessageFlags.Ephemeral });
            if (val.toUpperCase() !== "LOAD") return sub.editReply({ embeds: [brand(new EmbedBuilder().setColor(NV.NCR_TAN).setTitle("Cancelled").setDescription("Type **LOAD** to confirm — nothing was restored."))] });
            const r = loadFactionBackup();
            const e = brand(new EmbedBuilder().setColor(NV.AMBER).setTitle("Faction Whitelists Restored")
              .setDescription(hero(r.ok ? `Restored **${r.restored}** faction file(s)${r.savedAt ? ` from the snapshot saved <t:${Math.floor(r.savedAt / 1000)}:R>` : ""}.` : (r.empty ? "No saved snapshot found — use **Save faction whitelists** first." : `Load failed: ${r.error}`))).setTimestamp());
            logAction(e);
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
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Faction",      value: faction,                                                       inline: true },
              { name: "New Cap",       value: `**${cap}** members`,                                          inline: true },
              { name: "Current Size",  value: `${current} / ${cap}${current > cap ? "  over cap!" : ""}`, inline: true },
              { name: "Set By",        value: `${interaction.user}`,                                          inline: false },
            ).setFooter({ text: "Cap enforced on /faction add" }).setTimestamp();
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
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Faction",     value: faction,                                                              inline: true },
              { name: "Rank",        value: rankBadge(faction, rank),                                             inline: true },
              { name: "New Cap",      value: capStr,                                                               inline: true },
              { name: "Currently",    value: `${current}${cap > 0 ? ` / ${cap}${current > cap ? "  over cap!" : ""}` : ""}`, inline: true },
              { name: "Set By",       value: `${interaction.user}`,                                                inline: false },
            ).setFooter({ text: cap > 0 ? "Cap enforced on add / rank / transfer" : "Rank is now uncapped" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed], flags: MessageFlags.Ephemeral });
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
          const ACTION_ICONS = { "add": "", "remove": "", "rank": "", "transfer-in": "", "transfer-out": "" };
          const lines = allAudit.map(e => {
            const ts     = Math.floor(e.at / 1000);
            const icon   = ACTION_ICONS[e.action] ?? "";
            const detail = e.rank ? ` → **${e.rank}**` : e.oldRank ? ` *(was ${e.oldRank})*` : "";
            return `${icon}  \`${e.action}\`  **${e.playerId}**${detail}  ·  by *${e.by}*  ·  <t:${ts}:R>`;
          });
          return paginate(interaction, lines, (pageLines) =>
            new EmbedBuilder().setColor(NV.AMBER)
              .setTitle(`${faction} — Audit Log`)
              .setDescription(`**${allAudit.length}** total changes *(newest first)*\n\n${DIVIDER}\n${pageLines.join("\n")}`),
            { perPage: 15, flags: MessageFlags.Ephemeral });
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
            removePlayerFromRankFile(faction, playerId, rank);
          } else {
            if (had.includes(rank)) {
              return interaction.reply({ embeds: [warningEmbed("Already Holds Rank", `\`${playerId}\` already holds **${rank}** in **${faction}**.\n\nThey hold: ${had.join(", ")}.`)], flags: MessageFlags.Ephemeral });
            }
            const room = rankHasRoom(faction, rank);   // a member can hold MULTIPLE ranks; cap is per rank file
            if (!room.ok) {
              return interaction.reply({ embeds: [errorEmbed("Rank Full",
                `**${rank}** in **${faction}** is at its cap (**${room.count}/${room.cap}**).\n\nRaise the cap with \`/faction setrankcap\`.`)], flags: MessageFlags.Ephemeral });
            }
            addPlayerToRankFile(faction, playerId, rank);
          }
          const now = getPlayerRanks(faction, playerId);
          await setFactionRank(faction, playerId, now[now.length - 1] ?? getFactionDefaultRank(faction));   // track highest as primary
          writeFactionAudit({ action: "rank", faction, playerId, rank: removing ? `-${rank}` : rank, by: interaction.user.tag });
          writeModLog({ action: removing ? "faction-unrank" : "faction-rank", playerId, faction, rank, by: interaction.user.tag });
          const rankFile = getFactionRankConfig(faction)?.rankFiles[rank] ?? "n/a";
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle(removing ? "Faction Rank Removed" : "Faction Rank Added")
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(
              { name: "Courier",      value: `\`${playerId}\``,                inline: true },
              { name: "Faction",      value: faction,                          inline: true },
              { name: removing ? "Removed Rank" : "Added Rank", value: rankBadge(faction, rank), inline: true },
              { name: "Holds Now",    value: now.length ? now.map(r => `**${r}**`).join(", ") : "*no ranks*", inline: false },
              { name: "By",           value: `${interaction.user}`,            inline: true },
              { name: "Rank File",    value: `${removing ? "Removed from" : "Added to"} \`${rankFile}\``, inline: true },
            ).setFooter({ text: "Members can hold multiple ranks · rank files updated on disk" }).setTimestamp();
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
            writeFactionFile(fromSpawn, fromLines);   // fromLines still has the player once - restore original (no dup)
            addPlayerToRankFile(fromFaction, playerId, oldRank);
            await setFactionRank(fromFaction, playerId, oldRank);
            return interaction.reply({ embeds: [errorEmbed("Write Failed", `Could not update \`${toSpawn}\`. Transfer rolled back.`)], flags: MessageFlags.Ephemeral });
          }
          addPlayerToRankFile(toFaction, playerId, newRank);
          await setFactionRank(toFaction, playerId, newRank);
          writeFactionAudit({ action: "transfer-out", faction: fromFaction, playerId, oldRank, by: interaction.user.tag });
          writeFactionAudit({ action: "transfer-in",  faction: toFaction,   playerId, rank: newRank, by: interaction.user.tag });
          writeModLog({ action: "faction-transfer", playerId, fromFaction, toFaction, oldRank, newRank, by: interaction.user.tag });
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Faction Transfer Complete")
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(
              { name: "Courier",       value: `\`${playerId}\``,                                          inline: true },
              { name: "From",           value: `**${fromFaction}**  *(${rankBadge(fromFaction, oldRank)})*`, inline: true },
              { name: "To",             value: `**${toFaction}**  *(${rankBadge(toFaction, newRank)})*`,   inline: true },
              { name: "New Roster Size",value: `${toNow.length} / ${toCap}`,                              inline: true },
              { name: "Transferred By", value: `${interaction.user}`,                                     inline: true },
              { name: "Rank Files",     value: `Cleared from **${fromFaction}** rank files\nAdded to \`${getFactionRankConfig(toFaction)?.rankFiles[newRank] ?? "n/a"}\``, inline: false },
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
            .setDescription(`> *${randomQuote("faction")}*\n\n${DIVIDER}`)
            .addFields(
              { name: "Courier",       value: `\`${playerId}\``,            inline: true },
              { name: "Faction",       value: faction,                      inline: true },
              { name: "Starting Rank", value: rankBadge(faction, rank),     inline: true },
              { name: "Roster Size",    value: `${lines.length} / ${cap}`,  inline: true },
              { name: "Added By",       value: `${interaction.user}`,       inline: true },
              { name: "Rank File",      value: `\`${rankFile}\``,           inline: true },
            ).setFooter({ text: "Main spawn file + rank file updated · audit logged" }).setTimestamp();
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
            .setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Courier",    value: `\`${playerId}\``,             inline: true },
              { name: "Faction",    value: faction,                       inline: true },
              { name: "Was",         value: rankBadge(faction, oldRank),  inline: true },
              { name: "Roster Size", value: `${lines.length} / ${cap}`,   inline: true },
              { name: "Removed By",  value: `${interaction.user}`,        inline: true },
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
        await interaction.deferReply({ flags: MessageFlags.Ephemeral });
        try {
          if (server === "both") {
            // allSettled so one unreachable server doesn't fail the whole command
            const [s1, s2] = await Promise.allSettled([sendRcon(command, "server1"), sendRcon(command, "server2")]);
            const fmt = (r) => r.status === "fulfilled" ? ((r.value.trim() || "no response").slice(0, 900)) : `unreachable: ${r.reason?.message || r.reason}`;
            writeModLog({ action: "manual-rcon", command, server, by: interaction.user.tag });
            return interaction.editReply({ embeds: [
              new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("Raw RCON — Both Servers").setDescription(`${DIVIDER}`)
                .addFields(
                  { name: "Signal",             value: `\`\`\`${command}\`\`\``,           inline: false },
                  { name: "Server 1 Response",  value: `\`\`\`${fmt(s1)}\`\`\``,           inline: false },
                  { name: "Server 2 Response",  value: `\`\`\`${fmt(s2)}\`\`\``,           inline: false },
                  { name: "By",                  value: `${interaction.user}`,             inline: false },
                ).setTimestamp()
            ]});
          }
          const result = await sendRcon(command, server);
          writeModLog({ action: "manual-rcon", command, server, by: interaction.user.tag });
          await logAction(new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("Manual RCON")
            .addFields({ name: "Signal", value: `\`${command}\``, inline: true }, { name: "Server", value: serverLabel(server), inline: true }, { name: "By", value: interaction.user.tag, inline: true }).setTimestamp());
          return interaction.editReply({ embeds: [
            new EmbedBuilder().setColor(NV.BLUE_VATS).setTitle("RCON Transmission Complete").setDescription(`${DIVIDER}`)
              .addFields(
                { name: "Signal",  value: `\`\`\`${command}\`\`\``,                                             inline: false },
                { name: "Server",  value: `${serverLabel(server)}`,                     inline: true },
                { name: "By",      value: `${interaction.user}`,                                                inline: true },
                { name: "Response",value: `\`\`\`${(result.trim() || "no response").slice(0, 1000)}\`\`\``,    inline: false },
              ).setTimestamp()
          ]});
        } catch (err) {
          return interaction.editReply({ embeds: [errorEmbed("RCON Failed", `Cannot reach **${serverLabel(server)}**.\n\`\`\`${err.message}\`\`\`\nCheck \`/ping\` for server status.`)] });
        }
      }

      /* ─────────────────────────────────────────────────────
         ROTATEMAP
         ───────────────────────────────────────────────────── */

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
            .setDescription(`> *"Caps now. No strings attached."*\n\n${DIVIDER}`)
            .addFields(
              { name: "Courier",    value: `\`${playerId}\``,                          inline: true },
              { name: "Payment",    value: `**${tier.amount.toLocaleString()} caps**`, inline: true },
              { name: "New Balance",value: `**${newBal.toLocaleString()} caps**`,      inline: true },
              { name: "By",        value: `${interaction.user}`,                      inline: false },
            ).setFooter({ text: randomQuote("caps") }).setTimestamp();
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
          const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Payroll Tier Updated").setDescription(`${DIVIDER}`)
            .addFields(
              { name: "Courier",value: `\`${playerId}\``,                                    inline: true },
              { name: "Old",    value: `${old?.label ?? "?"} (+${old?.amount ?? "?"}/wk)`,   inline: true },
              { name: "New",    value: `**${tier.label}** (+${tier.amount}/wk)`,             inline: true },
              { name: "By",     value: `${interaction.user}`,                                inline: false },
            ).setFooter({ text: "Payroll updated — takes effect next cycle" }).setTimestamp();
          brand(embed); await logAction(embed);
          return interaction.reply({ embeds: [embed] });
        }
        wages.push({ playerId, tier: tierKey, addedBy: interaction.user.tag, addedAt: Date.now(), lastPaidAt: null, updatedAt: null, updatedBy: null });
        saveWages(wages);
        const bal = readPlayerBalance(playerId);
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Courier Added to Payroll")
          .setDescription(`> *"A fair day's work for a fair day's pay."*\n\n${DIVIDER}`)
          .addFields(
            { name: "Courier",    value: `\`${playerId}\``,                                              inline: true },
            { name: "Tier",       value: `**${tier.label}**`,                                            inline: true },
            { name: "Weekly",     value: `+**${tier.amount} caps/week**`,                                inline: true },
            { name: "Balance",    value: bal !== null ? `${bal.toLocaleString()} caps` : "*no ledger*", inline: true },
            { name: "By",        value: `${interaction.user}`,                                          inline: true },
            { name: "Next Payout",value: "Within 7 days of enrolment",                                  inline: true },
          ).setFooter({ text: randomQuote("wages") }).setTimestamp();
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
          { perPage: 12, flags: MessageFlags.Ephemeral });
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
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Courier Ledger").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Courier", value: `\`${playerId}\``,                          inline: true },
            { name: "Balance", value: `**${balance.toLocaleString()} caps**`,      inline: true },
            { name: "Payroll", value: wTier ? `${wTier.label} (+${wTier.amount}/wk)` : "Not enrolled", inline: true },
          ).setFooter({ text: randomQuote("caps") }).setTimestamp();
        if (nextTs) embed.addFields({ name: "Next Payout", value: `<t:${nextTs}:R>`, inline: true });
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
        const embed = new EmbedBuilder().setColor(NV.GOLD).setTitle("Caps Given").setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Courier",    value: `\`${playerId}\``,                      inline: true },
            { name: "Given",      value: `**+${amount.toLocaleString()} caps**`, inline: true },
            { name: "New Balance",value: `**${newBal.toLocaleString()} caps**`,  inline: true },
            { name: "Reason",     value: reason,                                 inline: false },
            { name: "By",        value: `${interaction.user}`,                  inline: false },
          ).setFooter({ text: randomQuote("caps") }).setTimestamp();
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
          .setTitle(`Caps ${pos ? "Credited" : "Debited"}`).setDescription(`${DIVIDER}`)
          .addFields(
            { name: "Courier",     value: `\`${playerId}\``,                                    inline: true },
            { name: `${pos ? "" : ""}  Change`,value: `**${pos ? "+" : ""}${amount.toLocaleString()} caps**`, inline: true },
            { name: "New Balance", value: `**${newBal.toLocaleString()} caps**`,                inline: true },
            { name: "Reason",      value: reason,                                               inline: false },
            { name: "By",         value: `${interaction.user}`,                                inline: false },
          ).setFooter({ text: "Manual cap adjustment · logged" }).setTimestamp();
        brand(embed); await logAction(embed);
        return interaction.reply({ embeds: [embed] });
      }

      /* ─────────────────────────────────────────────────────
         STATS
         ───────────────────────────────────────────────────── */
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

      case "stats": {
        const playerId = sanitizeId(interaction.options.getString("playerid"));
        if (!playerId) return interaction.reply({ embeds: [emptyIdEmbed()], flags: MessageFlags.Ephemeral });
        const playtime = loadPlaytime();
        const minutes  = playtime[playerId] ?? null;
        const factions = getPlayerFactions(playerId);
        const onS1     = playerCache.server1.some(n => n.toLowerCase() === playerId.toLowerCase());
        const onS2     = playerCache.server2.some(n => n.toLowerCase() === playerId.toLowerCase());
        const online   = onS1 || onS2;
        const balance  = readPlayerBalance(playerId);
        const wage     = loadWages().find(w => w.playerId.toLowerCase() === playerId.toLowerCase());
        const wTier    = wage ? WAGE_TIERS[wage.tier] : null;
        const tb       = loadBans().find(b => b.playerId.toLowerCase() === playerId.toLowerCase());
        const history  = getPlayerHistory(playerId);
        const lastSeen = getLastSeen(playerId);
        const donator  = isDonator(playerId);

        const fStr = factions === null ? "Folder unreadable"
          : !factions.length ? "*No faction access*"
          : factions.map(f => {
              const rank = getFactionRank(f, playerId);
              return `${getFactionRankBadge(f, rank)}  **${f}** *(${rank})*`;
            }).join("\n");

        const statusStr = !online ? "Offline" : [onS1 && "Server 1", onS2 && "Server 2"].filter(Boolean).join("  +  ");
        const color = tb ? NV.RUST_RED : online ? NV.IRRAD_GREEN : NV.AMBER;

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
  loadBans, upsertTempBan, upsertPermBan, removeBans,
  // donators
  DONATOR_FILE, readDonatorFile, writeDonatorFile, isDonator, addDonator, removeDonator,
  // owner / access
  isOwner, isBlacklisted,
  // ui / parsing helpers
  splitPages, extractPlayerNames, bar, dmStatusField,
  // faction rank caps
  getFactionRankCap, getFactionRankCaps, setFactionRankCap, getFactionCap, setFactionCap,
  // faction file safety
  readFactionFile, writeFactionFile, FACTION_BULK_DROP_LIMIT, FACTION_ROLES_PATH,
  // modsave sync
  syncAllModSave,
  // rcon menu roles
  loadMenuRoles, setMenuRole,
};
