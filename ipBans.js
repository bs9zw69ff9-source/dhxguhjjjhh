/* ================================================================
 * ipBans.js — IP ↔ player mapping + IP-match auto-ban (log-watch + RCON)
 * ================================================================
 * Tails the Pavlov dedicated-server log(s) and learns IP↔ID from TWO sources,
 * keyed by the hex unique ID:
 *   • "Login request" lines        -> name + uniqueId (+ correlated join IP)
 *   • disconnect / cleanup lines   -> RemoteAddr + UniqueId on the SAME line
 *
 * How enforcement works (no firewall, no webhook — just logs + RCON):
 *   1. blacklistPlayer(idOrName)   flags every IP that player has used.
 *   2. The bot keeps watching the LIVE log. When ANY player connects from a
 *      flagged IP, onAutoBan() fires so the bot RCON-bans that account.
 *   unblacklistPlayer(idOrName)    clears the flags for that player's IPs.
 *
 * LOG PATHS: defaults assume the bot runs as the user that owns the server.
 * If not (e.g. bot as root, server as `steam`), set PAVLOV_LOGS in .env:
 *   PAVLOV_LOGS=/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log,/home/steam/pavlovserver1/Pavlov/Saved/Logs/Pavlov.log
 * ================================================================ */

const fs   = require("fs");
const path = require("path");

/* ---------------- CONFIG ---------------- */
// Default to the common /home/steam install. Override with PAVLOV_LOGS in .env
// (comma/colon separated) — REQUIRED if your server lives elsewhere or the bot
// runs as a different user.
const DEFAULT_LOGS = [
  "/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log",
];
const LOG_FILES = process.env.PAVLOV_LOGS
  ? process.env.PAVLOV_LOGS.split(/[,:]/).map(s => s.trim()).filter(Boolean)
  : [...DEFAULT_LOGS];

const REGISTRY_PATH  = path.join(__dirname, "ip_registry.json");
const BLACKLIST_PATH = path.join(__dirname, "ip_blacklist.json");

const POLL_MS             = 5000;
const CORRELATE_WINDOW_MS = 10000;            // join IP must precede the login line by <10s
const AUTO_DEBOUNCE_MS    = 5 * 60 * 1000;    // don't re-auto-ban same id within 5 min
const MAX_BACKFILL_BYTES  = 50 * 1024 * 1024; // first run: only scan the tail of huge logs
const SAVE_THROTTLE_MS    = 3000;             // coalesce registry writes (disconnect lines are frequent)

const TS_RE     = /^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]/;
const ACCEPT_RE = /(?:NotifyAcceptingConnection accepted from:|NotifyAcceptedConnection:.*?RemoteAddr:|AddClientConnection:.*?RemoteAddr:)\s*((?:\d{1,3}\.){3}\d{1,3})/;
const CLOSE_RE  = /RemoteAddr:\s*((?:\d{1,3}\.){3}\d{1,3}):\d+.*?UniqueId:\s*([^\s,]+)/;   // same-line IP + id
const LOGIN_RE  = /Login request:\s*\?Name=([^?]+).*?userId:\s*(\S+)/i;
const BAN_RE    = /Rcon:\s*BanPlayer\s+(\S+)/i;     // any ban (this bot or any admin tool)
const UNBAN_RE  = /Rcon:\s*UnbanPlayer\s+(\S+)/i;   // best-effort unban (mirror of BanPlayer)

const skipId  = id => !id || /INVALID/i.test(id) || /localhost-/i.test(id);   // ignore pre-auth + server self-conn
const cleanId = raw => (raw.includes(":") ? raw.split(":").pop() : raw);      // "NULL:<hex>" -> "<hex>"
const labelFor = f => { const m = f.match(/([^/\\]+)[/\\]Pavlov[/\\]/i); return m ? m[1] : path.basename(path.dirname(f)); };

// Pavlov rotates the active log to "Pavlov-backup-<ts>.log" on every restart and
// starts a fresh, near-empty Pavlov.log. For each configured log we also pull in
// its sibling Pavlov*.log files so the FIRST backfill recovers history from before
// the last restart — otherwise /inspect shows nothing until someone reconnects.
function siblingLogs(f) {
  try {
    const dir = path.dirname(f);
    return fs.readdirSync(dir)
      .filter(n => /^Pavlov.*\.log$/i.test(n))
      .map(n => path.join(dir, n));
  } catch { return [f]; }
}
function mtimeOf(f) { try { return fs.statSync(f).mtimeMs; } catch { return 0; } }

/* ---------------- STATE ---------------- */
// registry: { [uniqueId]: { name, rawUserId, ips: [], lastSeen } }
let registry  = load(REGISTRY_PATH, {});
const blacklist = new Set(load(BLACKLIST_PATH, []));   // set of banned IPs
let onAutoBan = async () => {};
let onConnect = async () => {};   // fired on every LIVE join (name/id/ip/server)
let _live = false;   // false during startup backfill -> suppress feed + auto-ban for old logins

const _recentAuto = new Map();
let watchList = [...LOG_FILES];   // configured logs + discovered backups (set in init)
const offsets  = {};
const leftover = {};
let pendingIP = null, pendingTs = 0, lastTs = 0;
let _saveTimer = null, _dirty = false;

/* ---------------- IO ---------------- */
function load(p, fallback) { try { return JSON.parse(fs.readFileSync(p, "utf8")); } catch { return fallback; } }
function flushRegistry() { _dirty = false; try { fs.writeFileSync(REGISTRY_PATH, JSON.stringify(registry, null, 2)); } catch (e) { console.error("[ipBans] save registry:", e.message); } }
function scheduleSave() { _dirty = true; if (_saveTimer) return; _saveTimer = setTimeout(() => { _saveTimer = null; if (_dirty) flushRegistry(); }, SAVE_THROTTLE_MS); }
function saveBlacklist() { try { fs.writeFileSync(BLACKLIST_PATH, JSON.stringify([...blacklist], null, 2)); } catch (e) { console.error("[ipBans] save blacklist:", e.message); } }

/* ---------------- LOOKUPS ---------------- */
const norm = s => String(s ?? "").trim().toLowerCase();
function resolveIds(input) {
  const key = norm(input);
  if (registry[input]) return [input];
  const idHit = Object.keys(registry).find(id => norm(id) === key);
  if (idHit) return [idHit];
  return Object.keys(registry).filter(id => norm(registry[id].name) === key);
}
function ipsForIds(ids) {
  const set = new Set();
  for (const id of ids) for (const ip of (registry[id]?.ips || [])) set.add(ip);
  return [...set];
}
function altIdsForIps(ips, excludeIds = []) {
  const ex = new Set(excludeIds.map(norm));
  const set = new Set();
  for (const [id, e] of Object.entries(registry))
    if (!ex.has(norm(id)) && e.ips.some(ip => ips.includes(ip))) set.add(id);
  return [...set];
}

/* ---------------- PUBLIC: blacklist / unblacklist ---------------- */
// Flag every IP this player has used. Any account that later connects from one
// of these IPs is auto-banned (via onAutoBan) while the log is being watched.
function blacklistPlayer(input) {
  const ids  = resolveIds(input);
  const ips  = ipsForIds(ids);
  const alts = altIdsForIps(ips, ids);
  let added = 0;
  for (const ip of ips) if (!blacklist.has(ip)) { blacklist.add(ip); added++; }
  if (added) saveBlacklist();
  return {
    ids, ips, alts,
    field: {
      name: "🌐  IP Enforcement",
      value: ips.length
        ? `Flagged **${ips.length}** IP${ips.length !== 1 ? "s" : ""} — any account that connects from them is auto-banned.` +
          (alts.length ? `\n⚠️  Shares an IP with: ${alts.map(a => `\`${a}\``).join("  ·  ")}` : "")
        : "No connection IPs on record yet — nothing to match until this player has connected at least once.",
      inline: false,
    },
  };
}
function unblacklistPlayer(input) {
  const ids = resolveIds(input);
  const ips = ipsForIds(ids);
  let removed = 0;
  for (const ip of ips) if (blacklist.has(ip)) { blacklist.delete(ip); removed++; }
  if (removed) saveBlacklist();
  return { ids, ips };
}

/* ---------------- LOG PARSING ---------------- */
function parseTs(line) {
  const m = line.match(TS_RE);
  if (!m) return null;
  const [, Y, Mo, D, H, Mi, S, ms] = m;
  return Date.UTC(+Y, +Mo - 1, +D, +H, +Mi, +S, +ms);
}

// returns true if something new (ip/name/id) was learned
function record(uniqueId, rawUserId, name, ip, ts) {
  if (!uniqueId) return false;
  const fresh = !registry[uniqueId];
  const e = registry[uniqueId] || { name: null, rawUserId, ips: [], lastSeen: 0 };
  let changed = fresh;
  if (name && name !== "<null>" && e.name !== name) { e.name = name; changed = true; }
  if (rawUserId && e.rawUserId !== rawUserId) { e.rawUserId = rawUserId; changed = true; }
  if (ip && !e.ips.includes(ip)) { e.ips.push(ip); changed = true; }
  e.lastSeen = Math.max(e.lastSeen, ts || Date.now());
  registry[uniqueId] = e;
  if (changed) scheduleSave();
  return changed;
}

async function handleLogin(name, rawId, ip, ts, server) {
  if (skipId(rawId)) return;                       // server self-connection / pre-auth
  const id = cleanId(rawId);
  record(id, rawId, name, ip, ts);
  if (!_live) return;                              // startup backfill — don't feed/auto-ban old joins
  const display = (name && name !== "<null>") ? name : id;

  // live connection feed (every join)
  try { await onConnect({ uniqueId: id, name: display, ip: ip || null, server }); }
  catch (e) { console.error("[ipBans] onConnect failed:", e.message); }

  if (ip && blacklist.has(ip)) {                   // connected from a flagged IP -> auto-ban
    const last = _recentAuto.get(id) ?? 0;
    if (Date.now() - last >= AUTO_DEBOUNCE_MS) {
      _recentAuto.set(id, Date.now());
      try { await onAutoBan({ uniqueId: id, name: display, ip, server }); }
      catch (e) { console.error("[ipBans] onAutoBan failed:", e.message); _recentAuto.delete(id); }
    }
  }
}

function parseLine(line, server) {
  const t = parseTs(line);
  if (t != null) lastTs = t;
  const ts = t ?? lastTs;

  // 1) join-time accept line -> remember IP for the upcoming login line
  const a = line.match(ACCEPT_RE);
  if (a) { pendingIP = a[1]; pendingTs = ts; return; }

  // 2) login line -> name + id (+ correlated join IP), and auto-ban check
  const m = line.match(LOGIN_RE);
  if (m) {
    const ip = (ts - pendingTs <= CORRELATE_WINDOW_MS) ? pendingIP : null;
    pendingIP = null;
    handleLogin(m[1].trim(), m[2], ip, ts, server);
    return;
  }

  // 3) disconnect/cleanup line -> same-line IP + id (strongest pairing)
  const c = line.match(CLOSE_RE);
  if (c) { const raw = c[2]; if (!skipId(raw)) record(cleanId(raw), raw, null, c[1], ts); return; }

  // 4) admin ban/unban line (any tool) -> flag/clear that player's IPs so the
  //    live watcher catches alts. Only act on LIVE lines, not the backfill.
  if (!_live) return;
  const b = line.match(BAN_RE);
  if (b) {
    const r = blacklistPlayer(b[1]);
    if (r.ips.length) console.log(`[ipBans] ban detected for "${b[1]}" — flagged ${r.ips.length} IP(s)${r.alts.length ? `, alts: ${r.alts.join(", ")}` : ""}`);
    return;
  }
  const u = line.match(UNBAN_RE);
  if (u) { unblacklistPlayer(u[1]); return; }
}

function poll() {
  for (const f of watchList) {
    let st; try { st = fs.statSync(f); } catch { continue; }
    let from = offsets[f];
    if (from === undefined) from = Math.max(0, st.size - MAX_BACKFILL_BYTES);
    if (st.size < from) { from = 0; leftover[f] = ""; }   // log rotated/truncated
    if (st.size === from) { offsets[f] = st.size; continue; }
    let buf;
    try {
      const fd = fs.openSync(f, "r");
      buf = Buffer.alloc(st.size - from);
      fs.readSync(fd, buf, 0, buf.length, from);
      fs.closeSync(fd);
    } catch { continue; }
    offsets[f] = st.size;
    const text  = (leftover[f] || "") + buf.toString("utf8");
    const lines = text.split(/\r?\n/);
    leftover[f] = lines.pop();   // last element is an incomplete line — buffer it
    const label = labelFor(f);
    pendingIP = null;            // don't let one file's dangling accept IP leak into another's login
    for (const l of lines) if (l) parseLine(l, label);
  }
  if (_dirty) flushRegistry();
}

/* ---------------- INIT ---------------- */
function init(opts = {}) {
  if (typeof opts.onAutoBan === "function") onAutoBan = opts.onAutoBan;
  if (typeof opts.onConnect === "function") onConnect = opts.onConnect;
  if (Array.isArray(opts.logFiles) && opts.logFiles.length) { LOG_FILES.length = 0; LOG_FILES.push(...opts.logFiles); }
  // report each configured path's status up front so misconfig is obvious
  for (const f of LOG_FILES) {
    try { const st = fs.statSync(f); console.log(`[ipBans] log OK  (${(st.size / 1048576).toFixed(1)} MB): ${f}`); }
    catch (e) { console.warn(`[ipBans] log MISSING/unreadable: ${f} — ${e.code || e.message}. Set PAVLOV_LOGS in .env.`); }
  }

  // Expand each configured log into itself + its sibling Pavlov*.log backups so
  // the initial backfill recovers history from before the last server restart.
  // Process oldest -> newest (by mtime) so the latest name/lastSeen wins.
  const expanded = new Set();
  for (const f of LOG_FILES) for (const s of siblingLogs(f)) expanded.add(s);
  for (const f of LOG_FILES) expanded.add(f);   // ensure configured paths are present even if dir read failed
  watchList = [...expanded].sort((a, b) => mtimeOf(a) - mtimeOf(b));
  const backups = watchList.filter(f => !LOG_FILES.includes(f));
  if (backups.length) console.log(`[ipBans] also backfilling ${backups.length} rotated log(s): ${backups.map(b => path.basename(b)).join(", ")}`);

  poll();          // backfill the existing tail — _live is false, so no auto-ban for old joins
  _live = true;    // everything parsed from here on is a live event
  console.log(`[ipBans] ready — ${Object.keys(registry).length} known IDs, ${blacklist.size} flagged IPs. Live watching for joins (every ${(opts.pollMs || POLL_MS) / 1000}s).`);
  return setInterval(poll, opts.pollMs || POLL_MS);
}

module.exports = {
  init,
  blacklistPlayer,
  unblacklistPlayer,
  resolveIds,
  ipsForIds,
  getIPsForPlayer: (input) => ipsForIds(resolveIds(input)),
  getAltsOf:       (input) => altIdsForIps(ipsForIds(resolveIds(input)), resolveIds(input)),
  registry,
  get blacklist() { return [...blacklist]; },
};
