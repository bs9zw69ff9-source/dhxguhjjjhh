// Mojave Authority - our Pavlov VR moderation bot for the New Vegas RP servers.
// (c) 2026 bs9zw69ff9-source. Private project, please don't redistribute.
require("dotenv").config();
const fs     = require("fs");
const net    = require("net");
const path   = require("path");
const { execFileSync, execFile, spawn } = require("child_process");
const Database = require("better-sqlite3");
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
  AUTOROTATE:     "./autorotate.json",
  MUTES:          "./mutes.json",
  DISCORD_LINKS:  "./discord_links.json",
  AUTOBAN_EXEMPT: "./autoban_exempt.json",
  CASINO_CONFIG:  "./casino_config.json",
  UPDATE_LOG_STATE: "./update_log_state.json",
  BAN_RECONCILE_STATE: "./ban_reconcile_state.json",
  CASINO_QUOTA: "./casino_quota.json",
  CASINO_POT: "./casino_pot.json",
  AUTOPOST_STATE: "./autopost_state.json",
  VPN_CHECKS: "./vpn_checks.json",
  DONATOR_SUSPEND: "./donator_suspend.json",
};

// Bet limits and the global casino cooldown are all admin-tunable via /casino
// (they're the "configure later" knobs the payout tables were built against).
const CASINO_CONFIG_DEFAULTS = { enabled: true, minBet: 10, maxBet: 2000, cooldownMs: 4000 };

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
  [FILES.AUTOROTATE]:     "{}",
  [FILES.MUTES]:          "{}",
  [FILES.DISCORD_LINKS]:  "{}",
  [FILES.AUTOBAN_EXEMPT]: "{}",
  [FILES.DONATOR_SUSPEND]: "{}",
  [FILES.ROLES]:          JSON.stringify({ modRoleId: "", adminRoleId: "", factionLeaderRoleId: "" }, null, 2),
  [FILES.CASINO_CONFIG]:  JSON.stringify(CASINO_CONFIG_DEFAULTS, null, 2),
};

// ---- storage: SQLite (bot.db) with a one-time import from the JSON files ----
// Every dataset that used to be a JSON file is now a row in bot.db. The .json files are left
// untouched as a backup snapshot from migration time. safeRead/safeWrite/update and all the
// typed loaders are unchanged — only the low-level _rawRead/_rawWrite below now hit SQLite.
const db = new Database(path.join(__dirname, "bot.db"));
db.pragma("journal_mode = WAL");
db.pragma("busy_timeout = 5000");
db.exec("CREATE TABLE IF NOT EXISTS kv (file TEXT PRIMARY KEY, data TEXT NOT NULL)");
const _kvGet = db.prepare("SELECT data FROM kv WHERE file = ?");
const _kvSet = db.prepare("INSERT INTO kv(file,data) VALUES(?,?) ON CONFLICT(file) DO UPDATE SET data=excluded.data");
const _kvHas = (file) => _kvGet.get(file) !== undefined;

// One-time full data transfer: import every existing JSON data file into the DB (the JSON
// files stay as a backup), then seed defaults for anything still missing.
let _migrated = 0;
for (const file of Object.values(FILES)) {
  if (_kvHas(file)) continue;
  if (fs.existsSync(file)) {
    try { const raw = fs.readFileSync(file, "utf8"); JSON.parse(raw); _kvSet.run(file, raw); _migrated++; continue; }
    catch (e) { logger.warn("DB", `Could not migrate ${file}: ${e.message}`); }
  }
  if (DEFAULTS[file] !== undefined) _kvSet.run(file, DEFAULTS[file]);
}
if (_migrated) logger.info("DB", `Migrated ${_migrated} JSON dataset(s) into bot.db (JSON files kept as backup)`);

// ---- read/write through SQLite  +  in-memory cache  +  mutation serialization ----
const _cache  = new Map(); // file -> parsed value
const _queues = new Map(); // file -> Promise (tail of the per-file chain)

function _rawRead(file, fallback) {
  const row = _kvGet.get(file);
  if (row !== undefined) {
    try { return JSON.parse(row.data); }
    catch (err) { logger.warn("IO", `DB parse failed for ${file}: ${err.message}`); return fallback; }
  }
  // Not in the DB yet — import the JSON backup if present, else seed the fallback.
  if (fs.existsSync(file)) {
    try { const raw = fs.readFileSync(file, "utf8"); const val = JSON.parse(raw); _kvSet.run(file, raw); return val; } catch {}
  }
  _kvSet.run(file, JSON.stringify(fallback === undefined ? {} : fallback));
  return fallback;
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
  try { _kvSet.run(file, JSON.stringify(data)); return true; }
  catch (err) { logger.error("IO", `DB write failed for ${file}: ${err.message}`); return false; }
}

/* Periodic SQLite -> JSON export. SQLite is the source of truth; this keeps the .json
   files a current, human-readable backup (otherwise they'd be frozen at migration time).
   Covers BOTH index.js and ipBans datasets since they share bot.db. Atomic (temp+rename).
   Set DB_EXPORT_INTERVAL_MS=0 to disable. */
const DB_EXPORT_INTERVAL_MS = process.env.DB_EXPORT_INTERVAL_MS !== undefined
  ? Number(process.env.DB_EXPORT_INTERVAL_MS)
  : 10 * 60 * 1000;
function exportDbToJson() {
  let n = 0;
  try {
    for (const { file, data } of db.prepare("SELECT file, data FROM kv").all()) {
      try {
        let pretty = data; try { pretty = JSON.stringify(JSON.parse(data), null, 2); } catch {}
        const tmp = `${file}.exp.${process.pid}.tmp`;
        fs.writeFileSync(tmp, pretty);
        fs.renameSync(tmp, file);              // atomic swap over the backup file
        n++;
      } catch (e) { logger.warn("DB", `JSON export failed for ${file}: ${e.message}`); }
    }
  } catch (e) { logger.warn("DB", `JSON export failed: ${e.message}`); return 0; }
  logger.debug("DB", `Exported ${n} dataset(s) to JSON backups`);
  return n;
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
  "Kings": {
    order:   ["Prospect", "Silver Ace", "Guard", "High Roller", "Crown", "The King"],
    default: "Prospect",
    badges:  {
      "Prospect":    "",
      "Silver Ace":  "",
      "Guard":       "",
      "High Roller": "",
      "Crown":       "",
      "The King":    "",
    },
    rankFiles: {
      "Prospect":    "kingsprospect.txt",
      "Silver Ace":  "kingssilverace.txt",
      "Guard":       "kingsguard.txt",
      "High Roller": "kingshighroller.txt",
      "Crown":       "kingscrown.txt",
      "The King":    "kingstheking.txt",
    },
  },
  "Super Mutants": {
    order:   ["Suicider", "Infantry", "Sergeant", "Nightkin", "Bombardier", "Behemoth"],
    default: "Suicider",
    badges:  {
      "Suicider":   "",
      "Infantry":   "",
      "Sergeant":   "",
      "Nightkin":   "",
      "Bombardier": "",
      "Behemoth":   "",
    },
    rankFiles: {
      "Suicider":   "supermutantsuicider.txt",
      "Infantry":   "supermutantinfantry.txt",
      "Sergeant":   "supermutantsergeant.txt",
      "Nightkin":   "supermutantnightkin.txt",
      "Bombardier": "supermutantbombardier.txt",
      "Behemoth":   "supermutantbehemoth.txt",
    },
  },
  "Followers": {
    order:   ["Follower", "Guard", "Recruit", "Scholar", "Director"],
    default: "Follower",
    badges:  {
      "Follower": "",
      "Guard":    "",
      "Recruit":  "",
      "Scholar":  "",
      "Director": "",
    },
    rankFiles: {
      "Follower": "followersfollower.txt",
      "Guard":    "followersguard.txt",
      "Recruit":  "followersrecruit.txt",
      "Scholar":  "followersscholar.txt",
      "Director": "followersdirector.txt",
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
  casino:  [
    '"The house always wins. Eventually."',
    '"Mr. House built the Tops on losing streaks just like yours."',
    '"Fortune favors the bold — and the House favors the odds."',
    '"Every chip on this table has a story. Most of them end badly."',
    '"Luck is just another word for a spin no one\'s rigged yet."',
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

// ---- embed builders ----
const DIVIDER = "────────────────────────────";
const RULE    = "╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌╌";
const BRAND_NAME = "Mojave Authority";
// One tasteful, monochrome glyph set — status accents on titles, no cartoon emoji.
const GLYPH = { ok: "✓", bad: "✕", warn: "⚠", deny: "⊘", info: "▸", dot: "•", up: "●", down: "○", caps: "◈", rank: "◆" };

// ---- visual system  (consistent branding across every embed) ----
function brandIcon() { try { return client.user?.displayAvatarURL?.({ size: 128 }) ?? null; } catch { return null; } }

/** Stamp an embed with the bot's identity: author header (+ avatar),
    timestamp, thumbnail, and a subtle version footer unless one is set. */
function brand(embed, { thumb = false, footer } = {}) {
  const icon = brandIcon();
  embed.setAuthor(icon ? { name: BRAND_NAME, iconURL: icon } : { name: BRAND_NAME });
  if (thumb && icon) embed.setThumbnail(icon);
  const f = footer ? (typeof footer === "string" ? { text: footer } : footer) : { text: `${BRAND_NAME} · ${BUILD_ID}` };
  if (icon && !f.iconURL) f.iconURL = icon;
  embed.setFooter(f);
  embed.setTimestamp();
  clampEmbed(embed);
  return embed;
}
// Keep any embed inside Discord's hard API limits so a long field can never
// reject the whole message (title 256, desc 4096, field name 256/value 1024).
function clampEmbed(embed) {
  try {
    const d = embed.data;
    if (d.title) embed.setTitle(String(d.title).slice(0, 256));
    if (d.description) embed.setDescription(String(d.description).slice(0, 4096));
    if (Array.isArray(d.fields)) {
      for (const f of d.fields) {
        if (f.name  && f.name.length  > 256)  f.name  = f.name.slice(0, 253)  + "…";
        if (f.value && f.value.length > 1024) f.value = f.value.slice(0, 1021) + "…";
      }
    }
  } catch {}
  return embed;
}

/** Fine-grained progress bar — smooth 1/8-cell fill, e.g. ██████▍░░░░░ */
const _BAR_FRAC = ["", "▏", "▎", "▍", "▌", "▋", "▊", "▉"];
function bar(value, max, width = 12) {
  const ratio  = max > 0 ? Math.max(0, Math.min(1, value / max)) : 0;
  // Work in 1/8-cell units and carry a rounded-up fraction into a full block,
  // otherwise a fraction that rounds to 8/8 renders as EMPTY and a higher value
  // can draw a shorter bar than a lower one.
  const eighths = Math.round(ratio * width * 8);
  const full    = Math.floor(eighths / 8);
  const frac    = _BAR_FRAC[eighths % 8] || "";
  const used    = full + (frac ? 1 : 0);
  return "█".repeat(full) + frac + "░".repeat(Math.max(0, width - used));
}
/** Labeled meter: `██████▍░░░░░  n/max (p%)` — for dashboards and rosters. */
function meter(value, max, width = 12) {
  const pct = max > 0 ? Math.round((value / max) * 100) : 0;
  return `\`${bar(value, max, width)}\`  **${value}/${max}** *(${pct}%)*`;
}
const pip = (ok) => (ok ? GLYPH.up : GLYPH.down);

// Fixed-width cell for lining up columns inside a monospace code block.
const cell = (v, w) => { const s = String(v); return s.length > w ? s.slice(0, w - 1) + "…" : s.padEnd(w); };

/* A blockquote-styled hero line used at the top of feature embeds. */
function hero(quoteText) { return `> *${quoteText}*`; }

/* Ban / IP embeds: stamp them with the full Mojave Authority branding (author
   header, avatar, timestamp) + an optional footer — same look as everything else. */
function clinical(embed, footer) {
  return brand(embed, footer ? { footer } : {});
}

// ---- embed builders ----
function successEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.IRRAD_GREEN)
    .setTitle(`${GLYPH.ok}  ${title}`)
    .setDescription(String(description)));
}
function errorEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.RUST_RED)
    .setTitle(`${GLYPH.bad}  ${title}`)
    .setDescription(String(description)),
    { footer: { text: "Incident logged · Securitron network active" } });
}
function warningEmbed(title, description) {
  return brand(new EmbedBuilder().setColor(NV.AMBER)
    .setTitle(`${GLYPH.warn}  ${title}`)
    .setDescription(String(description)));
}
/* Shared "you can't run this" card — one look for every access gate. */
function deniedEmbed(title, description, footer = "Unauthorized access attempt logged") {
  return brand(new EmbedBuilder().setColor(NV.LEGION_RED)
    .setTitle(`${GLYPH.deny}  ${title}`)
    .setDescription(String(description)),
    { footer: { text: footer } });
}
function adminOnlyEmbed()  { return deniedEmbed("Administrators Only", "This command is restricted to **Administrators**."); }
function ownerOnlyEmbed()  { return deniedEmbed("Owner Only", "This command is restricted to the **bot owner**."); }
function modOnlyEmbed()    { return deniedEmbed("Moderators Only", "This command requires the **Moderator** role.", "Access restricted"); }
function factionLeaderOnlyEmbed()   { return deniedEmbed("Faction Leaders Only", "Requires the **Faction Leader** role (or Moderator).", "Access restricted"); }
function factionLeaderStrictEmbed() { return deniedEmbed("Faction Leaders Only", "Rank changes require the **Faction Leader** role specifically.", "Access restricted"); }
function blacklistedEmbed(entry) {
  const reason = entry?.reason ? `\n\n**Reason:** ${entry.reason}` : "";
  return deniedEmbed("Access Revoked", `You've been **blacklisted** from this bot — every command is unavailable to you.${reason}`,
    "Contact an administrator if you believe this is a mistake");
}
function emptyIdEmbed() {
  return warningEmbed("Courier ID Required",
    "Enter a valid **Courier ID** or username.\n-# Start typing in the player field — autocomplete surfaces anyone online, and manual IDs work for offline players.");
}
function rateLimitEmbed() {
  return warningEmbed("Slow Down", "You're issuing commands too quickly — wait a moment and try again.");
}

// ---- rcon ----
// Servers come online by setting RCON_HOST_N (+ port/password). Everything that
// fans out over "all servers" iterates ACTIVE_SERVERS so adding a server is just env.
const hasServer2 = !!process.env.RCON_HOST_2;
const hasServer3 = !!process.env.RCON_HOST_3;
const ACTIVE_SERVERS = ["server1", ...(hasServer2 ? ["server2"] : []), ...(hasServer3 ? ["server3"] : [])];
function getServerConfig(server) {
  if (server === "server2") return {
    host: process.env.RCON_HOST_2, port: Number(process.env.RCON_PORT_2), password: process.env.RCON_PASSWORD_2,
  };
  if (server === "server3") return {
    host: process.env.RCON_HOST_3, port: Number(process.env.RCON_PORT_3), password: process.env.RCON_PASSWORD_3,
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
    // A socket that times out / closes BEFORE authenticating never ran the command -
    // that's a failure (wrong password, dead host), not an empty response. Resolving
    // it would make sendRconBoth report ok for a server that silently did nothing.
    const settle = () => authenticated
      ? finish(resolve, response)
      : finish(reject, new Error(`no RCON auth from ${host}:${port}`));
    socket.on("timeout", settle);
    socket.on("error",   (err) => finish(reject, err));
    socket.on("close",   settle);
    fallbackTimer = setTimeout(settle, timeoutMs);
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
  if (server === "both") {   // "both" = every active server (2 or 3 of them)
    const results = await Promise.allSettled(ACTIVE_SERVERS.map(s => sendRcon(command, s, T, R)));
    const out = { s1: null, s2: null, s3: null, ok1: false, ok2: false, ok3: false };
    ACTIVE_SERVERS.forEach((s, i) => {
      const n = s.replace("server", "");
      if (results[i].status === "fulfilled") { out[`s${n}`] = results[i].value; out[`ok${n}`] = true; }
      else logger.warn("RCON", `[${s}] "${command}" failed: ${results[i].reason?.message || results[i].reason}`);
    });
    return out;
  }
  try {
    const v = await sendRcon(command, server, T, R);
    const n = String(server).replace("server", "");
    return { s1: null, s2: null, s3: null, ok1: false, ok2: false, ok3: false, [`s${n}`]: v, [`ok${n}`]: true };
  }
  catch (err) { logger.warn("RCON", `[${server}] "${command}" failed: ${err.message}`); return { s1: null, s2: null, s3: null, ok1: false, ok2: false, ok3: false }; }
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
/* ---- OS-level firewall block (ufw) — applied to EVERY ban ----
   Opt-in via UFW_BLOCK=1. On a ban: `sudo ufw insert 1 deny from <ip>` for each confirmed
   IP (top of the list; ufw is first-match-wins). On unban: locate the rule number(s) via
   `sudo ufw status numbered` and `sudo ufw delete <n>` (answering the "Proceed" prompt with
   `y`). IPs are strictly validated and passed as argv (no shell). The bot must run as root,
   OR have passwordless sudo for ufw (sudoers: `<user> ALL=(root) NOPASSWD: /usr/sbin/ufw`). */
const UFW_BLOCK = /^(1|true|yes|on)$/i.test(process.env.UFW_BLOCK || "");
const _IPV4_RE = /^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$/;
function _ufw(args) {
  return new Promise((resolve) => {
    execFile("sudo", ["ufw", ...args], { timeout: 10_000 }, (err, stdout, stderr) => {
      resolve({ ok: !err, out: `${stdout || ""}${stderr || ""}`.trim(), err: err?.message });
    });
  });
}
// Same as _ufw but feeds `input` to stdin — for `ufw delete <n>` which prompts "Proceed
// with operation (y|n)?". We answer "y".
function _ufwInput(args, input) {
  return new Promise((resolve) => {
    let out = "";
    try {
      const p = spawn("sudo", ["ufw", ...args], { timeout: 10_000 });
      p.stdout.on("data", d => { out += d; });
      p.stderr.on("data", d => { out += d; });
      p.on("error", (err) => resolve({ ok: false, out: (out + err.message).trim() }));
      p.on("close", (code) => resolve({ ok: code === 0, out: out.trim() }));
      try { if (input != null) p.stdin.write(input); p.stdin.end(); } catch {}
    } catch (err) { resolve({ ok: false, out: err.message }); }
  });
}
async function firewallBlockIps(ips) {
  if (!UFW_BLOCK) return { blocked: 0, off: true };
  const valid = [...new Set((ips || []).map(String).filter(ip => _IPV4_RE.test(ip)))];
  if (!valid.length) return { blocked: 0 };
  let blocked = 0;
  for (const ip of valid) {
    // Delete any stale rule first, then INSERT at position 1. ufw is first-match-wins and
    // `deny from` appends to the end, so a rule below an `allow <game port>` never fires.
    await _ufw(["delete", "deny", "from", ip]);
    const r = await _ufw(["insert", "1", "deny", "from", ip]);
    if (r.ok || /added|existing|skipping/i.test(r.out)) { blocked++; logger.info("Firewall", `ufw insert 1 deny from ${ip}`); }
    else logger.warn("Firewall", `ufw deny from ${ip} failed: ${r.err || r.out}`);
  }
  if (blocked) { const rl = await _ufw(["reload"]); if (!rl.ok) logger.warn("Firewall", `ufw reload failed: ${rl.err || rl.out}`); }
  return { blocked };
}
async function firewallUnblockIps(ips) {
  if (!UFW_BLOCK) return { unblocked: 0, off: true };
  const valid = [...new Set((ips || []).map(String).filter(ip => _IPV4_RE.test(ip)))];
  if (!valid.length) return { unblocked: 0 };
  let unblocked = 0;
  for (const ip of valid) {
    // Locate every rule number whose source is this IP, delete highest-first (deleting a
    // rule renumbers the ones below it), answering the confirmation prompt with `y`.
    const st = await _ufw(["status", "numbered"]);
    const ipRe = new RegExp(`(^|\\s)${ip.replace(/\./g, "\\.")}(\\s|$)`);
    const nums = String(st.out || "").split("\n")
      .map(l => { const m = l.match(/^\[\s*(\d+)\]/); return (m && ipRe.test(l)) ? Number(m[1]) : null; })
      .filter(n => n != null).sort((a, b) => b - a);
    for (const n of nums) {
      const r = await _ufwInput(["delete", String(n)], "y\n");
      if (r.ok) { unblocked++; logger.info("Firewall", `ufw delete ${n} (deny from ${ip})`); }
      else logger.warn("Firewall", `ufw delete ${n} failed: ${r.out}`);
    }
  }
  if (unblocked) { const rl = await _ufw(["reload"]); if (!rl.ok) logger.warn("Firewall", `ufw reload failed: ${rl.err || rl.out}`); }
  return { unblocked };
}
/* Re-apply firewall blocks for EVERY currently-active ban's confirmed IP(s) at ufw
   position 1 — rebuilds the block list after a restart and heals any stale rules. */
async function firewallResyncAll() {
  if (!UFW_BLOCK) return { off: true };
  const now = Date.now();
  const active = loadBans().filter(b => b.permanent || !b.expires || b.expires > now);   // perm + active temp
  const ips = new Set();
  for (const b of active) {
    try { for (const ip of (ipBans.getConfirmedIPsForPlayer(b.playerId) || [])) ips.add(ip); } catch {}
  }
  if (!ips.size) { logger.info("Firewall", "Resync: no active-ban IPs on record to block"); return { blocked: 0 }; }
  const r = await firewallBlockIps([...ips]);
  logger.info("Firewall", `Resync: re-applied ${r.blocked}/${ips.size} active-ban IP(s) at ufw position 1`);
  return r;
}

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

// Embed field summarising the ufw firewall action (null when the feature is off).
function firewallField(fw) {
  if (!fw || fw.off) return null;
  return { name: "Firewall (ufw)", value: fw.blocked
    ? `Blocked **${fw.blocked}** IP${fw.blocked !== 1 ? "s" : ""} — \`ufw deny from <ip>\``
    : "No confirmed IP on record yet — nothing to block.", inline: false };
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

/* ---------------- VPN / proxy detection ----------------
   IPHub is the free baseline check, run on a given IP exactly ONCE, ever. Only when
   IPHub flags it (block==1: hosting/proxy/non-residential) do we spend an
   IPQualityScore lookup to cross-reference it - IPQS is more accurate but its free
   tier is far more limited than IPHub's, so it's reserved for the flagged subset
   instead of running on every connection. The result is cached per-IP permanently -
   once an IP is checked, it's never re-queried against either API again: a clean
   result means that IP is trusted for good, a confirmed VPN means it's already been
   acted on. The check is keyed on the IP itself, not the account or EOS id - a clean
   IP is clean no matter who connects from it next.
   A confirmed VPN/proxy auto-bans the connecting player (native RCON Ban+Kick, IP+EOS
   flagged same as any other ban). If IPHub flags but IPQS explicitly disputes it
   (checked, came back clean), that's treated as a likely false positive and only
   logged, not banned - the whole point of the cross-check. If IPQS isn't configured,
   an IPHub flag alone is enough to ban.
   Entirely optional - a no-op if IPHUB_API_KEY isn't set. */
const IPHUB_API_KEY  = process.env.IPHUB_API_KEY || "";
const IPQS_API_KEY   = process.env.IPQS_API_KEY  || "";
const IPINFO_TOKEN   = process.env.IPINFO_TOKEN  || "";   // optional — higher ipinfo.io free quota
const _regionName    = (() => { try { return new Intl.DisplayNames(["en"], { type: "region" }); } catch { return null; } })();
const loadVpnChecks  = () => safeRead(FILES.VPN_CHECKS, {});
function saveVpnCheck(ip, data) {
  return update(FILES.VPN_CHECKS, {}, (all) => { all[ip] = { ...data, checkedAt: Date.now() }; return all; });
}
// Exactly one VPN check per IP: cached forever once done, and concurrent checks of the
// same not-yet-cached IP share a single in-flight lookup (no double API calls when two
// players connect from the same new IP at once, or /inspect races the connection feed).
const _vpnInFlight = new Map();   // ip -> Promise
async function checkVpn(ip) {
  if (!ip) return null;                            // geolocation runs even without the VPN keys
  const cached = loadVpnChecks()[ip];
  if (cached?.geo) return cached;                  // fully cached (already has geolocation)
  const inflight = _vpnInFlight.get(ip);
  if (inflight) return inflight;                   // a check for this exact IP is already running - reuse it
  // Entry cached before geolocation existed → backfill geo only (keep the VPN verdict);
  // otherwise run the full check. This heals old "unknown"-location cache entries.
  const p = (cached ? _backfillGeo(ip, cached) : _doVpnCheck(ip)).finally(() => _vpnInFlight.delete(ip));
  _vpnInFlight.set(ip, p);
  return p;
}
async function _backfillGeo(ip, prev) {
  const geo = await geoLookup(ip);
  if (!geo) return prev;                           // geo still unavailable — keep the old entry, retry next time
  const result = { ...prev, geo, isp: geo.isp || prev.isp || null };
  await saveVpnCheck(ip, result);
  return { ...result, checkedAt: Date.now() };
}
// IP geolocation via ipinfo.io — full city-level location. Works keyless (rate-limited);
// set IPINFO_TOKEN for the larger free quota. Doesn't touch the IPHub/IPQS quotas.
async function geoLookup(ip) {
  try {
    const url = `https://ipinfo.io/${encodeURIComponent(ip)}/json${IPINFO_TOKEN ? `?token=${IPINFO_TOKEN}` : ""}`;
    const res = await fetch(url, { headers: { Accept: "application/json" } });
    const d = await res.json();
    if (!d || !d.ip || d.error || d.bogon) return null;   // error / private / reserved IP
    const [lat, lon] = String(d.loc || "").split(",");
    // ipinfo's `org` is "AS#### <ISP name>" — split into ASN + ISP.
    let asn = null, isp = d.org || null;
    const m = String(d.org || "").match(/^(AS\d+)\s+(.*)$/);
    if (m) { asn = m[1]; isp = m[2]; }
    // ipinfo returns a 2-letter country code; expand to a full name via built-in Intl.
    let country = d.country || null;
    if (country && _regionName) { try { country = _regionName.of(country) || d.country; } catch {} }
    return {
      city: d.city || null, region: d.region || null,
      country, countryCode: d.country || null,
      zip: d.postal || null, isp, org: isp, asn,
      timezone: d.timezone || null,
      lat: lat ? Number(lat) : null, lon: lon ? Number(lon) : null,
    };
  } catch (err) {
    const msg = IPINFO_TOKEN ? String(err.message).split(IPINFO_TOKEN).join("***") : String(err.message);
    logger.warn("Geo", `ipinfo.io lookup failed for ${ip}: ${msg}`);
    return null;
  }
}
async function _doVpnCheck(ip) {
  // Geolocation first — free/keyless, for every IP regardless of the VPN keys.
  const gwho = await geoLookup(ip);

  // VPN detection — optional, only when IPHUB_API_KEY is set.
  let iphub = null, iphubFailed = false, flagged = false, confirmed = null, ipqs = null;
  if (IPHUB_API_KEY) {
    try {
      const res = await fetch(`https://v2.api.iphub.info/ip/${encodeURIComponent(ip)}`, { headers: { "X-Key": IPHUB_API_KEY } });
      iphub = await res.json();
    } catch (err) {
      logger.warn("VPN", `IPHub lookup failed for ${ip}: ${err.message}`);
      iphubFailed = true;
    }
    flagged = iphub?.block === 1;
    // IPQS only cross-checks IPHub-flagged IPs — its free tier is ~35/day, so spend it
    // where it counts (disputing a flag), not on geolocation (ipwho.is covers that).
    if (flagged && IPQS_API_KEY) {
      try {
        const res  = await fetch(`https://www.ipqualityscore.com/api/json/ip/${IPQS_API_KEY}/${encodeURIComponent(ip)}?strictness=1`);
        const data = await res.json();
        if (data?.success) {
          ipqs = { vpn: !!data.vpn, proxy: !!data.proxy, tor: !!data.tor, fraudScore: data.fraud_score ?? null };
          confirmed = !!(data.vpn || data.proxy || data.tor);
        }
      } catch (err) {
        logger.warn("VPN", `IPQS lookup failed for ${ip}: ${String(err.message ?? err).split(IPQS_API_KEY).join("***")}`);
      }
    }
  }

  // Prefer the whois geo (city-level); fall back to IPHub's country when whois is down.
  const geo = gwho || (iphub ? {
    city: null, region: null, country: iphub.countryName || null, countryCode: iphub.countryCode || null,
    zip: null, isp: iphub.isp || null, org: null, asn: null, timezone: null, lat: null, lon: null,
  } : null);
  if (!geo) return null;                           // no location at all - don't cache, retry next time
  if (IPHUB_API_KEY && iphubFailed) return null;   // VPN enabled but IPHub down - retry to capture the flag

  const result = { ip, flagged, confirmed, iphubBlock: iphub?.block ?? null, isp: geo.isp || null, ipqs, geo };
  await saveVpnCheck(ip, result);
  return { ...result, checkedAt: Date.now() };
}
// Human-readable full location from a stored geo object (webhook feed / /inspect).
function formatFullLocation(geo) {
  if (!geo) return null;
  const place = [geo.city, geo.region, geo.country].filter(Boolean).join(", ") + (geo.zip ? ` ${geo.zip}` : "");
  const bits  = [place.trim() || geo.country || null, geo.isp || null, geo.timezone ? `TZ ${geo.timezone}` : null].filter(Boolean);
  return bits.length ? bits.join("  ·  ") : null;
}
// Called from ipBans' onConfirm for every freshly-confirmed IP. Since checkVpn()
// caches an IP's result forever, this naturally only ever acts once per IP - a
// player reconnecting from an already-checked IP costs nothing and does nothing.
async function checkVpnAndAlert(name, ip) {
  if (!ip || !IPHUB_API_KEY) return null;
  const alreadyChecked = !!loadVpnChecks()[ip];
  const result = await checkVpn(ip).catch(() => null);
  if (!result) return null;
  // Clean, or an IP we've already acted on — return the verdict for the feed, but don't ban.
  if (!result.flagged || alreadyChecked) return result;

  // Masters and explicitly-unbanned players are never auto-actioned (matches onAutoBan).
  // Only master names bypass ALL enforcement; staff/donators are NOT exempt from this.
  if (isMasterName(name) || isAutobanExempt(name)) {
    logger.info("VPN", `Skipped VPN auto-ban for exempt/master ${name}`);
    return result;
  }

  const disputed = result.confirmed === false;   // IPHub flagged it, IPQS actively said clean
  if (disputed) {
    const embed = brand(new EmbedBuilder().setColor(NV.DEAD_GREY)
      .setTitle("VPN Flag Disputed — No Action")
      .setDescription(`${DIVIDER}\n**${name}** connected from an IP IPHub flagged, but IPQS checked it and disagreed.\n${DIVIDER}`)
      .addFields(
        { name: "ISP",         value: result.isp || "unknown",        inline: true },
        { name: "IPHub block", value: String(result.iphubBlock ?? "?"), inline: true },
        { name: "IPQS", value: `vpn:${result.ipqs.vpn} · proxy:${result.ipqs.proxy} · tor:${result.ipqs.tor} · fraud:${result.ipqs.fraudScore}`, inline: true },
      ).setFooter({ text: "Likely false positive — not banned" }));
    await logAction(embed);
    return result;
  }

  const label = result.confirmed === true ? "Confirmed VPN/proxy (IPHub + IPQS agree)" : "Flagged by IPHub (IPQS not configured to cross-check)";
  let res;
  try { res = await banWithIp(name, "both", { permanent: true, ip }); }
  catch (err) { logger.warn("VPN", `auto-ban failed for ${name}: ${err.message}`); res = { blacklist: { servers: 0 } }; }
  try { await upsertPermBan({ playerId: name, reason: "VPN/proxy detected", moderator: "VPN detection (auto)" }); } catch {}
  writeModLog({ action: "auto-vpnban", playerId: name, reason: `VPN/proxy detected (${label})`, by: "VPN detection (auto)" });
  logger.warn("VPN", `Auto-banned ${name} — ${label}, ip ${ip}`);
  const embed = clinical(new EmbedBuilder().setColor(CLIN.red)
    .setTitle("Auto-Ban — VPN/Proxy Detected")
    .setDescription(`${hero(randomQuote("autoban"))}`)
    .addFields(
      { name: "Courier", value: `\`${name}\``, inline: true },
      { name: "IP",      value: `\`${ip}\``,   inline: true },
      { name: "Status",  value: label,          inline: false },
      { name: "ISP",         value: result.isp || "unknown",        inline: true },
      { name: "IPHub block", value: String(result.iphubBlock ?? "?"), inline: true },
      ...(result.ipqs ? [{ name: "IPQS", value: `vpn:${result.ipqs.vpn} · proxy:${result.ipqs.proxy} · tor:${result.ipqs.tor} · fraud:${result.ipqs.fraudScore}`, inline: true }] : []),
      { name: "Enforced", value: `RCON Ban+Kick on ${res?.blacklist?.servers ?? 0}/${ACTIVE_SERVERS.length} server(s)`, inline: false },
      ...(firewallField(res?.firewall) ? [firewallField(res.firewall)] : []),
    ), "Auto-ban · native RCON ban · all servers");
  await logBan(embed);
  postFeed(embed);
  return result;
}

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

/* ---------------- casino: atomic ledger ops + shared intake ----------------
   readPlayerBalance/writePlayerBalance are a plain read-then-write with no
   locking - fine for rare admin commands (/givecaps, /adjustcaps) but not for
   a self-service, spammable feature. mutateBalance() queues per playerId (same
   pattern as the JSON update() helper) so concurrent gambles on one account
   can't race each other into a double-spend. */
const _ledgerQueues = new Map();   // lowercased playerId -> tail promise
function mutateBalance(playerId, mutator) {
  const key  = String(playerId).toLowerCase();
  const prev = _ledgerQueues.get(key) ?? Promise.resolve();
  const next = prev.then(async () => {
    const before = readPlayerBalance(playerId) ?? 0;
    const after  = mutator(before);
    if (after === null) return { ok: false, before, after: before };   // mutator veto (e.g. insufficient funds)
    const ok = writePlayerBalance(playerId, after);
    return { ok, before, after: ok ? after : before };
  }).catch(err => {
    logger.error("Casino", `mutateBalance failed for ${playerId}: ${err.message}`);
    return { ok: false, before: 0, after: 0 };
  });
  _ledgerQueues.set(key, next.catch(() => {}));
  return next;
}
function debitCaps(playerId, amount)  { return mutateBalance(playerId, (bal) => bal >= amount ? bal - amount : null); }
function creditCaps(playerId, amount) { return mutateBalance(playerId, (bal) => bal + amount); }

/* The jackpot pot: every losing gamble across the casino feeds it instead of the
   caps just vanishing, and /jackpot lets a player with a big enough bank risk their
   entire balance to take the whole thing. Uses the same serialized update() queue
   as everything else, so concurrent games adding to the pot can't race each other. */
function currentPot() { return safeRead(FILES.CASINO_POT, { amount: 0 }).amount || 0; }
function addToPot(amount) {
  if (!(amount > 0)) return Promise.resolve();
  return update(FILES.CASINO_POT, { amount: 0 }, (p) => ({ amount: (p.amount || 0) + Math.floor(amount) }));
}
// Empties the pot and returns however much was in it (0 if nothing).
async function drainPot() {
  let drained = 0;
  await update(FILES.CASINO_POT, { amount: 0 }, (p) => { drained = p.amount || 0; return { amount: 0 }; });
  return drained;
}

/* Shared 5-per-3-hours cap across EVERY casino game (including /jackpot), keyed to
   the linked Pavlov identity rather than the Discord id so it can't be dodged by
   switching accounts. Persisted to disk so a bot restart doesn't hand out a fresh
   allowance. Each check both reads AND (if allowed) consumes one attempt. */
const GAMBLE_QUOTA_MAX = 5;
const GAMBLE_QUOTA_WINDOW_MS = 3 * 60 * 60 * 1000;
function checkGambleQuota(playerId) {
  const key = String(playerId).toLowerCase();
  const all = safeRead(FILES.CASINO_QUOTA, {});
  const cutoff = Date.now() - GAMBLE_QUOTA_WINDOW_MS;
  const recent = (all[key] || []).filter(ts => ts > cutoff);
  if (recent.length >= GAMBLE_QUOTA_MAX) return { ok: false, resetAt: recent[0] + GAMBLE_QUOTA_WINDOW_MS };
  recent.push(Date.now());
  safeWrite(FILES.CASINO_QUOTA, { ...all, [key]: recent });
  return { ok: true, remaining: GAMBLE_QUOTA_MAX - recent.length };
}
function gambleQuotaLimitEmbed(resetAt) {
  return warningEmbed("Gambling Limit Reached",
    `You've hit the limit of **${GAMBLE_QUOTA_MAX} gambles per 3 hours**. Try again <t:${Math.floor(resetAt / 1000)}:R>.`);
}

/* Shared preflight for every gambling command: casino enabled, caller linked to
   a Pavlov identity, not rate-limited, under the 3-hour gamble quota, bet within
   the configured bounds and no larger than the caller's balance. Replies and
   returns null on any failure; otherwise returns { cfg, playerId, bet, balance }
   with nothing yet debited (and one gamble already counted against the quota). */
async function casinoIntake(interaction) {
  const cfg = loadCasinoConfig();
  if (!cfg.enabled) {
    await interaction.reply({ embeds: [warningEmbed("The House Is Closed", "Gambling is currently disabled.")], flags: MessageFlags.Ephemeral });
    return null;
  }
  const playerId = loadDiscordLinks()[interaction.user.id]?.name;
  if (!playerId) {
    await interaction.reply({ embeds: [warningEmbed("Not Linked", "Link your Discord to your Pavlov username first — use `/link add`.")], flags: MessageFlags.Ephemeral });
    return null;
  }
  if (!checkRateLimit(interaction.user.id, "casino", cfg.cooldownMs)) {
    await interaction.reply({ embeds: [rateLimitEmbed()], flags: MessageFlags.Ephemeral });
    return null;
  }
  const quota = checkGambleQuota(playerId);
  if (!quota.ok) {
    await interaction.reply({ embeds: [gambleQuotaLimitEmbed(quota.resetAt)], flags: MessageFlags.Ephemeral });
    return null;
  }
  const bet = interaction.options.getInteger("bet");
  if (bet < cfg.minBet || bet > cfg.maxBet) {
    await interaction.reply({ embeds: [errorEmbed("Bad Bet", `Bet must be between **${cfg.minBet.toLocaleString()}** and **${cfg.maxBet.toLocaleString()}** caps.`)], flags: MessageFlags.Ephemeral });
    return null;
  }
  const balance = readPlayerBalance(playerId) ?? 0;
  if (bet > balance) {
    await interaction.reply({ embeds: [errorEmbed("Insufficient Caps", `You only have **${balance.toLocaleString()}** caps.`)], flags: MessageFlags.Ephemeral });
    return null;
  }
  return { cfg, playerId, bet, balance };
}

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

/* Reset one faction's whitelist: empties the spawn (membership) roster and every
   rank file, and drops its faction_ranks.json rank overrides. Existing content is
   snapshotted first (same as loadFactionBackup) so a bad wipe is recoverable by hand.
   Returns { ok, count } where count is how many members were on the roster. */
async function wipeFaction(faction) {
  const spawn = SPAWN_FILE_MAP[faction];
  if (!spawn) return { ok: false, error: "unknown faction" };
  const before = readFactionFile(spawn);
  if (before === null) return { ok: false, error: "file unreadable" };
  const cfg   = getFactionRankConfig(faction);
  const files = new Set([spawn]);
  if (cfg) for (const f of Object.values(cfg.rankFiles)) if (f) files.add(f);
  for (const f of files) {
    try { backupFactionFile(f, fs.readFileSync(path.join(FACTION_ROLES_PATH, f), "utf8")); } catch {}
    writeFactionFile(f, [], { allowBulk: true });
  }
  await update(FILES.FACTION_RANKS, {}, (ranks) => { delete ranks[faction]; return ranks; });
  return { ok: true, count: before.length };
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

// ---- leaderboard ----
function buildLeaderboardData() {
  const base = getModsavePath();
  if (!base) return null;
  const entries = [];
  try {
    for (const file of fs.readdirSync(base).filter(f => f.endsWith(".txt") && f.toLowerCase() !== "banlist.txt")) {
      const id  = path.basename(file, ".txt");
      try {
        const bal = parseInt(fs.readFileSync(path.join(base, file), "utf8").trim(), 10);
        if (!isNaN(bal)) entries.push({ playerId: id, balance: bal });
      } catch {}
    }
  } catch (err) { logger.error("Leaderboard", err.message); return null; }
  const sorted = entries.sort((a, b) => b.balance - a.balance);
  const top = sorted.slice(0, LEADERBOARD_TOP_N);
  // Whole-economy totals (every ledger, not just the shown top N) for the header.
  top.totalCaps    = sorted.reduce((s, e) => s + e.balance, 0);
  top.totalPlayers = sorted.length;
  return top;
}

function rankLabel(i) {
  // Top three get the ◆ badge; everyone else a plain aligned number.
  return `\`${i < 3 ? "◆" : "#"}${String(i + 1).padStart(2)}\``;
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
  return brand(embed.setDescription(
    `${hero("War never changes. But caps? Caps fluctuate.")}\n${GLYPH.caps} **Combined: ${(entries.totalCaps ?? 0).toLocaleString()} caps** across **${entries.totalPlayers ?? entries.length}** ledgers\n${DIVIDER}\n${body}`),
    { thumb: true, footer: { text: `Updated every 30s` } });
}

/* Message IDs for the "edit this one message in place" auto-posts (both
   leaderboards, the player list, the dashboard) are persisted here - NOT kept
   in memory only. A bot restart used to forget which message it was editing,
   so the next tick couldn't find it and posted a fresh one instead, leaving
   the old one orphaned in the channel. Restart a few times and they stack up. */
const loadAutopostState = () => safeRead(FILES.AUTOPOST_STATE, {});
function getAutopostMsgId(key) { return loadAutopostState()[key] || null; }
function setAutopostMsgId(key, id) { safeWrite(FILES.AUTOPOST_STATE, { ...loadAutopostState(), [key]: id }); }

async function postLeaderboard() {
  const channelId = process.env.LEADERBOARD_CHANNEL;
  if (!channelId) return;
  let channel;
  try { channel = await client.channels.fetch(channelId); } catch { return; }
  const embed = buildLeaderboardEmbed();
  const existingId = getAutopostMsgId("leaderboard");
  if (existingId) {
    try { const m = await channel.messages.fetch(existingId); await m.edit({ embeds: [embed] }); return; }
    catch { setAutopostMsgId("leaderboard", null); }
  }
  try { const m = await channel.send({ embeds: [embed] }); setAutopostMsgId("leaderboard", m.id); } catch {}
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
  // Grand total across EVERY tracked player (not just the top N shown).
  const all = loadPlaytime();
  const totalMin = Object.values(all).reduce((s, m) => s + (Number(m) || 0), 0);
  const players  = Object.keys(all).filter(k => (Number(all[k]) || 0) > 0).length;
  const body = entries.map((e, i) => {
    const meter = i < 5 ? `  \`${bar(e.minutes, top, 8)}\`` : "";
    return `${rankLabel(i)}  **${e.playerId}**  ·  ${formatPlaytime(e.minutes)}${meter}`;
  }).join("\n");
  return brand(embed.setDescription(
    `${hero("Time served in the Mojave.")}\n${GLYPH.caps} **Combined: ${formatPlaytime(totalMin)}** across **${players}** couriers\n${DIVIDER}\n${body}`),
    { thumb: true, footer: { text: `Updated every 30s` } });
}

async function postPlaytimeLeaderboard() {
  if (!PLAYTIME_LB_CHANNEL) return;
  let channel;
  try { channel = await client.channels.fetch(PLAYTIME_LB_CHANNEL); } catch { return; }
  const embed = buildPlaytimeLeaderboardEmbed();
  const existingId = getAutopostMsgId("playtimeLb");
  if (existingId) {
    try { const m = await channel.messages.fetch(existingId); await m.edit({ embeds: [embed] }); return; }
    catch { setAutopostMsgId("playtimeLb", null); }
  }
  try { const m = await channel.send({ embeds: [embed] }); setAutopostMsgId("playtimeLb", m.id); } catch {}
}

/* Live player list — edits its own message in a channel every 30s. */
function buildPlayerListEmbed() {
  // Read the faction spawn files once so we can tag each connected player with
  // their faction. Players not in any faction are shown exactly as before.
  const membership = buildFactionMembershipIndex();
  const factionTag = (name) => {
    const facs = membership?.get(name.toLowerCase());
    return facs && facs.length ? `  —  ${facs.join(" / ")}` : "";
  };
  const fmt = (arr) => {
    if (!arr.length) return "*Empty*";
    let out = arr.map(n => `• ${n}${factionTag(n)}`).join("\n");
    if (out.length > 1024) out = out.slice(0, 1000).replace(/\n[^\n]*$/, "") + "\n…";
    return out;
  };
  const total = allCachedPlayers().length;
  const embed = new EmbedBuilder().setColor(NV.IRRAD_GREEN).setTitle("Live Player List")
    .setDescription(hero(`**${total}** courier${total !== 1 ? "s" : ""} roaming the Mojave right now.`));
  for (const srv of ACTIVE_SERVERS) {
    const list = [...playerCache[srv]].sort((a, b) => a.localeCompare(b));
    embed.addFields({ name: `${serverLabel(srv)} (${list.length})`, value: fmt(list), inline: true });
  }
  return brand(embed.setFooter({ text: "Updates every 30s" }).setTimestamp());
}
async function postPlayerList() {
  if (!PLAYERLIST_CHANNEL) return;
  let channel;
  try { channel = await client.channels.fetch(PLAYERLIST_CHANNEL); } catch { return; }
  try { for (const srv of ACTIVE_SERVERS) await refreshPlayerCache(srv); } catch {}
  const embed = buildPlayerListEmbed();
  const existingId = getAutopostMsgId("playerList");
  if (existingId) {
    try { const m = await channel.messages.fetch(existingId); await m.edit({ embeds: [embed] }); return; }
    catch { setAutopostMsgId("playerList", null); }
  }
  try { const m = await channel.send({ embeds: [embed] }); setAutopostMsgId("playerList", m.id); } catch {}
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

// Clear stray messages from the leaderboard/player-list channels (anything left over
// from before the bot tracked message ids, manual posts, etc.), then post/edit the
// tracked message as usual. Deliberately does NOT reset the tracked ids first: if
// purgeChannel couldn't actually delete the tracked message (missing permission, a
// message >14 days old that the individual-delete fallback also failed on, a rate
// limit), postLeaderboard() et al. will still find and edit it - so a purge that only
// partially succeeds can't result in a duplicate the way an unconditional reset would.
async function refreshLeaderboardChannels() {
  const channels = [...new Set([process.env.LEADERBOARD_CHANNEL, PLAYTIME_LB_CHANNEL, PLAYERLIST_CHANNEL, DASHBOARD_CHANNEL].filter(Boolean))];
  for (const ch of channels) {
    try { const n = await purgeChannel(ch); if (n) logger.info("Purge", `Cleared ${n} old message(s) from channel ${ch}`); }
    catch (e) { logger.warn("Purge", `Could not purge ${ch}: ${e.message}`); }
  }
  postLeaderboard(); postPlaytimeLeaderboard(); postPlayerList(); if (DASHBOARD_CHANNEL) postDashboard();
}

// Live server-status dashboard: one self-refreshing embed with per-server
// health, player-count progress bars, map/mode, and gateway ping.
async function serverSnapshot(srv) {
  try {
    const [list, info] = await Promise.all([
      sendRcon("RefreshList", srv, 2500, 0),
      sendRcon("ServerInfo",  srv, 2500, 0),
    ]);
    const ld = parseRcon(list), id = parseRcon(info);
    const players = (ld?.PlayerList ?? []).length;
    const max = Number(id?.ServerInfo?.MaxPlayers ?? id?.MaxPlayers ?? 24) || 24;
    return {
      up: !!ld?.Successful,
      players, max,
      map:  id?.ServerInfo?.MapLabel ?? id?.MapLabel ?? id?.ServerInfo?.MapName ?? "unknown",
      mode: id?.ServerInfo?.GameMode ?? id?.GameMode ?? "unknown",
      name: id?.ServerInfo?.ServerName ?? id?.ServerName ?? serverLabel(srv),
    };
  } catch { return { up: false, players: 0, max: 24, map: "-", mode: "-", name: serverLabel(srv) }; }
}
/* Two compact monospace lines per server. Plain code block (no ANSI color) so it
   renders identically on desktop and mobile, and lines stay under ~28 chars so
   phones don't wrap them. */
function hudRow(s) {
  const dot   = s.up ? GLYPH.up : GLYPH.down;
  const name  = cell(s.name, 22);
  if (!s.up) return `${dot} ${name}\n  offline`;
  const count = cell(`${s.players}/${s.max}`, 5);
  return `${dot} ${name}\n  ${count} ${bar(s.players, s.max, 10)}  ${cell(s.mode, 8)}`;
}
function buildDashboardEmbed(snaps) {
  const anyUp  = snaps.some(s => s.up);
  const totalP = snaps.reduce((a, s) => a + (s.up ? s.players : 0), 0);
  const gw     = Math.max(0, client.ws.ping);
  const lines = [
    "LIVE NETWORK STATUS",
    `${totalP} online · gw ${gw}ms · ${Math.round(DASHBOARD_INTERVAL_MS / 1000)}s`,
    "──────────────────────────",
    ...snaps.map(hudRow),
  ];
  const embed = new EmbedBuilder()
    .setColor(anyUp ? NV.IRRAD_GREEN : NV.RUST_RED)
    .setTitle("Live Server Status")
    .setDescription("```\n" + lines.join("\n") + "\n```");
  return brand(embed);
}
async function dashboardSnapshots() {
  return Promise.all(ACTIVE_SERVERS.map(serverSnapshot));
}
async function postDashboard() {
  if (!DASHBOARD_CHANNEL) return;
  let channel; try { channel = await client.channels.fetch(DASHBOARD_CHANNEL); } catch { return; }
  const embed = buildDashboardEmbed(await dashboardSnapshots());
  const existingId = getAutopostMsgId("dashboard");
  if (existingId) {
    try { const m = await channel.messages.fetch(existingId); await m.edit({ embeds: [embed] }); return; }
    catch { setAutopostMsgId("dashboard", null); }
  }
  try { const m = await channel.send({ embeds: [embed] }); setAutopostMsgId("dashboard", m.id); } catch {}
}

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
