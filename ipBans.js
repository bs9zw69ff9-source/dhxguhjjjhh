/* ================================================================
 * ipBans.js - IP / EOS ban-evasion tracker for Pavlov
 * ================================================================
 * Tails the Pavlov server log, learns each account's UniqueId (EOS), name and
 * IPs, and catches ban evaders. The SPEC this implements:
 *
 *   1. TRACK from the log: for every account (UniqueId), its name and its
 *      CONFIRMED IPs - the IP and id that appear on the SAME disconnect line
 *      (UChannel::Close etc.). These pairings are certain. Join-time IPs (an
 *      "accept" line correlated to a later "login" line) are a GUESS: kept for
 *      display only, NEVER used to flag or auto-ban - a wrong guess would ban a
 *      stranger.
 *
 *   2. FLAG on a ban (blacklistPlayer, called by the bot's /permban, /tempban
 *      and the auto-ban): the account's CONFIRMED IP(s) + its EOS id. If it has
 *      no confirmed IP yet (still online), nothing is flagged now - pendingFlag
 *      flags the real IP the instant a disconnect confirms it.
 *
 *   3. AUTO-BAN trigger: a live join whose EXACT IP or EOS id is flagged. No /24
 *      subnets. The bot (onAutoBan) is handed the UniqueId so it can Kick by id.
 *
 *   4. REVERSIBLE: unblacklistPlayer clears the account's IP + EOS + name flags.
 *      A temp ban's EOS flag is cleared on expiry so a served ban never sticks.
 *
 * Exemptions (masters/staff/donators are never auto-banned) live in the bot's
 * onAutoBan handler; masters are also seeded here as "untracked".
 *
 * LOG PATH: set PAVLOV_LOGS in .env, else it auto-detects Pavlov.log under the
 * usual roots. Run `node ipBans.js` to self-test the log format.
 * ================================================================ */

"use strict";
const fs   = require("fs");
const path = require("path");
const Database = require("better-sqlite3");

/* ---------------- CONFIG ---------------- */
const REGISTRY_PATH  = path.join(__dirname, "ip_registry.json");
const BLACKLIST_PATH = path.join(__dirname, "ip_blacklist.json");      // flagged IPs
const FNAMES_PATH    = path.join(__dirname, "ip_flagged_names.json");  // flagged usernames
const FIDS_PATH      = path.join(__dirname, "ip_flagged_ids.json");    // flagged EOS/unique ids
const UNTRACKED_PATH = path.join(__dirname, "ip_untracked.json");   // usernames to never track
const UNTRACKED_IDS_PATH = path.join(__dirname, "ip_untracked_ids.json"); // ids resolved to those names (persisted - registry entries are purged)
const CUTOFF_PATH    = path.join(__dirname, "ip_cutoff.json");      // ignore log lines older than this (set by "wipe all IP data")
const PENDING_FLAG_PATH = path.join(__dirname, "ip_pending_flag.json"); // ids banned while online, awaiting their confirmed IP
const KD_PATH        = path.join(__dirname, "kd.json");            // per-player kills/deaths
const KILLLOG_PATH   = path.join(__dirname, "kill_log.json");      // per-killer victim tallies (for faction kill counts)
const LOG_TAIL       = path.join("Pavlov", "Saved", "Logs", "Pavlov.log");
const DEFAULT_LOG    = path.join("/home/steam/pavlovserver", LOG_TAIL);

const POLL_MS             = 1500;   // tail the log ~every 1.5s so flagged joins are banned on sight
const CORRELATE_WINDOW_MS = 10000;            // join IP must precede the login line by < this
const JOIN_DEBOUNCE_MS    = 20000;            // one feed message per join (collapse INVALID+auth pair)
const CONFIRM_DEBOUNCE_MS = 20000;            // one confirm feed message per id (collapse a disconnect's close lines)
const PENDING_FLAG_MS     = 5 * 60 * 1000;    // after a ban, flag the IP confirmed by the kick within this window
const AUTO_DEBOUNCE_MS    = 5 * 60 * 1000;    // don't re-auto-ban the same id within 5 min
const MAX_BACKFILL_BYTES  = 50 * 1024 * 1024; // first pass: only scan the tail of huge logs
const SAVE_THROTTLE_MS    = 3000;             // coalesce registry writes (disconnect lines are frequent)

/* ---------------- REGEXES (validated against real Pavlov logs) ---------------- */
const TS_RE     = /^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]/;
const ACCEPT_RE = /(?:NotifyAcceptingConnection accepted from:|NotifyAcceptedConnection:.*?RemoteAddr:|AddClientConnection:.*?RemoteAddr:)\s*((?:\d{1,3}\.){3}\d{1,3})/;
// Order-independent field extractors. Any line carrying BOTH a RemoteAddr IP and a
// UniqueId is a confirmed IP<->id pairing (no matter the field order); any Login/Join
const IP_FIELD_RE  = /RemoteAddr:\s*((?:\d{1,3}\.){3}\d{1,3})/i;                    // the IP, anywhere on the line
const UID_FIELD_RE = /UniqueId:\s*([^\s,]+)/i;                                      // disconnect-style id field
// ?Name=Foo option. The value ends at the next URL option (?/&), at a following
// "field: value" token (real Pavlov lines are `?Name=Bob userId: EOS:...` with no ?
const NAME_OPT_RE  = /[?&]Name=([^?&]+?)(?=[?&]|\s+(?:userId|userid|PlayerId|UniqueId|platform|pid)\s*[:=]|\s*$)/i;
const UID_LOGIN_RE = /(?:userId|PlayerId|UniqueId|userid)\s*[:=]\s*([^\s,?&]+)/i;   // login-style id field
// A confirmed pairing = a line with both an IP and a disconnect-style id.
function extractClose(line) {
  const ipm = line.match(IP_FIELD_RE), idm = line.match(UID_FIELD_RE);
  return (ipm && idm) ? { ip: ipm[1], id: idm[1] } : null;
}
// A login/join line = name (optional) + id (required to link).
function extractLogin(line) {
  if (!/Login request:|Join request:/i.test(line)) return null;
  const idm = line.match(UID_LOGIN_RE);
  if (!idm) return null;
  const nm = line.match(NAME_OPT_RE);
  return { name: nm ? nm[1].trim() : null, id: idm[1] };
}
const BAN_RE    = /Rcon:\s*BanPlayer\s+(\S+)/i;
const UNBAN_RE  = /Rcon:\s*UnbanPlayer\s+(\S+)/i;
// Kill/death stats (requires bVerboseLogging=true in Game.ini). Pavlov logs a
// KillData record - either as a single JSON line or split across lines. Match each
// field anywhere, case-insensitively.
const KILLER_RE   = /"Killer":\s*"([^"]*)"/i;
const KILLED_RE   = /"Killed":\s*"([^"]*)"/i;
const KILLEDBY_RE = /"KilledBy":\s*"([^"]*)"/i;

/* ---------------- small helpers ---------------- */
const norm     = s => String(s ?? "").trim().toLowerCase();
// Strict IPv4 shape check - octets bounded 0-255 so "999.1.1.1" isn't treated as an IP.
const IPV4_RE  = /^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$/;
// "NULL:<hex>" -> "<hex>", and strip any stray non-alphanumerics (e.g. a trailing
// "'" captured from quoted NetworkFailure/error log lines).
const cleanId  = raw => { const s = raw == null ? "" : String(raw); const p = s.includes(":") ? s.split(":").pop() : s; return p.replace(/[^a-z0-9]/gi, ""); };
const skipId   = id => !id || /INVALID/i.test(id) || /localhost-/i.test(id);       // pre-auth / server self-conn
const labelFor = f => { const m = String(f).match(/([^/\\]+)[/\\]Pavlov[/\\]/i); return m ? m[1] : path.basename(path.dirname(f)); };
const mtimeOf  = f => { try { return fs.statSync(f).mtimeMs; } catch { return 0; } };
const exists   = f => { try { return fs.existsSync(f); } catch { return false; } };
/* ---------------- storage: SQLite (shared bot.db); JSON files kept as backup ----------------
   All ipBans state now lives in the shared SQLite DB (same bot.db index.js uses). Each old
   per-file JSON is imported ONCE on first read and its .json file is left in place as a
   backup snapshot. Keyed by basename so the key is stable regardless of the absolute path. */
const _db = new Database(path.join(__dirname, "bot.db"));
_db.pragma("journal_mode = WAL");
_db.pragma("busy_timeout = 5000");
_db.exec("CREATE TABLE IF NOT EXISTS kv (file TEXT PRIMARY KEY, data TEXT NOT NULL)");
const _kvGet = _db.prepare("SELECT data FROM kv WHERE file = ?");
const _kvSet = _db.prepare("INSERT INTO kv(file,data) VALUES(?,?) ON CONFLICT(file) DO UPDATE SET data=excluded.data");
const _kvKey = p => path.basename(p);
function loadJSON(p, fallback) {
  const key = _kvKey(p);
  const row = _kvGet.get(key);
  if (row !== undefined) { try { return JSON.parse(row.data); } catch { return fallback; } }
  // Not in the DB yet - import the JSON backup file if present, else seed the fallback.
  try {
    const raw = fs.readFileSync(p, "utf8");
    const val = JSON.parse(raw);
    _kvSet.run(key, raw);                       // one-time data transfer
    return val;
  } catch (err) {
    if (err.code !== "ENOENT") {                // corrupt file - preserve the evidence, start fresh
      try { const bak = `${p}.corrupt-${Date.now()}`; fs.renameSync(p, bak); console.error(`[ipBans] ${path.basename(p)} was corrupt - moved to ${path.basename(bak)}, starting fresh`); } catch {}
    }
    _kvSet.run(key, JSON.stringify(fallback === undefined ? {} : fallback));
    return fallback;
  }
}
function saveJSON(p, data) {
  try { _kvSet.run(_kvKey(p), JSON.stringify(data)); }
  catch (e) { console.error(`[ipBans] db save ${_kvKey(p)}:`, e.message); }
}

/* ---------------- STATE ---------------- */
// registry: { [hexId]: { name, ips: string[], firstSeen, lastSeen } }
const registry  = loadJSON(REGISTRY_PATH, {});
const flagged      = new Set(loadJSON(BLACKLIST_PATH, []));   // flagged IPs (EXACT match only - no /24 subnets)
const ipFlagged = ip => flagged.has(ip);
const flaggedNames = new Set((loadJSON(FNAMES_PATH, []) || []).map(s => String(s).trim().toLowerCase())); // flagged usernames
const MANUAL_IPS_PATH = path.join(__dirname, "ip_manual.json");
const manualIps = new Set(loadJSON(MANUAL_IPS_PATH, []) || []);   // admin-flagged IPs - never cleared by a player unban
function saveManualIps() { saveJSON(MANUAL_IPS_PATH, [...manualIps]); }
const flaggedIds   = new Set((loadJSON(FIDS_PATH, []) || []).map(cleanId).filter(Boolean));               // flagged EOS/unique ids
const untrackedNames = new Set((loadJSON(UNTRACKED_PATH, []) || []).map(s => String(s).trim().toLowerCase())); // usernames excluded from tracking
const untrackedIds   = new Map(Object.entries(loadJSON(UNTRACKED_IDS_PATH, {}) || {}));   // id -> nameKey it was ignored under
const masterNames    = new Set();   // never tracked, but still fire onConnect (so the bot can hand them a menu)

let onAutoBan = async () => {};
let onConnect = async () => {};   // best-effort join (tentative IP)
let onConfirm = async () => {};   // confirmed IPid pairing (same-line disconnect) - accurate IP
let onKill    = async () => {};   // a live PvP kill (killer distinct from victim) - not suicides/environmental deaths
let live      = false;            // false during the startup backfill (suppress feed + auto-ban)
let watchList = [];               // resolved log files (active + rotated backups)

const offsets     = {};           // per-file byte offset
const leftover    = {};           // per-file buffered partial last line
const pendingIP   = {};           // per-file: IP from the latest accept line, awaiting its login
const pendingTs   = {};           // per-file: log-timestamp of that accept line
const pendingSet  = {};           // per-file: distinct IPs accepted since the last login (size 1 = unambiguous)
const recentJoin  = new Map();    // name  -> ts  (join feed dedupe)
const recentConfirm = new Map();  // id    -> ts  (confirm feed dedupe - collapse a disconnect's multiple close lines)
const recentAuto  = new Map();    // id    -> ts  (auto-ban dedupe)
/* id -> ts. A player banned while still ONLINE has no confirmed IP yet, so there is
   nothing to flag at ban time; this remembers them so the IP their disconnect confirms
   gets flagged (within PENDING_FLAG_MS). PERSISTED: this used to be memory-only, so a
   bot restart inside that window silently dropped the pending flag and the banned
   player's IP was never flagged - a ban-evasion hole (ban someone online -> restart ->
   they disconnect -> alt from the same IP walks in unflagged). */
const pendingFlag = new Map(Object.entries(loadJSON(PENDING_FLAG_PATH, {}) || {})
  .map(([id, ts]) => [cleanId(id), Number(ts) || 0])
  .filter(([id, ts]) => id && ts && Date.now() - ts <= PENDING_FLAG_MS));   // drop entries already stale at load
function savePendingFlag() { saveJSON(PENDING_FLAG_PATH, Object.fromEntries(pendingFlag)); }
const pendingKill = {};           // per-file: { killer, killed } accumulated across a KillData block's lines
let lastTs = 0, saveTimer = null, dirty = false;
let cutoffTs = Number(loadJSON(CUTOFF_PATH, 0)) || 0;   // log lines at/older than this are ignored (post-wipe)
function saveCutoff() { saveJSON(CUTOFF_PATH, cutoffTs); }

/* ---------------- kill/death stats ---------------- */
const kd = loadJSON(KD_PATH, {});   // { [nameLower]: { name, kills, deaths } }
let kdDirty = false, kdTimer = null;
function flushKD() { kdDirty = false; saveJSON(KD_PATH, kd); }
function scheduleKD() { kdDirty = true; if (kdTimer) return; kdTimer = setTimeout(() => { kdTimer = null; if (kdDirty) flushKD(); }, SAVE_THROTTLE_MS); }
function bumpKD(name, field) {
  const k = norm(name); if (!k) return;
  const e = kd[k] || { name, kills: 0, deaths: 0 };
  e.name = name; e[field] = (e[field] || 0) + 1; kd[k] = e; scheduleKD();
}
// Per-killer victim tallies: { [killerLower]: { [victimLower]: { name, count } } }.
// Lets /stats show how many times a player has killed each individual - which the
// bot then rolls up per faction by cross-referencing the faction spawn files.
const killLog = loadJSON(KILLLOG_PATH, {});
let killLogDirty = false, killLogTimer = null;
function flushKillLog() { killLogDirty = false; saveJSON(KILLLOG_PATH, killLog); }
function scheduleKillLog() { killLogDirty = true; if (killLogTimer) return; killLogTimer = setTimeout(() => { killLogTimer = null; if (killLogDirty) flushKillLog(); }, SAVE_THROTTLE_MS); }
function bumpKill(killer, victim) {
  const k = norm(killer), v = norm(victim);
  if (!k || !v) return;
  const bucket = killLog[k] || (killLog[k] = {});
  const e = bucket[v] || { name: victim, count: 0 };
  e.name = victim; e.count += 1; bucket[v] = e;   // keep the freshest spelling of the victim's name
  scheduleKillLog();
}
// Every distinct player `killer` has killed, most-killed first: [{ name, count }].
function getKills(killer) {
  const bucket = killLog[norm(killer)];
  if (!bucket) return [];
  return Object.values(bucket).map(e => ({ name: e.name, count: e.count })).sort((a, b) => b.count - a.count);
}

// Record one KillData block. Killed always gets a death; killer gets a kill unless
// it's a suicide/self-death (killer === killed) or there's no real killer.
function recordKill(pk) {
  if (!pk || !pk.killed) return;
  bumpKD(pk.killed, "deaths");
  if (pk.killer && norm(pk.killer) !== norm(pk.killed)) {
    bumpKD(pk.killer, "kills");
    bumpKill(pk.killer, pk.killed);
  }
}
function getKD(name) { const e = kd[norm(name)]; return e ? { ...e } : { name: String(name), kills: 0, deaths: 0 }; }
function topKD(limit = 30, minKills = 0) {
  return Object.values(kd)
    .filter(e => (e.kills + e.deaths) > 0 && e.kills >= minKills)
    .map(e => ({ ...e, ratio: e.deaths ? e.kills / e.deaths : e.kills }))
    .sort((a, b) => b.ratio - a.ratio || b.kills - a.kills)
    .slice(0, limit);
}

/* ---------------- persistence ---------------- */
function flushRegistry() { dirty = false; saveJSON(REGISTRY_PATH, registry); }
function scheduleSave()  { dirty = true; if (saveTimer) return; saveTimer = setTimeout(() => { saveTimer = null; if (dirty) flushRegistry(); }, SAVE_THROTTLE_MS); }
function saveFlagged()    { saveJSON(BLACKLIST_PATH, [...flagged]); }
function saveFNames()     { saveJSON(FNAMES_PATH, [...flaggedNames]); }
function saveFIds()       { saveJSON(FIDS_PATH, [...flaggedIds]); }
function saveUntracked()  { saveJSON(UNTRACKED_PATH, [...untrackedNames]); }
function saveUntrackedIds() { saveJSON(UNTRACKED_IDS_PATH, Object.fromEntries(untrackedIds)); }

/* ---------------- lookups ---------------- */
function resolveIds(input) {
  const key = norm(input);
  if (registry[input]) return [input];
  // registry keys are CLEANED ids - also try the cleaned form of the input, so
  // prefixed tokens ("NULL:abc...", "EOS:abc...") still resolve to their entry.
  const cid = cleanId(input);
  if (cid && registry[cid]) return [cid];
  const ckey = norm(cid);
  const idHit = Object.keys(registry).find(id => norm(id) === key || (ckey && norm(id) === ckey));
  if (idHit) return [idHit];
  return Object.keys(registry).filter(id => norm(registry[id].name) === key);
}
function ipsForIds(ids) {           // all IPs ever seen (incl. best-effort join correlation) - for display
  const set = new Set();
  for (const id of ids) for (const ip of (registry[id]?.ips || [])) set.add(ip);
  return [...set];
}
function confirmedIpsForIds(ids) { // only same-line (disconnect) IPid pairings - trustworthy, for alts/enforcement
  const set = new Set();
  for (const id of ids) for (const ip of (registry[id]?.cips || [])) set.add(ip);
  return [...set];
}
// Alt = another id that shares a CONFIRMED IP (same-line disconnect pairing).
function altIdsForIps(ips, excludeIds = []) {
  const ex = new Set(excludeIds.map(norm));
  const set = new Set();
  for (const [id, e] of Object.entries(registry))
    if (!ex.has(norm(id)) && (e.cips || []).some(ip => ips.includes(ip))) set.add(id);
  return [...set];
}
// Everything Pavlov.log has taught us about a player: name, all/confirmed IPs,
// first/last seen, and shared-IP alt usernames. Returns null if unknown.
function getRecord(input) {
  const ids = resolveIds(input);
  if (!ids.length) return null;
  const ips = new Set(), cips = new Set();
  let name = null, firstSeen = Infinity, lastSeen = 0, logins = 0;
  let recent = [];
  for (const id of ids) {
    const e = registry[id]; if (!e) continue;
    if (e.name && !name) name = e.name;
    for (const ip of (e.ips || []))  ips.add(ip);
    for (const ip of (e.cips || [])) cips.add(ip);
    if (e.firstSeen) firstSeen = Math.min(firstSeen, e.firstSeen);
    if (e.lastSeen)  lastSeen  = Math.max(lastSeen, e.lastSeen);
    logins += e.logins || 0;
    if (Array.isArray(e.recent)) recent = recent.concat(e.recent);
  }
  recent.sort((a, b) => (b.ts || 0) - (a.ts || 0));   // newest first
  const nm = norm(name);
  return {
    name: name || String(input),
    id: ids[0],
    ids,
    ips: [...ips],
    cips: [...cips],
    firstSeen: firstSeen === Infinity ? null : firstSeen,
    lastSeen: lastSeen || null,
    alts: altNamesForIps([...cips], ids),
    logins,
    recent: recent.slice(0, 8),
    bypass: untrackedNames.has(nm),                                   // ignore-listed (never auto-banned/tracked)
    flagged: [...cips].some(ipFlagged) || flaggedNames.has(nm) || ids.some(id => flaggedIds.has(id)),
  };
}

/* ---------------- record an observation ---------------- */
// sure = true when the IP and id came from the SAME log line (disconnect) - a
// trustworthy pairing. false = best-effort join-time correlation (display only).
function record(id, name, ip, ts, sure) {
  if (skipId(id)) return;
  id = cleanId(id);
  if (untrackedIds.has(id)) return;                // ignore-listed player - never track
  const fresh = !registry[id];
  const e = registry[id] || { name: null, ips: [], cips: [], firstSeen: ts || Date.now(), lastSeen: 0 };
  if (!e.cips) e.cips = [];                         // migrate older registry entries
  let changed = fresh, newIp = false;
  if (name && name !== "<null>" && e.name !== name) { e.name = name; changed = true; }
  if (ip && !e.ips.includes(ip)) { e.ips.push(ip); changed = true; newIp = true; }
  if (ip && sure && !e.cips.includes(ip)) {
    e.cips.push(ip); changed = true;                                             // confirmed pairing
    // recently banned with no IP flagged yet? flag this confirmed IP now (catches alts of a never-disconnected ban)
    const pf = pendingFlag.get(id);
    if (pf && Date.now() - pf <= PENDING_FLAG_MS) {
      if (!flagged.has(ip)) {
        flagged.add(ip); saveFlagged();
        console.log(`[ipBans] BAN-FLAG confirmed IP ${ip} for recently-banned ${e.name || id} [${id}] (pending ${Math.round((Date.now() - pf) / 1000)}s)`);
      }
      pendingFlag.delete(id); savePendingFlag();    // satisfied - don't re-flag on later disconnects
    }
  }
  e.lastSeen = Math.max(e.lastSeen || 0, ts || Date.now());
  registry[id] = e;
  if (changed) scheduleSave();
  if (newIp && live) console.log(`[ipBans] learned ${e.name || id} [${id}] @ ${ip}${sure ? "" : " (tentative)"}`);
}

// Resolve alt ids -> their usernames (skip any with no known name).
function altNamesForIps(ips, excludeIds = []) {
  return altIdsForIps(ips, excludeIds).map(id => registry[id]?.name).filter(Boolean);
}

/* ---------------- public: flag / unflag a player ---------------- */
// Flags the player's username(s) AND confirmed IP(s). Any account that later
// connects matching either is auto-banned. (No hex ids - Shack bans by name.)
function blacklistPlayer(input, opts = {}) {
  const flagId = opts.flagId === true;
  const ids  = resolveIds(input);
  // Flag ONLY CONFIRMED IPs (same-line disconnect pairings - 100% reliable). Join-time
  // IPs are a timing GUESS and can be mis-correlated to a stranger, so flagging them
  // was auto-banning unrelated players ("random IP bans"). If the banned player has no
  // confirmed IP yet (still online, never disconnected), nothing is flagged now —
  // pendingFlag flags their real IP the moment their disconnect confirms it.
  const cips = confirmedIpsForIds(ids);
  const ips  = cips;
  const altNames = altNamesForIps(cips, ids);
  let added = 0;
  for (const ip of ips) if (!flagged.has(ip)) { flagged.add(ip); added++; }
  if (added) saveFlagged();
  console.log(`[ipBans] blacklistPlayer "${input}" -> ${ids.length} id(s), flagged ${ips.length} confirmed IP(s), ${flagged.size} total flagged`);
  // remember the player so the IP the kick confirms is flagged too (if none yet).
  // NOTE: we deliberately do NOT auto-flag the username here. A flagged username
  for (const id of ids) pendingFlag.set(id, Date.now());
  if (ids.length) savePendingFlag();               // survive a restart inside the window
  // Flag the EOS/unique id(s) ONLY for permanent bans (flagId). Temp bans, warn
  // escalations and in-game-detected bans must NOT permanently flag an account id -
  // that's what was auto-banning innocent/returning players.
  let fId = false;
  if (flagId) for (const id of ids) if (!flaggedIds.has(id)) { flaggedIds.add(id); fId = true; }
  if (fId) saveFIds();
  // banned by a raw token not in the registry: flag it as an IP; only flag it as an
  // EOS id when it actually LOOKS like one (long hex) - never turn a display name into an id.
  if (!ids.length) {
    const raw = String(input ?? "").trim();
    if (IPV4_RE.test(raw) && !flagged.has(raw)) { flagged.add(raw); added++; saveFlagged(); }
    else if (flagId) { const cid = cleanId(raw); if (cid && /^[0-9a-f]{16,}$/i.test(cid) && !skipId(raw) && !flaggedIds.has(cid)) { flaggedIds.add(cid); saveFIds(); } }
  }
  return {
    ids, ips, alts: altNames,
    field: {
      name: "IP Enforcement",
      value: (ips.length
        ? `Flagged **${ips.length}** IP${ips.length !== 1 ? "s" : ""} + ${ids.length} account id(s) - any account from them is auto-banned.`
        : (ids.length ? `Flagged **${ids.length}** account id(s) - any reconnect with them is auto-banned.` : "No connection data on record yet.")) +
        (altNames.length ? `\nShares an IP with: ${altNames.map(a => `\`${a}\``).join("  -  ")}` : ""),
      inline: false,
    },
  };
}
function unblacklistPlayer(input) {
  const ids = resolveIds(input);
  const ips = ipsForIds(ids);
  let nIp = 0, nName = 0, nId = 0;
  for (const ip of ips) if (!manualIps.has(ip) && flagged.delete(ip)) nIp++;   // never clear an admin's manual IP flag
  for (const id of ids) {
    const nm = norm(registry[id]?.name);
    if (nm && flaggedNames.delete(nm)) nName++;
    if (flaggedIds.delete(id)) nId++;
    if (pendingFlag.delete(id)) savePendingFlag();   // an unban cancels a pending IP flag
  }
  // also clear a raw name/ip/id token (e.g. unbanning by a value not in the registry)
  const raw = String(input ?? "").trim();
  if (flaggedNames.delete(norm(raw))) nName++;
  if (!manualIps.has(raw) && flagged.delete(raw)) nIp++;
  if (flaggedIds.delete(cleanId(raw))) nId++;
  if (nIp) saveFlagged();
  if (nName) saveFNames();
  if (nId) saveFIds();
  return { ids, ips, cleared: { ips: nIp, names: nName, ids: nId } };
}

/* ---------------- public: clearing logged data (stop false bans) ---------------- */
// Manually blacklist an IP so any account that connects from it is auto-banned.
// Returns the ids already known to use that IP so the caller can ban them now too.
function idsWithIp(ip) {
  const set = new Set();
  for (const [id, e] of Object.entries(registry))
    if ((e.ips || []).includes(ip) || (e.cips || []).includes(ip)) set.add(id);
  return [...set];
}
function flagIp(ip) {
  ip = String(ip || "").trim();
  const added = !!ip && !flagged.has(ip);
  if (added) { flagged.add(ip); saveFlagged(); }
  if (ip) { manualIps.add(ip); saveManualIps(); }   // deliberate flag - unbanning a player never clears it
  return { added, ip, ids: idsWithIp(ip) };
}
// Manually blacklist an IP or a username (IPv4 detected by shape; anything else
// is treated as a username). Returns the accounts already on record matching it.
function flagTarget(input) {
  const val = String(input || "").trim();
  if (IPV4_RE.test(val)) {                              // IPv4
    const added = !flagged.has(val); if (added) { flagged.add(val); saveFlagged(); }
    manualIps.add(val); saveManualIps();              // deliberate flag - survives player unbans
    return { kind: "IP", value: val, added, ids: idsWithIp(val) };
  }
  const ids = resolveIds(val);                          // known player by username?
  let fName = false;
  if (ids.length) {
    for (const id of ids) { const nm = norm(registry[id]?.name); if (nm && !flaggedNames.has(nm)) { flaggedNames.add(nm); fName = true; } }
    if (fName) saveFNames();
    return { kind: "username", value: val, added: fName, ids };
  }
  const nmF = norm(val);
  if (nmF && !flaggedNames.has(nmF)) { flaggedNames.add(nmF); fName = true; }
  if (fName) saveFNames();
  return { kind: "username", value: val, added: fName, ids: [] };
}
// Everything currently blacklisted, by category.
function getBlacklist() { return { ips: [...flagged], names: [...flaggedNames], ids: [...flaggedIds] }; }
// Clear ALL flags (IPs + usernames + account ids). Stops every auto-ban. Registry kept.
function clearFlags() {
  const n = flagged.size + flaggedNames.size + flaggedIds.size;
  flagged.clear(); flaggedNames.clear(); flaggedIds.clear(); manualIps.clear(); pendingFlag.clear();
  saveFlagged(); saveFNames(); saveFIds(); saveManualIps(); savePendingFlag();
  return n;
}
// Clear only the flagged USERNAMES (keep flagged IPs). Stops "blacklisted
// username" auto-bans without losing IP-based alt enforcement.
function clearFlaggedNames() {
  const n = flaggedNames.size;
  flaggedNames.clear(); saveFNames();
  return n;
}
// Remove one IP from the flag list AND from every player's recorded IPs.
function clearIp(ip) {
  const flagRemoved = flagged.delete(ip) ? 1 : 0;
  if (flagRemoved) saveFlagged();
  if (manualIps.delete(ip)) saveManualIps();   // deliberate clear releases the manual protection too
  let players = 0;
  for (const e of Object.values(registry)) {
    let hit = false;
    if (e.ips)  { const i = e.ips.indexOf(ip);  if (i >= 0) { e.ips.splice(i, 1);  hit = true; } }
    if (e.cips) { const i = e.cips.indexOf(ip); if (i >= 0) { e.cips.splice(i, 1); hit = true; } }
    if (hit) players++;
  }
  if (players) scheduleSave();
  return { flagRemoved, players };
}
// Wipe the whole IP registry AND all flags (full reset). Untracked list is kept.
// Also set a cutoff at the latest log time so the next restart's backfill won't
// rebuild what we just wiped - only connections AFTER the wipe are tracked again.
function clearAll() {
  const ids = Object.keys(registry).length, fl = flagged.size + flaggedNames.size + flaggedIds.size;
  for (const k of Object.keys(registry)) delete registry[k];
  flagged.clear(); flaggedNames.clear(); flaggedIds.clear(); manualIps.clear(); pendingFlag.clear();
  cutoffTs = Math.max(cutoffTs, lastTs || Date.now());
  flushRegistry(); saveFlagged(); saveFNames(); saveFIds(); saveManualIps(); savePendingFlag(); saveCutoff();
  return { ids, flagged: fl };
}

// Clear all kill/death + victim-tally stats (part of a full player-data wipe).
// Returns how many players had K/D on record.
function clearKD() {
  const n = Object.keys(kd).length;
  for (const k of Object.keys(kd)) delete kd[k];
  for (const k of Object.keys(killLog)) delete killLog[k];
  flushKD(); flushKillLog();
  return n;
}

/* ---------------- public: untracked (ignore) list by username ---------------- */
// Add a username the IP system should never track. Also purges any existing
// registry entries for that name and remembers their ids so their disconnect
// lines are ignored too. Returns { name, purged }.
function addUntracked(name) {
  const key = norm(name);
  if (!key) return { name: key, purged: 0 };
  untrackedNames.add(key);
  saveUntracked();
  let purged = 0;
  for (const [id, e] of Object.entries(registry))
    if (norm(e.name) === key) { untrackedIds.set(id, key); delete registry[id]; purged++; }
  if (purged) scheduleSave();
  return { name: key, purged };
}
function removeUntracked(name) {
  const key = norm(name);
  const had = untrackedNames.delete(key);
  if (had) saveUntracked();
  // Also release the ids ignored under this name, or record() keeps dropping
  // their lines until a restart and tracking never actually resumes.
  let freed = 0;
  for (const [id, nm] of untrackedIds) if (nm === key) { untrackedIds.delete(id); freed++; }
  if (freed) saveUntrackedIds();
  return had;
}
function getUntracked() { return [...untrackedNames]; }

/* ---------------- log parsing ---------------- */
function parseTs(line) {
  const m = line.match(TS_RE);
  if (!m) return null;
  const [, Y, Mo, D, H, Mi, S, ms] = m;
  return Date.UTC(+Y, +Mo - 1, +D, +H, +Mi, +S, +ms);
}

async function handleJoin(name, rawId, ip, ts, server, confident) {
  if (/localhost-/i.test(rawId || "")) return;     // server self-connection
  if (name && untrackedNames.has(norm(name))) {    // ignore-listed username - don't track at all
    if (!skipId(rawId)) { const cid = cleanId(rawId); if (!untrackedIds.has(cid)) { untrackedIds.set(cid, norm(name)); saveUntrackedIds(); } }   // also skip their disconnect lines
    // Master names still get a live join callback (with NO IP) so the bot can hand
    // them a menu on join - but nothing about them is recorded.
    if (live && masterNames.has(norm(name)) && Date.now() - (recentJoin.get(norm(name)) ?? 0) >= JOIN_DEBOUNCE_MS) {
      recentJoin.set(norm(name), Date.now());
      try { await onConnect({ name, ip: null, server }); } catch (e) { console.error("[ipBans] onConnect(master) failed:", e.message); }
    }
    return;
  }
  const valid = !skipId(rawId);
  const id    = valid ? cleanId(rawId) : null;
  // Record the join IP into the registry ONLY when the correlation is unambiguous
  // (exactly one pending connection). With concurrent joins the accept<->login pairing
  if (valid) record(rawId, name, confident ? ip : null, ts, false);

  if (!live) return;                               // startup backfill: don't feed/auto-ban old joins

  const display = (name && name !== "<null>") ? name : (id || "unknown");

  // connection feed - once per join (Pavlov logs an INVALID pre-auth line + the authed one)
  const key  = norm(display);
  if (Date.now() - (recentJoin.get(key) ?? 0) >= JOIN_DEBOUNCE_MS) {
    recentJoin.set(key, Date.now());
    // `confident` = the join IP was unambiguously correlated (exactly one pending
    // connection) - safe for the bot to act on (e.g. VPN ban+kick while online).
    try { await onConnect({ name: display, ip: ip || null, server, confident }); }
    catch (e) { console.error("[ipBans] onConnect failed:", e.message); }
  }

  // auto-ban if this join matches a flagged username or IP.
  //  • username comes straight from the login line -> reliable, ban on sight.
  if (valid && Date.now() - (recentAuto.get(id) ?? 0) >= AUTO_DEBOUNCE_MS) {
    // Ban-on-join match, EXACT only:
    //  • flagged EOS id           - straight off the login line, most reliable
    //  • flagged username         - deliberate manual flag
    //  • a CONFIRMED IP on record  - this account previously disconnected from a flagged IP
    //  • the current join's IP     - only when the correlation is unambiguous (confident)
    // Never a mis-correlated join IP (e.ips) - that would ban an innocent.
    const e = registry[id];
    const knownFlagged = e && (e.cips || []).some(ipFlagged);
    const reason = flaggedIds.has(id)                        ? "blacklisted account (EOS id)"
                 : (name && flaggedNames.has(norm(name)))    ? "blacklisted username"
                 : knownFlagged                              ? "blacklisted IP"
                 : (confident && ip && ipFlagged(ip))        ? "blacklisted IP"
                 : null;
    if (reason) {
      recentAuto.set(id, Date.now());
      try { await onAutoBan({ name: display, uniqueId: id, ip: ip || null, server, reason }); }
      catch (e) { console.error("[ipBans] onAutoBan failed:", e.message); recentAuto.delete(id); }
    }
  }
}

function parseLine(line, server, key) {
  const t  = parseTs(line);
  if (t != null) lastTs = t;
  const ts = t ?? lastTs;
  // Ignore everything at/before the last "wipe all IP data" so a restart's log
  // backfill can't rebuild the data you just cleared.
  if (cutoffTs && ts && ts < cutoffTs) return;

  // Kill/death stats - only live (backfill would re-count old kills on restart).
  // Handles BOTH a single-line JSON record ({"Killer":..,"Killed":..,"KilledBy":..})
  if (live) {
    const mk = line.match(KILLER_RE);
    const md = line.match(KILLED_RE);
    const mb = line.match(KILLEDBY_RE);
    if (mk || md || mb) {
      let pk = pendingKill[key] || {};
      if (mk) pk = { killer: mk[1] };                    // "Killer" starts a NEW record - drop any stale block
      else if (md && pk.killed) pk = {};                 // second victim without a finalize - stale block, start fresh
      if (md) pk.killed = md[1];
      // KilledBy is the last field of a kill record (single- or multi-line) - finalize.
      if (mb) {
        pk.killedBy = mb[1];
        recordKill(pk);
        // Only a real PvP kill (a distinct killer) is feed-worthy - not a suicide,
        // fall damage, or other environmental death (no real killer, or self).
        if (pk.killer && pk.killed && norm(pk.killer) !== norm(pk.killed)) {
          Promise.resolve(onKill({ killer: pk.killer, killed: pk.killed, server }))
            .catch(e => console.error("[ipBans] onKill failed:", e.message));
        }
        pk = {};
      }
      pendingKill[key] = pk;
      return;
    }
  }

  // 1) disconnect line - IP + real id on the SAME line (most reliable; no live gate needed)
  const c = extractClose(line);
  if (c && !skipId(c.id)) {
    const id = cleanId(c.id), ip = c.ip;
    const nm = line.match(NAME_OPT_RE);         // some close lines also carry ?Name= - attach it if so
    record(c.id, nm ? nm[1].trim() : null, ip, ts, true);   // confirmed same-line pairing
    // count one completed connection per disconnect (a disconnect emits several
    // close lines a few ms apart - collapse them by LOG time so backfill counts too).
    const e = registry[id];
    if (e && (ts - (e.lastCountedTs ?? -Infinity)) > 5000) {
      e.lastCountedTs = ts;
      e.logins = (e.logins || 0) + 1;
      e.recent = Array.isArray(e.recent) ? e.recent : [];
      if (!(e.recent[0] && e.recent[0].ts === ts && e.recent[0].ip === ip)) e.recent.unshift({ ts: ts || Date.now(), ip });
      if (e.recent.length > 8) e.recent.length = 8;
      scheduleSave();
    }
    // fire the feed on the CONFIRMED IP (accurate), deduped per id per disconnect.
    // Only when we actually know who this is (a login captured a name) - skip
    // anonymous/partial connections so the feed isn't full of "unknown".
    if (live && registry[id]?.name && !untrackedIds.has(id) && Date.now() - (recentConfirm.get(id) ?? 0) >= CONFIRM_DEBOUNCE_MS) {
      recentConfirm.set(id, Date.now());
      const rec = getRecord(id) || {};   // look up by id, not the (maybe missing) name
      Promise.resolve(onConfirm({ name: registry[id].name, ip, server, record: rec }))
        .catch(e => console.error("[ipBans] onConfirm failed:", e.message));
    }
    // retroactive auto-ban: an alt that slipped the ambiguous join check is caught
    // here with the 100%-accurate confirmed IP. Skip the freshly-banned account
    // itself (pendingFlag). The login-time check also catches them next connect.
    if (live && (ipFlagged(ip) || flaggedIds.has(id)) && registry[id]?.name && !pendingFlag.has(id) && !untrackedIds.has(id)
        && Date.now() - (recentAuto.get(id) ?? 0) >= AUTO_DEBOUNCE_MS) {
      recentAuto.set(id, Date.now());
      Promise.resolve(onAutoBan({ name: registry[id].name, uniqueId: id, ip, server, reason: flaggedIds.has(id) ? "blacklisted account (EOS id)" : "blacklisted IP" }))
        .catch(e => { console.error("[ipBans] onAutoBan (confirm) failed:", e.message); recentAuto.delete(id); });
    }
    return;
  }

  // 2) accept line - remember the IP for the upcoming login. Track DISTINCT pending
  //    IPs so we can tell an unambiguous join (1) from concurrent joins (>1). Accepts
  const a = line.match(ACCEPT_RE);
  if (a) {
    if (pendingSet[key] && ts - (pendingTs[key] ?? 0) > CORRELATE_WINDOW_MS) pendingSet[key].clear();
    pendingIP[key] = a[1]; pendingTs[key] = ts; (pendingSet[key] || (pendingSet[key] = new Set())).add(a[1]);
    return;
  }

  // 3) login line - name + id, correlated with the most recent accept IP
  const lg = extractLogin(line);
  if (lg) {
    const within    = ts - (pendingTs[key] ?? 0) <= CORRELATE_WINDOW_MS;
    const ip        = within ? (pendingIP[key] ?? null) : null;
    const confident = within && !!pendingSet[key] && pendingSet[key].size === 1;   // exactly one connection pending
    // Pavlov often logs a pre-auth login (userId INVALID) before the real one.
    // Only a VALID id consumes the pending accept-IP - otherwise the throwaway
    // line eats it and the authed login correlates to nothing.
    if (!skipId(lg.id)) { pendingIP[key] = null; if (pendingSet[key]) pendingSet[key].clear(); }
    handleJoin(lg.name, lg.id, ip, ts, server, confident);
    return;
  }

  // 4) ban/unban from ANY admin tool - flag/clear that player's IPs (live only)
  if (!live) return;
  const b = line.match(BAN_RE);
  if (b) { const r = blacklistPlayer(b[1]); if (r.ips.length) console.log(`[ipBans] ban detected "${b[1]}" - flagged ${r.ips.length} IP(s)${r.alts.length ? `, alts: ${r.alts.join(", ")}` : ""}`); return; }
  const u = line.match(UNBAN_RE);
  if (u) { unblacklistPlayer(u[1]); }
}

/* ---------------- polling reader ---------------- */
const inodes = {};   // per-path inode - detects rotation even when the new file is already larger than our offset
function poll() {
  for (const f of watchList) {
    let st; try { st = fs.statSync(f); } catch { continue; }
    if (st.ino && inodes[f] && st.ino !== inodes[f]) {
      // File replaced under the same path. A fresh rotated log is near-empty so we read
      // it all; but if a LARGE file was swapped in (a restored backup / non-empty
      // rotation), only tail it - replaying its whole history as live events would
      // inflate login counts and re-fire the feed/auto-ban for old connections.
      offsets[f] = Math.max(0, st.size - MAX_BACKFILL_BYTES);
      leftover[f] = "";
    }
    if (st.ino) inodes[f] = st.ino;
    let from = offsets[f];
    if (from === undefined) from = Math.max(0, st.size - MAX_BACKFILL_BYTES);
    if (st.size < from) { from = 0; leftover[f] = ""; }   // rotated / truncated
    if (st.size === from) { offsets[f] = st.size; continue; }
    let buf;
    try {
      const fd = fs.openSync(f, "r");
      buf = Buffer.alloc(st.size - from);
      const n = fs.readSync(fd, buf, 0, buf.length, from);   // may be short if the file shrank between stat and read
      fs.closeSync(fd);
      buf = buf.subarray(0, n);
      offsets[f] = from + n;             // advance by what we actually read, not the stale stat size
    } catch { continue; }
    const lines = ((leftover[f] || "") + buf.toString("utf8")).split(/\r?\n/);
    leftover[f] = lines.pop();          // last element is an incomplete line - buffer it
    const label = labelFor(f);
    for (const l of lines) if (l) parseLine(l, label, f);
  }
  if (dirty) flushRegistry();
  if (kdDirty) flushKD();
  if (killLogDirty) flushKillLog();
  pruneDebounceMaps();
}

// Flush every throttled store now - called by the bot's graceful shutdown so a
// pm2 restart can't drop the last few seconds of registry/K-D/kill-log updates.
function flushAll() {
  if (dirty) flushRegistry();
  if (kdDirty) flushKD();
  if (killLogDirty) flushKillLog();
}

// Drop stale debounce entries so these maps don't grow without bound over long uptime.
let lastPrune = 0;
function pruneDebounceMaps() {
  const now = Date.now();
  if (now - lastPrune < 60_000) return;            // at most once a minute
  lastPrune = now;
  const sweep = (map, ttl) => { for (const [k, t] of map) if (now - t > ttl) map.delete(k); };
  sweep(recentJoin,    JOIN_DEBOUNCE_MS * 2);
  sweep(recentConfirm, CONFIRM_DEBOUNCE_MS * 2);
  sweep(recentAuto,    AUTO_DEBOUNCE_MS * 2);
  const beforePF = pendingFlag.size;
  sweep(pendingFlag,   PENDING_FLAG_MS * 2);
  if (pendingFlag.size !== beforePF) savePendingFlag();   // keep the persisted copy in step
}

/* ---------------- log discovery ---------------- */
function listDirs(p) {
  try { return fs.readdirSync(p, { withFileTypes: true }).filter(d => d.isDirectory()).map(d => path.join(p, d.name)); }
  catch { return []; }
}
// Find active Pavlov.log(s) by probing the fixed tail under a few likely roots
// (≤2 wildcard levels - no full-filesystem walk). Newest first, capped.
function discoverLogs(roots) {
  if (!roots) {
    roots = [...listDirs("/home"), "/root", "/opt", "/srv"];
    if (process.env.HOME) roots.push(process.env.HOME);
  }
  const found = new Set();
  for (const root of roots) {
    if (exists(path.join(root, LOG_TAIL))) found.add(path.join(root, LOG_TAIL));
    for (const l1 of listDirs(root)) {
      if (exists(path.join(l1, LOG_TAIL))) found.add(path.join(l1, LOG_TAIL));
      for (const l2 of listDirs(l1)) if (exists(path.join(l2, LOG_TAIL))) found.add(path.join(l2, LOG_TAIL));
    }
  }
  return [...found].sort((a, b) => mtimeOf(b) - mtimeOf(a)).slice(0, 4);
}
// Pavlov rotates the active log to Pavlov-backup-<ts>.log on restart; pull those
// siblings in too so the one-time backfill recovers history from before a restart.
function siblingLogs(f) {
  try { return fs.readdirSync(path.dirname(f)).filter(n => /^Pavlov.*\.log$/i.test(n)).map(n => path.join(path.dirname(f), n)); }
  catch { return [f]; }
}

// Resolve the set of log files to read, in priority order.
function resolveLogFiles(opts = {}) {
  if (Array.isArray(opts.logFiles) && opts.logFiles.length) return { files: [...opts.logFiles], how: "opts.logFiles" };
  if (process.env.PAVLOV_LOGS) {
    const files = process.env.PAVLOV_LOGS.split(/[,:]/).map(s => s.trim()).filter(Boolean);
    if (files.length) return { files, how: "PAVLOV_LOGS" };
  }
  const discovered = discoverLogs(opts.roots);
  if (discovered.length) return { files: discovered, how: "auto-detected" };
  return { files: [DEFAULT_LOG], how: "default" };
}

/* ---------------- init ---------------- */
function init(opts = {}) {
  if (typeof opts.onAutoBan === "function") onAutoBan = opts.onAutoBan;
  if (typeof opts.onConnect === "function") onConnect = opts.onConnect;
  if (typeof opts.onConfirm === "function") onConfirm = opts.onConfirm;
  if (typeof opts.onKill    === "function") onKill    = opts.onKill;

  // Master/owner in-game names are never tracked (no IP logging, feed, or auto-ban)
  // but DO still get a live join callback so the bot can hand them a menu. Seeded
  // in-memory only - hardcoded in the bot, so not written to the untracked file.
  if (Array.isArray(opts.masters)) for (const m of opts.masters) { const k = norm(m); if (k) { untrackedNames.add(k); masterNames.add(k); } }

  const { files, how } = resolveLogFiles(opts);
  console.log(`[ipBans] log source: ${how}`);
  for (const f of files) {
    try { const st = fs.statSync(f); console.log(`[ipBans] log OK  (${(st.size / 1048576).toFixed(1)} MB): ${f}`); }
    catch (e) { console.warn(`[ipBans] log MISSING/unreadable: ${f} - ${e.code || e.message}. Set PAVLOV_LOGS in .env.`); }
  }

  // expand each log into itself + sibling rotated logs, oldest -> newest so the latest name/lastSeen wins
  const expanded = new Set();
  for (const f of files) { for (const s of siblingLogs(f)) expanded.add(s); expanded.add(f); }
  watchList = [...expanded].sort((a, b) => mtimeOf(a) - mtimeOf(b));
  const backups = watchList.filter(f => !files.includes(f));
  if (backups.length) console.log(`[ipBans] also backfilling ${backups.length} rotated log(s): ${backups.map(b => path.basename(b)).join(", ")}`);

  // Ignore-list reconciliation across restarts:
  //  • add ids for any registry entry whose name is ignore-listed (disconnect lines
  //    carry no name to re-match on, so we need the id set), and
  //  • drop any persisted id whose name is no longer ignored (e.g. removed out-of-band
  //    while the bot was down) - otherwise that player is dropped forever.
  let untrackedDirty = false;
  for (const [id, e] of Object.entries(registry)) {
    if (e && untrackedNames.has(norm(e.name))) { untrackedIds.set(id, norm(e.name)); delete registry[id]; untrackedDirty = true; }
  }
  for (const [id, nm] of untrackedIds) {
    if (!untrackedNames.has(nm)) { untrackedIds.delete(id); untrackedDirty = true; }   // no longer ignored -> resume tracking
  }
  if (untrackedDirty) saveUntrackedIds();

  live = false; poll();              // backfill (no feed / no auto-ban for old joins)
  live = true;                       // everything from here on is a live event

  // Linkage health: how many accounts have a NAME and a CONFIRMED IP. If ids are
  // known but none are fully linked, the log format isn't pairing IPs to accounts
  // (so account bans can't flag IPs) - surface it loudly with the fix pointer.
  const all   = Object.keys(registry);
  const named = all.filter(id => registry[id].name).length;
  const cipd  = all.filter(id => (registry[id].cips || []).length).length;
  const linked = all.filter(id => registry[id].name && (registry[id].cips || []).length).length;
  console.log(`[ipBans] ready - ${all.length} known IDs (${named} named, ${cipd} with a confirmed IP, ${linked} fully linked), ${flagged.size} flagged IPs. Watching ${watchList.length} file(s) every ${(opts.pollMs || POLL_MS) / 1000}s.`);
  if (all.length && !linked) console.warn(`[ipBans] WARNING: no account has a confirmed IP yet - account bans can't flag IPs. Run \`node ipBans.js\` and check the close=(IP+id) count to verify the log format.`);
  return setInterval(poll, opts.pollMs || POLL_MS);
}

/* ---------------- exports ---------------- */
module.exports = {
  init,
  blacklistPlayer,
  unblacklistPlayer,
  resolveIds,
  ipsForIds,
  getIPsForPlayer:          (input) => ipsForIds(resolveIds(input)),            // all (incl. tentative) - display
  getConfirmedIPsForPlayer: (input) => confirmedIpsForIds(resolveIds(input)),   // only same-line confirmed pairings
  getAltsOf:                (input) => altIdsForIps(confirmedIpsForIds(resolveIds(input)), resolveIds(input)),
  getAltNamesOf:            (input) => altNamesForIps(confirmedIpsForIds(resolveIds(input)), resolveIds(input)),
  getRecord,
  getKD,
  topKD,
  getKills,
  flushAll,
  addUntracked,
  removeUntracked,
  getUntracked,
  flagIp,
  flagTarget,
  getBlacklist,
  clearFlags,
  clearFlaggedNames,
  clearIp,
  clearAll,
  clearKD,
  discoverLogs,
  registry,
  get blacklist() { return [...flagged]; },
};

/* ---------------- CLI self-test:  node ipBans.js  [logfile ...] ---------------- */
if (require.main === module) {
  const bar = "─".repeat(64);
  const argFiles = process.argv.slice(2);
  const { files, how } = resolveLogFiles(argFiles.length ? { logFiles: argFiles } : {});
  console.log(bar);
  console.log("ipBans self-test");
  console.log("uid", process.getuid ? process.getuid() : "?", " - source:", how, " - PAVLOV_LOGS:", process.env.PAVLOV_LOGS || "(unset)");
  console.log(bar);
  for (const f of files) {
    console.log(`\n### ${f}`);
    let st; try { st = fs.statSync(f); } catch (e) { console.log(`  ✗ cannot stat: ${e.code || e.message}`); continue; }
    console.log(`  ✓ ${(st.size / 1048576).toFixed(2)} MB, modified ${st.mtime.toISOString()}`);
    try { fs.accessSync(f, fs.constants.R_OK); console.log("  ✓ readable"); } catch { console.log("  ✗ NOT readable by this user"); continue; }
    const tail = (() => { const from = Math.max(0, st.size - 2 * 1024 * 1024); const fd = fs.openSync(f, "r"); const b = Buffer.alloc(st.size - from); fs.readSync(fd, b, 0, b.length, from); fs.closeSync(fd); return b.toString("utf8").split(/\r?\n/); })();
    let nA = 0, nL = 0, nC = 0, nB = 0, nK = 0;
    for (const l of tail) { if (ACCEPT_RE.test(l)) nA++; if (extractLogin(l)) nL++; if (extractClose(l)) nC++; if (BAN_RE.test(l)) nB++; if (KILLEDBY_RE.test(l)) nK++; }
    console.log(`  matches in last ${tail.length} lines:  accept=${nA}  login=${nL}  close(IP+id)=${nC}  ban=${nB}  kill(KilledBy)=${nK}`);
    if (!nL && !nC) console.log("  no join/disconnect lines matched - wrong/empty log, or unexpected format.");
    if (!nK) console.log("  no kill data matched - set bVerboseLogging=true in Game.ini (else K/D can't work).");
    // Dump real sample lines so the exact format can be matched if the counts are low.
    const sample = (label, test, n = 2) => {
      const hits = tail.filter(test).slice(-n);
      if (hits.length) { console.log(`  sample ${label}:`); hits.forEach(l => console.log("    " + l.slice(0, 220))); }
      else console.log(`  sample ${label}: (none found)`);
    };
    sample("KillData lines",   l => /"Kill(ed|er|edBy)"/i.test(l));
    sample("RemoteAddr lines", l => /RemoteAddr/i.test(l));
    sample("UniqueId lines",   l => /UniqueId/i.test(l));
    sample("Login/Join lines", l => /Login request:|Join request:/i.test(l));
  }
  console.log(`\n${bar}\nRunning backfill...\n${bar}`);
  clearInterval(init({ logFiles: files, pollMs: 9e8 }));
  const ids = Object.keys(registry), withIp = ids.filter(id => (registry[id].ips || []).length);
  console.log(`\nregistry: ${ids.length} players, ${withIp.length} with an IP`);
  withIp.slice(0, 25).forEach(id => console.log(`  ${registry[id].name || "?"}  [${id}]  ->  ${registry[id].ips.join(", ")}`));
  console.log(`\n${bar}\n${withIp.length ? "RESULT: IPs ARE being captured." : "RESULT: no IPs captured - paste this whole output back."}\n${bar}`);
}
