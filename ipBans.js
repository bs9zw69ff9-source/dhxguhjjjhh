/* ================================================================
 * ipBans.js — IP ↔ player mapping, connection feed, and IP-match auto-ban
 * ================================================================
 * Tails the Pavlov dedicated-server log(s), learns which IP each unique-id has
 * connected from, and uses that to:
 *   • feed every live join to the bot (name · id · ip)            -> onConnect
 *   • flag a banned player's IPs and auto-ban any account that
 *     later connects from one of them (alt / ban-evader catching) -> onAutoBan
 *
 * It learns IP↔id from TWO sources, the first of which is rock-solid:
 *   1. disconnect lines  — RemoteAddr + UniqueId on the SAME line (no guessing)
 *        UChannel::Close / UNetConnection::Close / PendingConnectionLost
 *   2. join correlation  — an "accepted from: <ip>" line, then a later
 *        "Login request: ?Name=.. userId: <id>" line within a few seconds.
 *
 * LOG PATH: set PAVLOV_LOGS in .env to be explicit, e.g.
 *   PAVLOV_LOGS=/home/steam/pavlovserver/Pavlov/Saved/Logs/Pavlov.log
 * If unset, the module AUTO-DETECTS the active Pavlov.log under the usual
 * locations (/home/*, /root, /opt, /srv). Run `node ipBans.js` to self-test.
 * ================================================================ */

"use strict";
const fs   = require("fs");
const path = require("path");

/* ---------------- CONFIG ---------------- */
const REGISTRY_PATH  = path.join(__dirname, "ip_registry.json");
const BLACKLIST_PATH = path.join(__dirname, "ip_blacklist.json");
const LOG_TAIL       = path.join("Pavlov", "Saved", "Logs", "Pavlov.log");
const DEFAULT_LOG    = path.join("/home/steam/pavlovserver", LOG_TAIL);

const POLL_MS             = 5000;
const CORRELATE_WINDOW_MS = 10000;            // join IP must precede the login line by < this
const JOIN_DEBOUNCE_MS    = 20000;            // one feed message per join (collapse INVALID+auth pair)
const AUTO_DEBOUNCE_MS    = 5 * 60 * 1000;    // don't re-auto-ban the same id within 5 min
const MAX_BACKFILL_BYTES  = 50 * 1024 * 1024; // first pass: only scan the tail of huge logs
const SAVE_THROTTLE_MS    = 3000;             // coalesce registry writes (disconnect lines are frequent)

/* ---------------- REGEXES (validated against real Pavlov logs) ---------------- */
const TS_RE     = /^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]/;
const ACCEPT_RE = /(?:NotifyAcceptingConnection accepted from:|NotifyAcceptedConnection:.*?RemoteAddr:|AddClientConnection:.*?RemoteAddr:)\s*((?:\d{1,3}\.){3}\d{1,3})/;
const CLOSE_RE  = /RemoteAddr:\s*((?:\d{1,3}\.){3}\d{1,3}):\d+.*?UniqueId:\s*([^\s,]+)/;   // same-line IP + id
const LOGIN_RE  = /Login request:\s*\?Name=([^?]+).*?userId:\s*(\S+)/i;                    // name + id
const BAN_RE    = /Rcon:\s*BanPlayer\s+(\S+)/i;
const UNBAN_RE  = /Rcon:\s*UnbanPlayer\s+(\S+)/i;

/* ---------------- small helpers ---------------- */
const norm     = s => String(s ?? "").trim().toLowerCase();
const cleanId  = raw => (raw && raw.includes(":") ? raw.split(":").pop() : raw);   // "NULL:<hex>" -> "<hex>"
const skipId   = id => !id || /INVALID/i.test(id) || /localhost-/i.test(id);       // pre-auth / server self-conn
const labelFor = f => { const m = String(f).match(/([^/\\]+)[/\\]Pavlov[/\\]/i); return m ? m[1] : path.basename(path.dirname(f)); };
const mtimeOf  = f => { try { return fs.statSync(f).mtimeMs; } catch { return 0; } };
const exists   = f => { try { return fs.existsSync(f); } catch { return false; } };
function loadJSON(p, fallback) { try { return JSON.parse(fs.readFileSync(p, "utf8")); } catch { return fallback; } }

/* ---------------- STATE ---------------- */
// registry: { [hexId]: { name, ips: string[], firstSeen, lastSeen } }
const registry  = loadJSON(REGISTRY_PATH, {});
const flagged   = new Set(loadJSON(BLACKLIST_PATH, []));   // flagged IPs

let onAutoBan = async () => {};
let onConnect = async () => {};
let live      = false;            // false during the startup backfill (suppress feed + auto-ban)
let watchList = [];               // resolved log files (active + rotated backups)

const offsets     = {};           // per-file byte offset
const leftover    = {};           // per-file buffered partial last line
const pendingIP   = {};           // per-file: IP from the latest accept line, awaiting its login
const pendingTs   = {};           // per-file: log-timestamp of that accept line
const recentJoin  = new Map();    // name  -> ts  (feed dedupe)
const recentAuto  = new Map();    // id    -> ts  (auto-ban dedupe)
let lastTs = 0, saveTimer = null, dirty = false;

/* ---------------- persistence ---------------- */
function flushRegistry() { dirty = false; try { fs.writeFileSync(REGISTRY_PATH, JSON.stringify(registry, null, 2)); } catch (e) { console.error("[ipBans] save registry:", e.message); } }
function scheduleSave()  { dirty = true; if (saveTimer) return; saveTimer = setTimeout(() => { saveTimer = null; if (dirty) flushRegistry(); }, SAVE_THROTTLE_MS); }
function saveFlagged()   { try { fs.writeFileSync(BLACKLIST_PATH, JSON.stringify([...flagged], null, 2)); } catch (e) { console.error("[ipBans] save blacklist:", e.message); } }

/* ---------------- lookups ---------------- */
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
    if (!ex.has(norm(id)) && (e.ips || []).some(ip => ips.includes(ip))) set.add(id);
  return [...set];
}

/* ---------------- record an observation ---------------- */
function record(id, name, ip, ts) {
  if (skipId(id)) return;
  id = cleanId(id);
  const fresh = !registry[id];
  const e = registry[id] || { name: null, ips: [], firstSeen: ts || Date.now(), lastSeen: 0 };
  let changed = fresh, newIp = false;
  if (name && name !== "<null>" && e.name !== name) { e.name = name; changed = true; }
  if (ip && !e.ips.includes(ip)) { e.ips.push(ip); changed = true; newIp = true; }
  e.lastSeen = Math.max(e.lastSeen || 0, ts || Date.now());
  registry[id] = e;
  if (changed) scheduleSave();
  if (newIp && live) console.log(`[ipBans] learned ${e.name || id} [${id}] @ ${ip}`);
}

/* ---------------- public: flag / unflag a player's IPs ---------------- */
function blacklistPlayer(input) {
  const ids  = resolveIds(input);
  const ips  = ipsForIds(ids);
  const alts = altIdsForIps(ips, ids);
  let added = 0;
  for (const ip of ips) if (!flagged.has(ip)) { flagged.add(ip); added++; }
  if (added) saveFlagged();
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
  for (const ip of ips) if (flagged.has(ip)) { flagged.delete(ip); removed++; }
  if (removed) saveFlagged();
  return { ids, ips };
}

/* ---------------- log parsing ---------------- */
function parseTs(line) {
  const m = line.match(TS_RE);
  if (!m) return null;
  const [, Y, Mo, D, H, Mi, S, ms] = m;
  return Date.UTC(+Y, +Mo - 1, +D, +H, +Mi, +S, +ms);
}

async function handleJoin(name, rawId, ip, ts, server) {
  if (/localhost-/i.test(rawId || "")) return;     // server self-connection
  const valid = !skipId(rawId);
  const id    = valid ? cleanId(rawId) : null;
  if (valid) record(rawId, name, ip, ts);          // registry + auto-ban need a real id
  if (!live) return;                               // startup backfill: don't feed/auto-ban old joins

  const display = (name && name !== "<null>") ? name : (id || "unknown");

  // connection feed — once per join (Pavlov logs an INVALID pre-auth line + the authed one)
  const key  = norm(display);
  if (Date.now() - (recentJoin.get(key) ?? 0) >= JOIN_DEBOUNCE_MS) {
    recentJoin.set(key, Date.now());
    try { await onConnect({ uniqueId: id, name: display, ip: ip || null, server }); }
    catch (e) { console.error("[ipBans] onConnect failed:", e.message); }
  }

  // auto-ban if this join came from a flagged IP
  if (valid && ip && flagged.has(ip) && Date.now() - (recentAuto.get(id) ?? 0) >= AUTO_DEBOUNCE_MS) {
    recentAuto.set(id, Date.now());
    try { await onAutoBan({ uniqueId: id, name: display, ip, server }); }
    catch (e) { console.error("[ipBans] onAutoBan failed:", e.message); recentAuto.delete(id); }
  }
}

function parseLine(line, server, key) {
  const t  = parseTs(line);
  if (t != null) lastTs = t;
  const ts = t ?? lastTs;

  // 1) disconnect line — IP + real id on the SAME line (most reliable; no live gate needed)
  const c = line.match(CLOSE_RE);
  if (c && !skipId(c[2])) { record(c[2], null, c[1], ts); return; }

  // 2) accept line — remember the IP for the upcoming login (per file, survives across polls)
  const a = line.match(ACCEPT_RE);
  if (a) { pendingIP[key] = a[1]; pendingTs[key] = ts; return; }

  // 3) login line — name + id, correlated with the most recent accept IP
  const m = line.match(LOGIN_RE);
  if (m) {
    const ip = (ts - (pendingTs[key] ?? 0) <= CORRELATE_WINDOW_MS) ? (pendingIP[key] ?? null) : null;
    pendingIP[key] = null;
    handleJoin(m[1].trim(), m[2], ip, ts, server);
    return;
  }

  // 4) ban/unban from ANY admin tool — flag/clear that player's IPs (live only)
  if (!live) return;
  const b = line.match(BAN_RE);
  if (b) { const r = blacklistPlayer(b[1]); if (r.ips.length) console.log(`[ipBans] ban detected "${b[1]}" — flagged ${r.ips.length} IP(s)${r.alts.length ? `, alts: ${r.alts.join(", ")}` : ""}`); return; }
  const u = line.match(UNBAN_RE);
  if (u) { unblacklistPlayer(u[1]); }
}

/* ---------------- polling reader ---------------- */
function poll() {
  for (const f of watchList) {
    let st; try { st = fs.statSync(f); } catch { continue; }
    let from = offsets[f];
    if (from === undefined) from = Math.max(0, st.size - MAX_BACKFILL_BYTES);
    if (st.size < from) { from = 0; leftover[f] = ""; }   // rotated / truncated
    if (st.size === from) { offsets[f] = st.size; continue; }
    let buf;
    try {
      const fd = fs.openSync(f, "r");
      buf = Buffer.alloc(st.size - from);
      fs.readSync(fd, buf, 0, buf.length, from);
      fs.closeSync(fd);
    } catch { continue; }
    offsets[f] = st.size;
    const lines = ((leftover[f] || "") + buf.toString("utf8")).split(/\r?\n/);
    leftover[f] = lines.pop();          // last element is an incomplete line — buffer it
    const label = labelFor(f);
    for (const l of lines) if (l) parseLine(l, label, f);
  }
  if (dirty) flushRegistry();
}

/* ---------------- log discovery ---------------- */
function listDirs(p) {
  try { return fs.readdirSync(p, { withFileTypes: true }).filter(d => d.isDirectory()).map(d => path.join(p, d.name)); }
  catch { return []; }
}
// Find active Pavlov.log(s) by probing the fixed tail under a few likely roots
// (≤2 wildcard levels — no full-filesystem walk). Newest first, capped.
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

  const { files, how } = resolveLogFiles(opts);
  console.log(`[ipBans] log source: ${how}`);
  for (const f of files) {
    try { const st = fs.statSync(f); console.log(`[ipBans] log OK  (${(st.size / 1048576).toFixed(1)} MB): ${f}`); }
    catch (e) { console.warn(`[ipBans] log MISSING/unreadable: ${f} — ${e.code || e.message}. Set PAVLOV_LOGS in .env.`); }
  }

  // expand each log into itself + sibling rotated logs, oldest -> newest so the latest name/lastSeen wins
  const expanded = new Set();
  for (const f of files) { for (const s of siblingLogs(f)) expanded.add(s); expanded.add(f); }
  watchList = [...expanded].sort((a, b) => mtimeOf(a) - mtimeOf(b));
  const backups = watchList.filter(f => !files.includes(f));
  if (backups.length) console.log(`[ipBans] also backfilling ${backups.length} rotated log(s): ${backups.map(b => path.basename(b)).join(", ")}`);

  live = false; poll();              // backfill (no feed / no auto-ban for old joins)
  live = true;                       // everything from here on is a live event
  console.log(`[ipBans] ready — ${Object.keys(registry).length} known IDs, ${flagged.size} flagged IPs. Watching ${watchList.length} file(s) every ${(opts.pollMs || POLL_MS) / 1000}s.`);
  return setInterval(poll, opts.pollMs || POLL_MS);
}

/* ---------------- exports ---------------- */
module.exports = {
  init,
  blacklistPlayer,
  unblacklistPlayer,
  resolveIds,
  ipsForIds,
  getIPsForPlayer: (input) => ipsForIds(resolveIds(input)),
  getAltsOf:       (input) => altIdsForIps(ipsForIds(resolveIds(input)), resolveIds(input)),
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
  console.log("uid", process.getuid ? process.getuid() : "?", " · source:", how, " · PAVLOV_LOGS:", process.env.PAVLOV_LOGS || "(unset)");
  console.log(bar);
  for (const f of files) {
    console.log(`\n### ${f}`);
    let st; try { st = fs.statSync(f); } catch (e) { console.log(`  ✗ cannot stat: ${e.code || e.message}`); continue; }
    console.log(`  ✓ ${(st.size / 1048576).toFixed(2)} MB, modified ${st.mtime.toISOString()}`);
    try { fs.accessSync(f, fs.constants.R_OK); console.log("  ✓ readable"); } catch { console.log("  ✗ NOT readable by this user"); continue; }
    const tail = (() => { const from = Math.max(0, st.size - 2 * 1024 * 1024); const fd = fs.openSync(f, "r"); const b = Buffer.alloc(st.size - from); fs.readSync(fd, b, 0, b.length, from); fs.closeSync(fd); return b.toString("utf8").split(/\r?\n/); })();
    let nA = 0, nL = 0, nC = 0, nB = 0;
    for (const l of tail) { if (ACCEPT_RE.test(l)) nA++; if (LOGIN_RE.test(l)) nL++; if (CLOSE_RE.test(l)) nC++; if (BAN_RE.test(l)) nB++; }
    console.log(`  matches in last ${tail.length} lines:  accept=${nA}  login=${nL}  close(IP+id)=${nC}  ban=${nB}`);
    if (!nL && !nC) console.log("  ⚠ no join/disconnect lines matched — wrong/empty log, or unexpected format.");
  }
  console.log(`\n${bar}\nRunning backfill…\n${bar}`);
  clearInterval(init({ logFiles: files, pollMs: 9e8 }));
  const ids = Object.keys(registry), withIp = ids.filter(id => (registry[id].ips || []).length);
  console.log(`\nregistry: ${ids.length} players, ${withIp.length} with an IP`);
  withIp.slice(0, 25).forEach(id => console.log(`  ${registry[id].name || "?"}  [${id}]  ->  ${registry[id].ips.join(", ")}`));
  console.log(`\n${bar}\n${withIp.length ? "RESULT: IPs ARE being captured." : "RESULT: no IPs captured — paste this whole output back."}\n${bar}`);
}
