/* ---------------- database: SQLite (bot.db) storage layer ----------------
   Extracted from index.js. Owns the single better-sqlite3 handle, the one-time
   JSON->SQLite migration, the in-memory cache + per-file write serialization,
   and the periodic SQLite->JSON export. Also carries the FILES/DEFAULTS dataset
   registry and the filesystem-ownership helpers, since they're all part of "how
   the bot persists state".

   Injected deps:
     logger   - the shared structured logger
     baseDir  - the bot root (__dirname of index.js), used for the bot.db path.
                The FILES paths stay cwd-relative (the bot starts from its root),
                so only the DB file needs an absolute anchor.

   Usage:
     const { FILES, DEFAULTS, safeRead, safeWrite, update, ensureFile, ... } =
       require("./database")({ logger, baseDir: __dirname });
*/
const fs   = require("fs");
const path = require("path");
const Database = require("better-sqlite3");

module.exports = function createDatabase({ logger, baseDir }) {
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
    // Server lock (RCON SetPin/RemovePin): the PIN plus who set it and when.
    SERVER_LOCK:    "./server_lock.json",
    VPN_CHECKS: "./vpn_checks.json",
    DONATOR_SUSPEND: "./donator_suspend.json",
    SERVER_STATS: "./server_stats.json",
    WARRANTS: "./warrants.json",
    VERIFICATIONS: "./verifications.json",
    VERIFY_STATE: "./verify_state.json",
    ARRESTS: "./arrests.json",
    SENTENCES: "./sentences.json",
    RANK_SUSPENSIONS: "./rank_suspensions.json",
    POLICE_CONFIG: "./police_config.json",
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
    [FILES.SERVER_STATS]:   "{}",
    [FILES.WARRANTS]:       "{}",
    [FILES.VERIFICATIONS]:  "{}",
    [FILES.VERIFY_STATE]:   "{}",
    [FILES.ARRESTS]:        "{}",
    [FILES.SENTENCES]:      "{}",
    [FILES.RANK_SUSPENSIONS]: "{}",
    [FILES.POLICE_CONFIG]:  JSON.stringify({ bailRate: 1 }, null, 2),
    [FILES.ROLES]:          JSON.stringify({ modRoleId: "", adminRoleId: "", factionLeaderRoleId: "", policeRoleId: "", gambinoRoleId: "", colomboRoleId: "", nypdRoleId: "" }, null, 2),
    [FILES.CASINO_CONFIG]:  JSON.stringify(CASINO_CONFIG_DEFAULTS, null, 2),
  };

  // ---- storage: SQLite (bot.db) with a one-time import from the JSON files ----
  // Every dataset that used to be a JSON file is now a row in bot.db. The .json files are left
  // untouched as a backup snapshot from migration time. safeRead/safeWrite/update and all the
  // typed loaders are unchanged — only the low-level _rawRead/_rawWrite below now hit SQLite.
  const db = new Database(path.join(baseDir, "bot.db"));
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

  return {
    db, FILES, DEFAULTS, CASINO_CONFIG_DEFAULTS,
    safeRead, safeWrite, update,
    exportDbToJson, DB_EXPORT_INTERVAL_MS,
    ensureFile, matchTreeOwner, intendedOwner,
  };
};
