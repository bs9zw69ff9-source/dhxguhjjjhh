/* ================================================================
 * ipBans.js — IP player mapping, connection feed, and IP-match auto-ban
 * ================================================================
 * Tails the Pavlov dedicated-server log(s), learns which IP each unique-id has
 * connected from, and uses that to:
 *   • feed every live join to the bot (name · id · ip)            -> onConnect
 *   • flag a banned player's IPs and auto-ban any account that
 *     later connects from one of them (alt / ban-evader catching) -> onAutoBan
 *
 * It learns IPid from TWO sources, the first of which is rock-solid:
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
const BLACKLIST_PATH = path.join(__dirname, "ip_blacklist.json");      // flagged IPs
const FNAMES_PATH    = path.join(__dirname, "ip_flagged_names.json");  // flagged usernames
const UNTRACKED_PATH = path.join(__dirname, "ip_untracked.json");   // usernames to never track
const LOG_TAIL       = path.join("Pavlov", "Saved", "Logs", "Pavlov.log");
const DEFAULT_LOG    = path.join("/home/steam/pavlovserver", LOG_TAIL);

const POLL_MS             = 5000;
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
const CLOSE_RE  = /RemoteAddr:\s*((?:\d{1,3}\.){3}\d{1,3}):\d+.*?UniqueId:\s*([^\s,]+)/;   // same-line IP + id
const LOGIN_RE  = /Login request:\s*\?Name=([^?]+).*?userId:\s*(\S+)/i;                    // name + id
const BAN_RE    = /Rcon:\s*BanPlayer\s+(\S+)/i;
const UNBAN_RE  = /Rcon:\s*UnbanPlayer\s+(\S+)/i;

/* ---------------- small helpers ---------------- */
const norm     = s => String(s ?? "").trim().toLowerCase();
// "NULL:<hex>" -> "<hex>", and strip any stray non-alphanumerics (e.g. a trailing
// "'" captured from quoted NetworkFailure/error log lines).
const cleanId  = raw => { const s = raw == null ? "" : String(raw); const p = s.includes(":") ? s.split(":").pop() : s; return p.replace(/[^a-z0-9]/gi, ""); };
const skipId   = id => !id || /INVALID/i.test(id) || /localhost-/i.test(id);       // pre-auth / server self-conn
const labelFor = f => { const m = String(f).match(/([^/\\]+)[/\\]Pavlov[/\\]/i); return m ? m[1] : path.basename(path.dirname(f)); };
const mtimeOf  = f => { try { return fs.statSync(f).mtimeMs; } catch { return 0; } };
const exists   = f => { try { return fs.existsSync(f); } catch { return false; } };
function loadJSON(p, fallback) { try { return JSON.parse(fs.readFileSync(p, "utf8")); } catch { return fallback; } }

/* ---------------- STATE ---------------- */
// registry: { [hexId]: { name, ips: string[], firstSeen, lastSeen } }
const registry  = loadJSON(REGISTRY_PATH, {});
const flagged      = new Set(loadJSON(BLACKLIST_PATH, []));   // flagged IPs
const flaggedNames = new Set((loadJSON(FNAMES_PATH, []) || []).map(s => String(s).trim().toLowerCase())); // flagged usernames
const untrackedNames = new Set((loadJSON(UNTRACKED_PATH, []) || []).map(s => String(s).trim().toLowerCase())); // usernames excluded from tracking
const untrackedIds   = new Set();   // runtime: ids resolved to belong to an untracked name

let onAutoBan = async () => {};
let onConnect = async () => {};   // best-effort join (tentative IP)
let onConfirm = async () => {};   // confirmed IPid pairing (same-line disconnect) — accurate IP
let live      = false;            // false during the startup backfill (suppress feed + auto-ban)
let watchList = [];               // resolved log files (active + rotated backups)

const offsets     = {};           // per-file byte offset
const leftover    = {};           // per-file buffered partial last line
const pendingIP   = {};           // per-file: IP from the latest accept line, awaiting its login
const pendingTs   = {};           // per-file: log-timestamp of that accept line
const pendingSet  = {};           // per-file: distinct IPs accepted since the last login (size 1 = unambiguous)
const recentJoin  = new Map();    // name  -> ts  (join feed dedupe)
const recentConfirm = new Map();  // id    -> ts  (confirm feed dedupe — collapse a disconnect's multiple close lines)
const lastCloseTs   = new Map();  // id    -> log ts (count one connection per disconnect, by log time)
const recentAuto  = new Map();    // id    -> ts  (auto-ban dedupe)
const pendingFlag = new Map();    // id    -> ts  (banned with no confirmed IP yet — flag it when the kick confirms one)
let lastTs = 0, saveTimer = null, dirty = false;

/* ---------------- persistence ---------------- */
function flushRegistry() { dirty = false; try { fs.writeFileSync(REGISTRY_PATH, JSON.stringify(registry, null, 2)); } catch (e) { console.error("[ipBans] save registry:", e.message); } }
function scheduleSave()  { dirty = true; if (saveTimer) return; saveTimer = setTimeout(() => { saveTimer = null; if (dirty) flushRegistry(); }, SAVE_THROTTLE_MS); }
function saveFlagged()    { try { fs.writeFileSync(BLACKLIST_PATH, JSON.stringify([...flagged], null, 2)); } catch (e) { console.error("[ipBans] save blacklist:", e.message); } }
function saveFNames()     { try { fs.writeFileSync(FNAMES_PATH, JSON.stringify([...flaggedNames], null, 2)); } catch (e) { console.error("[ipBans] save flagged names:", e.message); } }
function saveUntracked()  { try { fs.writeFileSync(UNTRACKED_PATH, JSON.stringify([...untrackedNames], null, 2)); } catch (e) { console.error("[ipBans] save untracked:", e.message); } }

/* ---------------- lookups ---------------- */
function resolveIds(input) {
  const key = norm(input);
  if (registry[input]) return [input];
  const idHit = Object.keys(registry).find(id => norm(id) === key);
  if (idHit) return [idHit];
  return Object.keys(registry).filter(id => norm(registry[id].name) === key);
}
function ipsForIds(ids) {           // all IPs ever seen (incl. best-effort join correlation) — for display
  const set = new Set();
  for (const id of ids) for (const ip of (registry[id]?.ips || [])) set.add(ip);
  return [...set];
}
function confirmedIpsForIds(ids) { // only same-line (disconnect) IPid pairings — trustworthy, for alts/enforcement
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
    ips: [...ips],
    cips: [...cips],
    firstSeen: firstSeen === Infinity ? null : firstSeen,
    lastSeen: lastSeen || null,
    alts: altNamesForIps([...cips], ids),
    logins,
    recent: recent.slice(0, 8),
    bypass: untrackedNames.has(nm),                                   // ignore-listed (never auto-banned/tracked)
    flagged: [...cips].some(ip => flagged.has(ip)) || flaggedNames.has(nm),
  };
}

/* ---------------- record an observation ---------------- */
// sure = true when the IP and id came from the SAME log line (disconnect) — a
// trustworthy pairing. false = best-effort join-time correlation (display only).
function record(id, name, ip, ts, sure) {
  if (skipId(id)) return;
  id = cleanId(id);
  if (untrackedIds.has(id)) return;                // ignore-listed player — never track
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
    if (pf && Date.now() - pf <= PENDING_FLAG_MS && !flagged.has(ip)) {
      flagged.add(ip); saveFlagged();
      if (live) console.log(`[ipBans] flagged confirmed IP ${ip} for recently-banned ${e.name || id}`);
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
// connects matching either is auto-banned. (No hex ids — Shack bans by name.)
function blacklistPlayer(input) {
  const ids  = resolveIds(input);
  const ips  = confirmedIpsForIds(ids);             // only trustworthy same-line IPs
  const altNames = altNamesForIps(ips, ids);
  let added = 0;
  for (const ip of ips) if (!flagged.has(ip)) { flagged.add(ip); added++; }
  if (added) saveFlagged();
  // remember the player so the IP the kick confirms is flagged too (if none yet).
  // NOTE: we deliberately do NOT auto-flag the username here. A flagged username
  // auto-bans anyone connecting under that name forever, which false-positives on
  // temp bans, kicks, warn-escalations and re-used names ("banned for no reason").
  // Username blacklisting is an explicit, owner-only action (flagTarget).
  for (const id of ids) pendingFlag.set(id, Date.now());
  // banned by a raw token not in the registry: flag it only if it's an IP.
  if (!ids.length) {
    const raw = String(input ?? "").trim();
    if (/^(\d{1,3}\.){3}\d{1,3}$/.test(raw) && !flagged.has(raw)) { flagged.add(raw); added++; saveFlagged(); }
  }
  return {
    ids, ips, alts: altNames,
    field: {
      name: "IP Enforcement",
      value: (ips.length
        ? `Flagged **${ips.length}** IP${ips.length !== 1 ? "s" : ""} — any account from them is auto-banned.`
        : "No connection IPs on record yet.") +
        (altNames.length ? `\nShares an IP with: ${altNames.map(a => `\`${a}\``).join("  ·  ")}` : ""),
      inline: false,
    },
  };
}
function unblacklistPlayer(input) {
  const ids = resolveIds(input);
  const ips = ipsForIds(ids);
  let nIp = 0, nName = 0;
  for (const ip of ips) if (flagged.delete(ip)) nIp++;
  for (const id of ids) {
    const nm = norm(registry[id]?.name);
    if (nm && flaggedNames.delete(nm)) nName++;
    pendingFlag.delete(id);
  }
  // also clear a raw name/ip token (e.g. unbanning by a value not in the registry)
  const raw = String(input ?? "").trim();
  if (flaggedNames.delete(norm(raw))) nName++;
  if (flagged.delete(raw)) nIp++;
  if (nIp) saveFlagged();
  if (nName) saveFNames();
  return { ids, ips, cleared: { ips: nIp, names: nName } };
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
  return { added, ip, ids: idsWithIp(ip) };
}
// Manually blacklist an IP or a username (IPv4 detected by shape; anything else
// is treated as a username). Returns the accounts already on record matching it.
function flagTarget(input) {
  const val = String(input || "").trim();
  if (/^(\d{1,3}\.){3}\d{1,3}$/.test(val)) {            // IPv4
    const added = !flagged.has(val); if (added) { flagged.add(val); saveFlagged(); }
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
function getBlacklist() { return { ips: [...flagged], names: [...flaggedNames] }; }
// Clear ALL flags (IPs + usernames). Stops every auto-ban. Registry kept.
function clearFlags() {
  const n = flagged.size + flaggedNames.size;
  flagged.clear(); flaggedNames.clear();
  saveFlagged(); saveFNames();
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
function clearAll() {
  const ids = Object.keys(registry).length, fl = flagged.size + flaggedNames.size;
  for (const k of Object.keys(registry)) delete registry[k];
  flagged.clear(); flaggedNames.clear();
  flushRegistry(); saveFlagged(); saveFNames();
  return { ids, flagged: fl };
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
    if (norm(e.name) === key) { untrackedIds.add(id); delete registry[id]; purged++; }
  if (purged) scheduleSave();
  return { name: key, purged };
}
function removeUntracked(name) {
  const key = norm(name);
  const had = untrackedNames.delete(key);
  if (had) saveUntracked();
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
  if (name && untrackedNames.has(norm(name))) {    // ignore-listed username — don't track at all
    if (!skipId(rawId)) untrackedIds.add(cleanId(rawId));   // also skip their disconnect lines
    return;
  }
  const valid = !skipId(rawId);
  const id    = valid ? cleanId(rawId) : null;
  if (valid) record(rawId, name, ip, ts, false);   // join correlation = best-effort (tentative)

  if (!live) return;                               // startup backfill: don't feed/auto-ban old joins

  const display = (name && name !== "<null>") ? name : (id || "unknown");

  // connection feed — once per join (Pavlov logs an INVALID pre-auth line + the authed one)
  const key  = norm(display);
  if (Date.now() - (recentJoin.get(key) ?? 0) >= JOIN_DEBOUNCE_MS) {
    recentJoin.set(key, Date.now());
    try { await onConnect({ name: display, ip: ip || null, server }); }
    catch (e) { console.error("[ipBans] onConnect failed:", e.message); }
  }

  // auto-ban if this join matches a flagged username or IP.
  //  • username comes straight from the login line -> reliable, ban on sight.
  //  • IP comes from join correlation -> only when unambiguous (one connection
  //    pending); ambiguous joins are caught later at disconnect via the confirmed IP.
  if (valid && Date.now() - (recentAuto.get(id) ?? 0) >= AUTO_DEBOUNCE_MS) {
    const reason = (name && flaggedNames.has(norm(name))) ? "blacklisted username"
                 : (confident && ip && flagged.has(ip))   ? "blacklisted IP"
                 : null;
    if (reason) {
      recentAuto.set(id, Date.now());
      try { await onAutoBan({ name: display, ip: ip || null, server, reason }); }
      catch (e) { console.error("[ipBans] onAutoBan failed:", e.message); recentAuto.delete(id); }
    }
  }
}

function parseLine(line, server, key) {
  const t  = parseTs(line);
  if (t != null) lastTs = t;
  const ts = t ?? lastTs;

  // 1) disconnect line — IP + real id on the SAME line (most reliable; no live gate needed)
  const c = line.match(CLOSE_RE);
  if (c && !skipId(c[2])) {
    const id = cleanId(c[2]), ip = c[1];
    record(c[2], null, ip, ts, true);          // confirmed same-line pairing
    // count one completed connection per disconnect (a disconnect emits several
    // close lines a few ms apart — collapse them by LOG time so backfill counts too).
    const e = registry[id];
    if (e && (ts - (lastCloseTs.get(id) ?? -Infinity)) > 5000) {
      lastCloseTs.set(id, ts);
      e.logins = (e.logins || 0) + 1;
      e.recent = Array.isArray(e.recent) ? e.recent : [];
      e.recent.unshift({ ts: ts || Date.now(), ip });
      if (e.recent.length > 8) e.recent.length = 8;
      scheduleSave();
    }
    // fire the feed on the CONFIRMED IP (accurate), deduped per id per disconnect.
    // Only when we actually know who this is (a login captured a name) — skip
    // anonymous/partial connections so the feed isn't full of "unknown".
    if (live && registry[id]?.name && !untrackedIds.has(id) && Date.now() - (recentConfirm.get(id) ?? 0) >= CONFIRM_DEBOUNCE_MS) {
      recentConfirm.set(id, Date.now());
      const rec = getRecord(id) || {};   // look up by id, not the (maybe missing) name
      Promise.resolve(onConfirm({ name: registry[id].name, ip, server, record: rec }))
        .catch(e => console.error("[ipBans] onConfirm failed:", e.message));
    }
    // retroactive auto-ban: an alt that slipped the ambiguous join check is caught
    // here with the 100%-accurate IP. Skip the freshly-banned account itself (pendingFlag).
    if (live && flagged.has(ip) && registry[id]?.name && !pendingFlag.has(id) && !untrackedIds.has(id)
        && Date.now() - (recentAuto.get(id) ?? 0) >= AUTO_DEBOUNCE_MS) {
      recentAuto.set(id, Date.now());
      Promise.resolve(onAutoBan({ name: registry[id].name, ip, server, reason: "blacklisted IP" }))
        .catch(e => { console.error("[ipBans] onAutoBan (confirm) failed:", e.message); recentAuto.delete(id); });
    }
    return;
  }

  // 2) accept line — remember the IP for the upcoming login. Track DISTINCT pending
  //    IPs so we can tell an unambiguous join (1) from concurrent joins (>1).
  const a = line.match(ACCEPT_RE);
  if (a) { pendingIP[key] = a[1]; pendingTs[key] = ts; (pendingSet[key] || (pendingSet[key] = new Set())).add(a[1]); return; }

  // 3) login line — name + id, correlated with the most recent accept IP
  const m = line.match(LOGIN_RE);
  if (m) {
    const within    = ts - (pendingTs[key] ?? 0) <= CORRELATE_WINDOW_MS;
    const ip        = within ? (pendingIP[key] ?? null) : null;
    const confident = within && !!pendingSet[key] && pendingSet[key].size === 1;   // exactly one connection pending
    pendingIP[key] = null;
    if (pendingSet[key]) pendingSet[key].clear();
    handleJoin(m[1].trim(), m[2], ip, ts, server, confident);
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
  if (typeof opts.onConfirm === "function") onConfirm = opts.onConfirm;

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
  getIPsForPlayer:          (input) => ipsForIds(resolveIds(input)),            // all (incl. tentative) — display
  getConfirmedIPsForPlayer: (input) => confirmedIpsForIds(resolveIds(input)),   // only same-line confirmed pairings
  getAltsOf:                (input) => altIdsForIps(confirmedIpsForIds(resolveIds(input)), resolveIds(input)),
  getAltNamesOf:            (input) => altNamesForIps(confirmedIpsForIds(resolveIds(input)), resolveIds(input)),
  getRecord,
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
    if (!nL && !nC) console.log("  no join/disconnect lines matched — wrong/empty log, or unexpected format.");
  }
  console.log(`\n${bar}\nRunning backfill…\n${bar}`);
  clearInterval(init({ logFiles: files, pollMs: 9e8 }));
  const ids = Object.keys(registry), withIp = ids.filter(id => (registry[id].ips || []).length);
  console.log(`\nregistry: ${ids.length} players, ${withIp.length} with an IP`);
  withIp.slice(0, 25).forEach(id => console.log(`  ${registry[id].name || "?"}  [${id}]  ->  ${registry[id].ips.join(", ")}`));
  console.log(`\n${bar}\n${withIp.length ? "RESULT: IPs ARE being captured." : "RESULT: no IPs captured — paste this whole output back."}\n${bar}`);
}
