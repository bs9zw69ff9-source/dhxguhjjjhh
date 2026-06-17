/* Copyright (c) 2026 bs9zw69ff9-source. All rights reserved. Proprietary — see LICENSE. */
/* ================================================================
   MOJAVE AUTHORITY — Companion Web App (zero-dependency, public)
   ================================================================
   Serves the dashboard (web/public) and a small public JSON API that reads
   the SAME data the Discord bot uses (data files + live RCON). Everything
   here is public — no login, no moderation tooling.

   Pages:  Servers · Leaderboard · Factions · Courier Lookup
   Run:    npm run web        (defaults to http://localhost:8080)
   ================================================================ */
try { require("dotenv").config(); } catch { /* dotenv optional — real env vars still work */ }
const http   = require("http");
const net    = require("net");
const crypto = require("crypto");
const fs     = require("fs");
const path   = require("path");

/* ---------------- config ---------------- */
const PORT     = Number(process.env.WEB_PORT || 8080);
const DATA_DIR = process.env.DATA_DIR || process.cwd();           // where the bot writes its JSON
const PUBLIC   = path.join(__dirname, "public");
const BASE_URL = (process.env.WEB_BASE_URL || `http://localhost:${PORT}`).replace(/\/$/, "");
const MODSAVE  = process.env.MODSAVE_PATH || "";
const DONATOR_FILE = process.env.DONATOR_PATH
  || "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/donator.txt";
const FACTION_ROLES_PATH = process.env.FACTION_ROLES_PATH
  || "/home/steam/pavlovserver/Pavlov/Saved/Config/ModSave/FactionRoles";

/* Compact mirror of the bot's faction layout (read-only display). */
const FACTIONS = {
  "NCR":                  { spawn: "ncrspawn.txt",     order: ["Private","Corporal","Sergeant","Medic","Heavy","MP","Ranger","Lieutenant","Officer"], badge: { Private:"🪖",Corporal:"⚔️",Sergeant:"🎖️",Medic:"💉",Heavy:"🛡️",MP:"🔒",Ranger:"🎯",Lieutenant:"🌟",Officer:"👑" } },
  "Legion":               { spawn: "legionspawn.txt",  order: ["Recruit","Legionnaire","Veteran Legionnaire","Legate"], badge: { Recruit:"🪖",Legionnaire:"⚔️","Veteran Legionnaire":"🎖️",Legate:"👑" } },
  "Enclave":              { spawn: "enclavespawn.txt", order: ["Low Rank","Mid Rank","High Rank"], badge: { "Low Rank":"🪖","Mid Rank":"⚔️","High Rank":"👑" } },
  "Khans":                { spawn: "khansspawn.txt",   order: ["Low Rank","High Rank"], badge: { "Low Rank":"🪖","High Rank":"👑" } },
  "Brotherhood of Steel": { spawn: "bosspawn.txt",     order: ["Initiate","Knight","Paladin","Elder"], badge: { Initiate:"🪖",Knight:"⚔️",Paladin:"🎖️",Elder:"👑" } },
};

/* ---------------- small utils ---------------- */
const log = (tag, msg) => console.log(`[${new Date().toISOString()}] [web] [${tag}] ${msg}`);
const md5 = (t) => crypto.createHash("md5").update(t).digest("hex");
const sanitizeId = (raw) => String(raw ?? "").trim().replace(/[^a-zA-Z0-9_\-.]/g, "").slice(0, 64);
function readJSON(file, fallback) { try { return JSON.parse(fs.readFileSync(path.join(DATA_DIR, file), "utf8")); } catch { return fallback; } }
function readLines(absFile) { try { return fs.readFileSync(absFile, "utf8").split(/\r?\n/).map(s => s.trim()).filter(Boolean); } catch { return null; } }

/* ---------------- RCON (Pavlov md5 handshake) ---------------- */
function serverConf(server) {
  const n = server === "server2" ? "2" : "1";
  return { host: process.env[`RCON_HOST_${n}`], port: Number(process.env[`RCON_PORT_${n}`]), password: process.env[`RCON_PASSWORD_${n}`] };
}
function rcon(command, server = "server1", timeoutMs = 2500) {
  const { host, port, password } = serverConf(server);
  return new Promise((resolve, reject) => {
    if (!host || !port) return reject(new Error("not configured"));
    const sock = new net.Socket();
    let res = "", authed = false, done = false;
    const finish = (fn, v) => { if (done) return; done = true; clearTimeout(t); try { sock.destroy(); } catch {} fn(v); };
    const t = setTimeout(() => finish(resolve, res), timeoutMs);
    sock.setTimeout(timeoutMs);
    sock.on("data", (d) => {
      const s = d.toString();
      if (s.includes("Password:")) return void sock.write(md5(password));
      if (s.includes("Authenticated=1") && !authed) { authed = true; return void sock.write(command + "\n"); }
      res += s;
    });
    sock.on("timeout", () => finish(resolve, res));
    sock.on("error", (e) => finish(reject, e));
    sock.on("close", () => finish(resolve, res));
    sock.connect(port, host);
  });
}
function parseRcon(raw) {
  if (!raw) return null;
  try { const a = raw.indexOf("{"), b = raw.lastIndexOf("}"); return a < 0 || b < 0 ? null : JSON.parse(raw.slice(a, b + 1)); }
  catch { return null; }
}
const configuredServers = () => (process.env.RCON_HOST_2 ? ["server1", "server2"] : ["server1"]);
function namesFromList(data) {
  return (data?.PlayerList ?? []).map(p => String(p.Username ?? p.username ?? p.PlayerName ?? p.name ?? p.Name ?? "").trim()).filter(Boolean);
}

/* ---------------- data aggregation (all public) ---------------- */
async function serverStatus(server) {
  try {
    const [listRaw, infoRaw] = await Promise.all([rcon("RefreshList", server), rcon("ServerInfo", server)]);
    const list = parseRcon(listRaw), info = parseRcon(infoRaw);
    return {
      id: server, label: server === "server2" ? "Server 2" : "Server 1",
      online: !!list?.Successful,
      name: info?.ServerName ?? (server === "server2" ? "Server 2" : "Server 1"),
      map: info?.MapLabel ?? info?.ServerName ?? "Unknown",
      mode: info?.GameMode ?? "Unknown",
      maxPlayers: info?.MaxPlayers ?? null,
      players: namesFromList(list),
    };
  } catch {
    return { id: server, label: server === "server2" ? "Server 2" : "Server 1", online: false, name: server === "server2" ? "Server 2" : "Server 1", map: "—", mode: "—", maxPlayers: null, players: [] };
  }
}

function balanceOf(id) {
  if (!MODSAVE) return null;
  try { const n = parseInt(fs.readFileSync(path.join(MODSAVE, sanitizeId(id) + ".txt"), "utf8").trim(), 10); return Number.isNaN(n) ? null : n; }
  catch { return null; }
}
function leaderboard(limit = 30) {
  if (!MODSAVE) return null;
  const out = [];
  try {
    for (const f of fs.readdirSync(MODSAVE).filter(f => f.endsWith(".txt"))) {
      try { const n = parseInt(fs.readFileSync(path.join(MODSAVE, f), "utf8").trim(), 10); if (!Number.isNaN(n)) out.push({ id: path.basename(f, ".txt"), balance: n }); } catch {}
    }
  } catch { return null; }
  return out.sort((a, b) => b.balance - a.balance).slice(0, Math.max(1, Math.min(100, limit)));
}
function isDonator(id) { const v = (readLines(DONATOR_FILE) ?? []); const k = String(id).toLowerCase(); return v.some(l => l.toLowerCase() === k); }

function factionRosters() {
  const ranks = readJSON("faction_ranks.json", {});
  const cfg   = readJSON("faction_config.json", {});
  return Object.entries(FACTIONS).map(([name, f]) => {
    const lines = readLines(path.join(FACTION_ROLES_PATH, f.spawn));
    const fr    = ranks[name] ?? {};
    const cap   = cfg[name]?.cap ?? 50;
    const rankCaps = cfg[name]?.rankCaps ?? {};
    const members = lines === null ? null : lines.map(id => {
      const rank = fr[id.toLowerCase()] ?? f.order[0];
      return { id, rank, badge: f.badge[rank] ?? "❓", weight: f.order.indexOf(rank) };
    }).sort((a, b) => b.weight - a.weight || a.id.localeCompare(b.id));
    return { name, cap, rankCaps, order: f.order, badge: f.badge, unreadable: members === null, count: members ? members.length : 0, members: members ?? [] };
  });
}
function playerFactions(id) {
  const ranks = readJSON("faction_ranks.json", {});
  const k = String(id).toLowerCase();
  const out = [];
  for (const [name, f] of Object.entries(FACTIONS)) {
    const lines = readLines(path.join(FACTION_ROLES_PATH, f.spawn));
    if (lines && lines.some(l => l.toLowerCase() === k)) {
      const rank = ranks[name]?.[k] ?? f.order[0];
      out.push({ faction: name, rank, badge: f.badge[rank] ?? "❓" });
    }
  }
  return out;
}

async function courierProfile(rawId) {
  const id = sanitizeId(rawId);
  if (!id) return null;
  const k = id.toLowerCase();
  // online presence (live)
  let onServers = [];
  await Promise.all(configuredServers().map(async (srv) => {
    try { const d = parseRcon(await rcon("RefreshList", srv)); if (namesFromList(d).some(n => n.toLowerCase() === k)) onServers.push(srv === "server2" ? "Server 2" : "Server 1"); } catch {}
  }));
  const playtime = readJSON("playtime.json", {});
  const minutes = playtime[id] ?? playtime[Object.keys(playtime).find(p => p.toLowerCase() === k)] ?? null;
  const lastSeen = readJSON("lastseen.json", {})[k] ?? null;
  return {
    id, online: onServers.length > 0, onServers,
    playtimeMinutes: minutes,
    lastSeen,
    balance: balanceOf(id),
    donator: isDonator(id),
    factions: playerFactions(id),
  };
}

/* ---------------- http helpers ---------------- */
const MIME = { ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".svg": "image/svg+xml", ".png": "image/png", ".ico": "image/x-icon" };
function sendJSON(res, status, obj) { res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" }); res.end(JSON.stringify(obj)); }
function sendFile(res, file) {
  fs.readFile(file, (err, buf) => {
    if (err) { res.writeHead(404, { "Content-Type": "text/plain" }); return res.end("404"); }
    res.writeHead(200, { "Content-Type": MIME[path.extname(file)] || "application/octet-stream" });
    res.end(buf);
  });
}

/* ---------------- routes ---------------- */
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, BASE_URL);
  const p = url.pathname;
  try {
    if (p === "/api/health") return sendJSON(res, 200, { ok: true });

    if (p === "/api/servers") {
      const list = await Promise.all(configuredServers().map(serverStatus));
      return sendJSON(res, 200, { servers: list, totalOnline: list.reduce((n, s) => n + s.players.length, 0) });
    }

    if (p === "/api/leaderboard") {
      const lb = leaderboard(Number(url.searchParams.get("limit")) || 30);
      return sendJSON(res, 200, { configured: MODSAVE !== "", leaderboard: lb ?? [] });
    }

    if (p === "/api/factions") return sendJSON(res, 200, { factions: factionRosters() });

    if (p === "/api/player") {
      const profile = await courierProfile(url.searchParams.get("id") || "");
      if (!profile) return sendJSON(res, 400, { error: "Provide a courier id/name" });
      return sendJSON(res, 200, profile);
    }

    if (p.startsWith("/api/")) return sendJSON(res, 404, { error: "not found" });

    /* ---- static ---- */
    const pages = { "/": "index.html", "/leaderboard": "leaderboard.html", "/factions": "factions.html", "/lookup": "lookup.html" };
    const rel = pages[p] || p.replace(/^\/+/, "");
    const file = path.join(PUBLIC, rel);
    if (!file.startsWith(PUBLIC)) { res.writeHead(403); return res.end("forbidden"); }
    return sendFile(res, file);
  } catch (err) {
    log("error", `${p}: ${err.message}`);
    return sendJSON(res, 500, { error: "internal error" });
  }
});

if (require.main === module) {
  server.listen(PORT, () => log("boot", `Mojave Authority web up on ${BASE_URL}  (data: ${DATA_DIR})`));
}

module.exports = { server, serverStatus, leaderboard, factionRosters, playerFactions, courierProfile, balanceOf, isDonator };
